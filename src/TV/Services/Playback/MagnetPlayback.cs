using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using TV.Services.Library;
using TV.Services.Persistence;

namespace TV.Services.Playback;

internal sealed class MagnetPlayback : IAsyncDisposable
{
    internal const int MaxLinkLength = 8192;
    private const long MaxTorrentBytes = 24L * 1024 * 1024 * 1024;
    private readonly ClientEngine _engine;
    private readonly TorrentManager _manager;
    private readonly Stream _stream;
    private readonly string _directory;
    private readonly CancellationTokenSource _reading = new();
    private int _disposed;

    private MagnetPlayback(ClientEngine engine, TorrentManager manager, Stream stream, string directory, ITorrentManagerFile file)
    {
        _engine = engine;
        _manager = manager;
        _stream = stream;
        _directory = directory;
        _file = file;
        FileName = Path.GetFileName(file.Path);
    }

    internal string FileName { get; }
    private readonly ITorrentManagerFile _file;
    internal bool TryGetCompleteFile(out string path)
    {
        path = _file.FullPath;
        return _file.BitField.AllTrue && File.Exists(path);
    }
    internal string? ReadFailure { get; private set; }

    internal async Task SaveSelectedFileAsync(string destination, IProgress<double> progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _reading.Token);
        var buffer = new byte[128 * 1024];
        _stream.Position = 0;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long written = 0;
        try
        {
            while (written < _file.Length)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var count = (int)Math.Min(buffer.Length, _file.Length - written);
                var length = await _stream.ReadAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                if (length == 0) throw new EndOfStreamException("磁力资源提前结束，文件未保存。");
                await output.WriteAsync(buffer.AsMemory(0, length), deadline.Token).ConfigureAwait(false);
                written += length;
                progress.Report(Math.Min(99, written * 100d / _file.Length));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_reading.IsCancellationRequested)
        {
            throw new TimeoutException("等待磁力节点继续传输超时。");
        }
    }
    internal static bool IsMagnet(string? address) => address?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool TryParse(string? address, [NotNullWhen(true)] out MagnetLink? magnet)
    {
        magnet = null;
        if (!IsMagnet(address) || address!.Length > MaxLinkLength || address.Any(char.IsControl) ||
            !MagnetLink.TryParse(address, out var parsed)) return false;
        var trackers = parsed.AnnounceUrls.Where(IsTracker).Distinct(StringComparer.Ordinal).Take(16).ToArray();
        var name = new string((parsed.Name ?? "").Where(c => !char.IsControl(c)).Take(140).ToArray()).Trim();
        magnet = new MagnetLink(parsed.InfoHashes, name, trackers);
        return true;
    }

    private static bool IsTracker(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "udp" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && !uri.IsLoopback;

    internal static async Task<MagnetLink> ResolveUdpTrackersAsync(MagnetLink magnet, CancellationToken token)
    {
        var trackers = await Task.WhenAll(magnet.AnnounceUrls.Select(async address =>
        {
            var uri = new Uri(address);
            if (uri.Scheme != "udp") return address;
            if (uri.Port <= 0) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var ips = await Dns.GetHostAddressesAsync(uri.IdnHost, deadline.Token).ConfigureAwait(false);
                var ip = ips.FirstOrDefault(IsPublicAddress);
                return ip is null ? null : new UriBuilder(uri) { Host = ip.ToString() }.Uri.AbsoluteUri;
            }
            catch (SocketException) { return null; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        })).ConfigureAwait(false);
        return new MagnetLink(magnet.InfoHashes, magnet.Name, trackers.OfType<string>().ToArray());
    }

    internal static async Task<MagnetPlayback> OpenAsync(string address, CancellationToken token)
    {
        if (!TryParse(address, out var magnet)) throw new InvalidDataException("磁力链接格式无效或缺少 BT 信息哈希。");
        var directory = Path.Combine(AppPaths.Root, "cache", "magnet", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(70));
            var torrent = await MagnetMetadataWorker.ReadAsync(address, directory, deadline.Token).ConfigureAwait(false);
            if (torrent.InfoHashes.V1 is { } v1 && magnet.InfoHashes.Contains(v1) ||
                torrent.InfoHashes.V2 is { } v2 && magnet.InfoHashes.Contains(v2))
                return await OpenFileAsync(torrent, directory, Settings(directory), CreateFactories(), deadline.Token).ConfigureAwait(false);
            throw new InvalidDataException("磁力元数据的哈希与输入链接不匹配。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            TryRemoveCache(directory);
            throw new IOException("等待磁力节点或首段数据超时。");
        }
        catch
        {
            TryRemoveCache(directory);
            throw;
        }
    }

    internal static async Task<MagnetPlayback> OpenFileAsync(Torrent torrent, string directory, EngineSettings settings,
        Factories factories, CancellationToken token, Uri? initialPeer = null)
    {
        ValidateFiles(torrent);
        torrent.HttpSeeds.Clear();
        var engine = new ClientEngine(settings, factories);
        TorrentManager? manager = null;
        Stream? stream = null;
        try
        {
            manager = await engine.AddStreamingAsync(torrent, directory, new TorrentSettingsBuilder
            {
                CreateContainingDirectory = false,
                MaximumConnections = 36,
                AllowInitialSeeding = false
            }.ToSettings()).ConfigureAwait(false);
            var playable = manager.Files.Where(file => file.Length > 0 && LocalMediaService.IsSupportedMedia(file.Path))
                .OrderByDescending(file => file.Length).ThenBy(file => file.Path, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new NotSupportedException("磁力资源没有可播放的音频或视频。");
            if (DriveInfo.GetDrives().FirstOrDefault(drive => string.Equals(drive.Name, Path.GetPathRoot(directory),
                    StringComparison.OrdinalIgnoreCase)) is { IsReady: true } disk && disk.AvailableFreeSpace < playable.Length)
                throw new IOException("剩余磁盘空间不足以缓存磁力视频或音频。");
            foreach (var file in manager.Files)
            {
                token.ThrowIfCancellationRequested();
                await manager.SetFilePriorityAsync(file, ReferenceEquals(file, playable) ? Priority.High : Priority.DoNotDownload)
                    .ConfigureAwait(false);
            }
            await manager.StartAsync().ConfigureAwait(false);
            if (initialPeer is not null) await manager.AddPeerAsync(new PeerInfo(initialPeer)).ConfigureAwait(false);
            stream = await manager.StreamProvider!.CreateStreamAsync(playable, prebuffer: false, token).ConfigureAwait(false);
            // Let the player request the file tail if the container needs it. Require
            // the first piece now so there is still a bounded deadline before opening the player.
            var probe = new byte[1];
            await stream.ReadExactlyAsync(probe, 0, probe.Length, token).ConfigureAwait(false);
            stream.Position = 0;
            return new MagnetPlayback(engine, manager, stream, directory, playable);
        }
        catch
        {
            stream?.Dispose();
            await StopEngineAsync(engine, manager).ConfigureAwait(false);
            throw;
        }
    }

    internal static void ValidateFiles(Torrent torrent)
    {
        if (torrent.Size <= 0 || torrent.Size > MaxTorrentBytes || torrent.Files.Count is < 1 or > 1000 ||
            torrent.PieceLength is < 1 or > 32 * 1024 * 1024 || torrent.PieceCount > 262144)
            throw new InvalidDataException("磁力资源的文件或分片数量超出播放缓存限制。");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
        {
            var path = file.Path.Replace('\\', '/');
            if (file.Length < 0 || path.Length is 0 or > 220 || !files.Add(path) || folders.Contains(path))
                throw new InvalidDataException("磁力资源包含重复或无效的文件路径。");
            foreach (var segment in path.Split('/'))
            {
                var stem = segment.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
                if (segment.Length is 0 or > 160 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                    segment.AsSpan().ContainsAny(Path.GetInvalidFileNameChars()) ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                    (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    stem.Length == 4 && "0123456789¹²³".Contains(stem[3]))
                    throw new InvalidDataException("磁力资源包含 Windows 无法安全存储的文件名。");
            }
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                var folder = path[..slash];
                if (files.Contains(folder)) throw new InvalidDataException("磁力资源的文件与目录名称相冲突。");
                folders.Add(folder);
            }
        }
    }

    internal static EngineSettings Settings(string directory) => new EngineSettingsBuilder
    {
        CacheDirectory = directory,
        AllowPortForwarding = false,
        AllowLocalPeerDiscovery = false,
        AutoSaveLoadDhtCache = false,
        AutoSaveLoadMagnetLinkMetadata = false,
        AutoSaveLoadFastResume = false,
        ListenEndPoints = [],
        DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
        MaximumConnections = 36,
        MaximumHalfOpenConnections = 12,
        MaximumDownloadRate = 16 * 1024 * 1024,
        MaximumUploadRate = 128 * 1024,
        MaximumOpenFiles = 8,
        DiskCacheBytes = 8 * 1024 * 1024,
        UsePartialFiles = false
    }.ToSettings();

    internal static Factories CreateFactories() => Factories.Default
        .WithPeerConnectionCreator("ipv4", PublicPeer)
        .WithPeerConnectionCreator("ipv6", PublicPeer)
        .WithTrackerCreator("http", CreateHttpTracker)
        .WithTrackerCreator("https", CreateHttpTracker)
        .WithTrackerCreator("udp", uri => IsTracker(uri.AbsoluteUri) &&
            IPAddress.TryParse(uri.IdnHost, out var ip) && IsPublicAddress(ip)
            ? Factories.Default.CreateTracker(uri)! : throw new IOException("磁力 Tracker 地址不可用。"));

    private static MonoTorrent.Connections.Peer.IPeerConnection PublicPeer(Uri uri) =>
        IPAddress.TryParse(uri.IdnHost, out var ip) && IsPublicAddress(ip)
            ? Factories.Default.CreatePeerConnection(uri)! : throw new IOException("磁力节点地址不可用。");

    private static ITracker CreateHttpTracker(Uri uri)
    {
        if (!IsTracker(uri.AbsoluteUri)) throw new IOException("磁力 Tracker 地址不可用。");
        return new Tracker(new HttpTrackerConnection(uri, CreateTrackerClient, AddressFamily.InterNetwork),
            new HttpTrackerConnection(uri, CreateTrackerClient, AddressFamily.InterNetworkV6));
    }

    private static HttpClient CreateTrackerClient(AddressFamily family) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, token).ConfigureAwait(false);
            foreach (var address in addresses.Where(IsPublicAddress))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("没有可连接的公网 Tracker 地址。");
        }
    }) { Timeout = TimeSpan.FromSeconds(12) };

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] is 0 or 168) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19);
        return (bytes[0] & 0xe0) == 0x20 && !address.IsIPv6Teredo;
    }

    internal MediaInput CreateInput() => new TorrentInput(this);
    internal void CancelReads()
    {
        try { _reading.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelReads();
        _stream.Dispose();
        await StopEngineAsync(_engine, _manager).ConfigureAwait(false);
        _reading.Dispose();
        TryRemoveCache(_directory);
    }

    private static async Task StopEngineAsync(ClientEngine engine, TorrentManager? manager)
    {
        try
        {
            if (manager is not null) await manager.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) { Diagnostics.Log("磁力会话停止: " + ex.GetType().Name); }
        finally { engine.Dispose(); }
    }

    private static void TryRemoveCache(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Diagnostics.Log("磁力缓存清理: " + ex.GetType().Name); }
    }

    private sealed class TorrentInput : MediaInput
    {
        private const int BufferBytes = 128 * 1024;
        private readonly MagnetPlayback _session;

        internal TorrentInput(MagnetPlayback session)
        {
            _session = session;
            CanSeek = true;
        }

        public override bool Open(out ulong size)
        {
            size = (ulong)_session._stream.Length;
            return Seek(0);
        }

        public override int Read(nint buffer, uint length)
        {
            var data = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, BufferBytes));
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_session._reading.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                var count = _session._stream.ReadAsync(data, 0, (int)Math.Min(length, BufferBytes), deadline.Token)
                    .GetAwaiter().GetResult();
                Marshal.Copy(data, 0, buffer, count);
                return count;
            }
            catch (OperationCanceledException)
            {
                if (!_session._reading.IsCancellationRequested) _session.ReadFailure = "磁力节点未及时返回播放数据。";
                return -1;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("磁力数据读取: " + ex.GetType().Name);
                _session.ReadFailure = "读取磁力数据失败。";
                return -1;
            }
            finally { ArrayPool<byte>.Shared.Return(data); }
        }

        public override bool Seek(ulong offset)
        {
            if (_session._reading.IsCancellationRequested || offset > (ulong)_session._stream.Length) return false;
            _session._stream.Position = (long)offset;
            return true;
        }

        public override void Close() { }
    }
}
