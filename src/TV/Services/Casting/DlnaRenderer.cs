using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using HttpBadRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace TV.Services.Casting;

internal sealed class DlnaRenderer(CastNetwork receiver) : IDisposable
{
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private const string Protocols = "http-get:*:video/mp4:*,http-get:*:video/mpeg:*,http-get:*:video/quicktime:*,http-get:*:video/x-matroska:*,http-get:*:application/vnd.apple.mpegurl:*";
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    private static readonly XNamespace Scpd = "urn:schemas-upnp-org:service-1-0";
    private static readonly string[] Services = ["AVTransport", "RenderingControl", "ConnectionManager"];
    private static readonly string[] Targets = ["upnp:rootdevice", DeviceType, .. Services.Select(ServiceType)];
    private readonly string _uuid = "uuid:" + Guid.NewGuid().ToString("D");
    private UdpClient? _discovery;

    public bool IsRunning => _discovery is not null;

    public void StartDiscovery(IPAddress address, CancellationToken token)
    {
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Multicast.Port));
            socket.JoinMulticastGroup(Multicast.Address, address);
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            _discovery = socket;
            Announce("ssdp:alive");
            _ = DiscoverAsync(socket, token);
        }
        catch (SocketException)
        {
            socket?.Dispose();
            _discovery = null;
        }
    }

    private async Task DiscoverAsync(UdpClient socket, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var datagram = await socket.ReceiveAsync(token).ConfigureAwait(false);
                if (datagram.Buffer.Length > 2048 || !receiver.IsLocal(datagram.RemoteEndPoint.Address)) continue;
                var lines = Encoding.ASCII.GetString(datagram.Buffer).Split("\r\n");
                if (!lines[0].Equals("M-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Header(lines, "ST");
                if (!Header(lines, "MAN").Trim('"').Equals("ssdp:discover", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var reply in SearchTargets(target))
                {
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\nLOCATION: {receiver.BaseAddress}upnp/device.xml\r\n" +
                        $"SERVER: Windows/10 UPnP/1.0 TV/1.0\r\nST: {reply}\r\nUSN: {Usn(reply)}\r\n\r\n");
                    await socket.SendAsync(response, datagram.RemoteEndPoint, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) { Diagnostics.Log($"DLNA 发现结束: {ex.SocketErrorCode}"); }
    }

    private IEnumerable<string> SearchTargets(string target)
    {
        if (target.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase)) return Targets.Append(_uuid);
        var result = Targets.Append(_uuid).FirstOrDefault(item => item.Equals(target, StringComparison.OrdinalIgnoreCase));
        return result is null ? [] : [result];
    }

    private static string Header(string[] lines, string name) => lines
        .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";

    private string Usn(string target) => target == _uuid ? _uuid : $"{_uuid}::{target}";
    private static string ServiceType(string name) => $"urn:schemas-upnp-org:service:{name}:1";

    private void Announce(string status)
    {
        if (_discovery is not { } socket) return;
        foreach (var target in Targets.Append(_uuid))
        {
            var message = Encoding.ASCII.GetBytes($"NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: {target}\r\nNTS: {status}\r\n" +
                $"USN: {Usn(target)}\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: {receiver.BaseAddress}upnp/device.xml\r\nSERVER: Windows/10 UPnP/1.0 TV/1.0\r\n\r\n");
            try { socket.Send(message, Multicast); }
            catch (SocketException) { }
        }
    }

    public async Task<bool> RespondAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path is not ["upnp", _, ..]) return false;
        if (context.Request.Method == "GET" && path is ["upnp", "device.xml"])
            await XmlAsync(context, DeviceDescription()).ConfigureAwait(false);
        else if (path is ["upnp", var service, var route] && Services.Contains(service, StringComparer.Ordinal))
        {
            if (context.Request.Method == "GET" && route == "description.xml")
                await XmlAsync(context, ServiceDescription(service)).ConfigureAwait(false);
            else if (context.Request.Method == "POST" && route == "control")
                await ReadActionAsync(context, service).ConfigureAwait(false);
            else return false;
        }
        else return false;
        return true;
    }

    private XElement DeviceDescription() => new(Device + "root",
        Version(Device),
        new XElement(Device + "device",
            new XElement(Device + "deviceType", DeviceType), new XElement(Device + "friendlyName", receiver.DeviceName),
            new XElement(Device + "manufacturer", "TV"), new XElement(Device + "modelName", "TV"),
            new XElement(Device + "UDN", _uuid), new XElement(Device + "serviceList", Services.Select(name =>
                new XElement(Device + "service", new XElement(Device + "serviceType", ServiceType(name)),
                    new XElement(Device + "serviceId", "urn:upnp-org:serviceId:" + name),
                    new XElement(Device + "SCPDURL", $"/upnp/{name}/description.xml"),
                    new XElement(Device + "controlURL", $"/upnp/{name}/control"),
                    new XElement(Device + "eventSubURL", $"/upnp/{name}/events"))))));

    private static XElement Version(XNamespace ns) => new(ns + "specVersion", new XElement(ns + "major", 1), new XElement(ns + "minor", 0));

    private static XElement ServiceDescription(string service)
    {
        var actions = service switch
        {
            "AVTransport" => new[]
            {
                "SetAVTransportURI:InstanceID,CurrentURI,CurrentURIMetaData", "Play:InstanceID,Speed", "Pause:InstanceID", "Stop:InstanceID",
                "Seek:InstanceID,Unit,Target", "GetTransportInfo:InstanceID;CurrentTransportState,CurrentTransportStatus,CurrentSpeed",
                "GetPositionInfo:InstanceID;Track,TrackDuration,TrackMetaData,TrackURI,RelTime,AbsTime,RelCount,AbsCount",
                "GetMediaInfo:InstanceID;NrTracks,MediaDuration,CurrentURI,CurrentURIMetaData,NextURI,NextURIMetaData,PlayMedium,RecordMedium,WriteStatus",
                "GetDeviceCapabilities:InstanceID;PlayMedia,RecMedia,RecQualityModes",
                "GetTransportSettings:InstanceID;PlayMode,RecQualityMode", "GetCurrentTransportActions:InstanceID;Actions"
            },
            "RenderingControl" => ["GetVolume:InstanceID,Channel;CurrentVolume", "SetVolume:InstanceID,Channel,DesiredVolume",
                "GetMute:InstanceID,Channel;CurrentMute", "SetMute:InstanceID,Channel,DesiredMute"],
            _ => ["GetProtocolInfo:;Source,Sink", "GetCurrentConnectionIDs:;ConnectionIDs",
                "GetCurrentConnectionInfo:ConnectionID;RcsID,AVTransportID,ProtocolInfo,PeerConnectionManager,PeerConnectionID,Direction,Status"]
        };
        var variables = new SortedSet<string>(StringComparer.Ordinal);
        var actionList = new XElement(Scpd + "actionList");
        foreach (var definition in actions)
        {
            var parts = definition.Split([':', ';']);
            var arguments = new XElement(Scpd + "argumentList");
            for (var index = 1; index < parts.Length; index++)
            {
                foreach (var name in parts[index].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    variables.Add(name);
                    arguments.Add(new XElement(Scpd + "argument", new XElement(Scpd + "name", name),
                        new XElement(Scpd + "direction", index == 1 ? "in" : "out"), new XElement(Scpd + "relatedStateVariable", name)));
                }
            }
            var action = new XElement(Scpd + "action", new XElement(Scpd + "name", parts[0]));
            if (arguments.HasElements) action.Add(arguments);
            actionList.Add(action);
        }
        return new XElement(Scpd + "scpd", Version(Scpd), actionList,
            new XElement(Scpd + "serviceStateTable", variables.Select(name => new XElement(Scpd + "stateVariable",
                new XAttribute("sendEvents", "no"), new XElement(Scpd + "name", name), new XElement(Scpd + "dataType", DataType(name))))));
    }

    private static string DataType(string name) => name switch
    {
        "InstanceID" or "Track" or "NrTracks" => "ui4",
        "ConnectionID" or "RcsID" or "AVTransportID" or "PeerConnectionID" or "RelCount" or "AbsCount" => "i4",
        "CurrentVolume" or "DesiredVolume" => "ui2",
        "CurrentMute" or "DesiredMute" => "boolean",
        "CurrentURI" or "NextURI" or "TrackURI" => "uri",
        _ => "string"
    };

    private async Task ReadActionAsync(HttpContext context, string service)
    {
        var requestedAction = context.Request.Headers["SOAPAction"].ToString().Trim().Trim('"');
        if (context.Request.ContentLength is not > 0 or > 65_536 || !requestedAction.StartsWith(ServiceType(service) + "#", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            return;
        }
        try
        {
            using var reader = XmlReader.Create(context.Request.Body, new XmlReaderSettings
            { Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65_536 });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
            var action = document.Root?.Element(Soap + "Body")?.Elements().SingleOrDefault();
            if (action?.Name.NamespaceName != ServiceType(service) || requestedAction != ServiceType(service) + "#" + action.Name.LocalName)
            {
                await FaultAsync(context).ConfigureAwait(false);
                return;
            }
            var response = Invoke(service, action, context.Connection.RemoteIpAddress!);
            if (response is null) await FaultAsync(context).ConfigureAwait(false);
            else await XmlAsync(context, new XElement(Soap + "Envelope", new XElement(Soap + "Body", response))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or HttpBadRequestException)
        { await FaultAsync(context).ConfigureAwait(false); }
    }

    private XElement? Invoke(string service, XElement action, IPAddress sender)
    {
        var name = action.Name.LocalName;
        string Arg(string key) => action.Elements().FirstOrDefault(element => element.Name.LocalName == key)?.Value ?? "";
        XElement Reply(params object[] values) => new(action.Name.Namespace + (name + "Response"), values);
        XElement Field(string key, object value) => new(key, value);
        var state = receiver.Status;
        if (service == "ConnectionManager")
            return name switch
            {
                "GetProtocolInfo" => Reply(Field("Source", ""), Field("Sink", Protocols)),
                "GetCurrentConnectionIDs" => Reply(Field("ConnectionIDs", "0")),
                "GetCurrentConnectionInfo" => Reply(Field("RcsID", 0), Field("AVTransportID", 0), Field("ProtocolInfo", ""),
                    Field("PeerConnectionManager", ""), Field("PeerConnectionID", -1), Field("Direction", "Input"), Field("Status", "OK")),
                _ => null
            };
        if (service == "RenderingControl")
        {
            switch (name)
            {
                case "GetVolume": return Reply(Field("CurrentVolume", state.Volume));
                case "GetMute": return Reply(Field("CurrentMute", state.Muted ? 1 : 0));
                case "SetMute":
                    receiver.Control(sender, CastOperation.Mute, Arg("DesiredMute") is "true" or "1" ? 1 : 0);
                    return Reply();
                case "SetVolume" when int.TryParse(Arg("DesiredVolume"), NumberStyles.None, CultureInfo.InvariantCulture, out var volume):
                    receiver.Control(sender, CastOperation.Volume, Math.Clamp(volume, 0, 100));
                    return Reply();
                default: return null;
            }
        }

        switch (name)
        {
            case "SetAVTransportURI":
                if (!Uri.TryCreate(Arg("CurrentURI"), UriKind.Absolute, out var media) || media.IsLoopback ||
                    media.Scheme is not ("https" or "http") || media.UserInfo.Length != 0) return null;
                receiver.Offer(new CastOffer(Guid.NewGuid(), ReadTitle(Arg("CurrentURIMetaData")), sender, media, null));
                return Reply();
            case "Play": receiver.Control(sender, CastOperation.Play); return Reply();
            case "Pause": receiver.Control(sender, CastOperation.Pause); return Reply();
            case "Stop": receiver.Control(sender, CastOperation.Stop); return Reply();
            case "Seek" when Arg("Unit") is "REL_TIME" or "ABS_TIME" &&
                TimeSpan.TryParse(Arg("Target"), CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero:
                receiver.Control(sender, CastOperation.Seek, time.TotalSeconds);
                return Reply();
            case "GetTransportInfo": return Reply(Field("CurrentTransportState", state.State), Field("CurrentTransportStatus", "OK"), Field("CurrentSpeed", "1"));
            case "GetPositionInfo":
                return Reply(Field("Track", state.Duration > TimeSpan.Zero ? 1 : 0), Field("TrackDuration", Time(state.Duration)),
                    Field("TrackMetaData", ""), Field("TrackURI", ""), Field("RelTime", Time(state.Position)),
                    Field("AbsTime", Time(state.Position)), Field("RelCount", 0), Field("AbsCount", 0));
            case "GetMediaInfo":
                return Reply(Field("NrTracks", state.Duration > TimeSpan.Zero ? 1 : 0), Field("MediaDuration", Time(state.Duration)), Field("CurrentURI", ""),
                    Field("CurrentURIMetaData", ""), Field("NextURI", ""), Field("NextURIMetaData", ""),
                    Field("PlayMedium", "NETWORK"), Field("RecordMedium", "NOT_IMPLEMENTED"), Field("WriteStatus", "NOT_IMPLEMENTED"));
            case "GetDeviceCapabilities": return Reply(Field("PlayMedia", "NETWORK"), Field("RecMedia", "NOT_IMPLEMENTED"), Field("RecQualityModes", "NOT_IMPLEMENTED"));
            case "GetTransportSettings": return Reply(Field("PlayMode", "NORMAL"), Field("RecQualityMode", "NOT_IMPLEMENTED"));
            case "GetCurrentTransportActions": return Reply(Field("Actions", "Play,Pause,Stop,Seek"));
            default: return null;
        }
    }

    private static string Time(TimeSpan time) => time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string ReadTitle(string metadata)
    {
        if (metadata.Length == 0) return CastNetwork.CleanTitle(null);
        try
        {
            using var input = new StringReader(metadata);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32_768 });
            return CastNetwork.CleanTitle(XDocument.Load(reader).Descendants().FirstOrDefault(item => item.Name.LocalName == "title")?.Value);
        }
        catch (XmlException) { return CastNetwork.CleanTitle(null); }
    }

    private static Task FaultAsync(HttpContext context)
    {
        context.Response.StatusCode = 500;
        XNamespace upnp = "urn:schemas-upnp-org:control-1-0";
        return XmlAsync(context, new XElement(Soap + "Envelope", new XElement(Soap + "Body",
            new XElement(Soap + "Fault", new XAttribute(XNamespace.Xmlns + "s", Soap.NamespaceName), new XElement("faultcode", "s:Client"),
                new XElement("faultstring", "UPnPError"), new XElement("detail", new XElement(upnp + "UPnPError",
                    new XElement(upnp + "errorCode", 401), new XElement(upnp + "errorDescription", "Invalid Action")))))));
    }

    private static Task XmlAsync(HttpContext context, XElement payload)
    {
        context.Response.ContentType = "text/xml; charset=utf-8";
        var data = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + payload.ToString(SaveOptions.DisableFormatting));
        context.Response.ContentLength = data.Length;
        return context.Response.Body.WriteAsync(data, context.RequestAborted).AsTask();
    }

    public void Dispose()
    {
        Announce("ssdp:byebye");
        _discovery?.Dispose();
        _discovery = null;
    }
}
