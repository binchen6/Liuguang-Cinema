using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using QRCoder;
using TV.Models;

namespace TV.Services.Cloud;

public sealed class CloudLoginService
{
    private sealed class State
    {
        public required CloudLoginSession Session { get; init; }
        public Dictionary<string, string> Parameters { get; } = new();
        public string Token { get; set; } = string.Empty;
        public string Cookie { get; set; } = string.Empty;
        public CookieContainer? BaiduCookies { get; init; }
    }

    private static readonly Uri BaiduPassport = new("https://passport.baidu.com/");
    private static readonly Uri BaiduDrive = new("https://pan.baidu.com/");
    private static readonly Uri BaiduDriveLanding = new(BaiduDrive, "disk/main");
    private readonly CloudHttpClient _http;
    private readonly CloudHttpClient _ucAccountHttp;
    private readonly CloudDriveService _cloud;
    private readonly ConcurrentDictionary<string, State> _states = new();

    public CloudLoginService(CloudHttpClient http, CloudDriveService cloud, CloudHttpClient ucAccountHttp)
    {
        _http = http;
        _ucAccountHttp = ucAccountHttp;
        _cloud = cloud;
    }

    public const string UcAccountEndpoint = "https://pc-api.uc.cn/1/clouddrive/file/sort?pr=UCBrowser&fr=pc&pdir_fid=0&_page=1&_size=1";

    public async Task<bool> VerifyAliTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var oauth = await AliOAuth.RefreshAsync(_http, token, cancellationToken).ConfigureAwait(false);
        if (oauth is null) return false;
        if (oauth.RefreshToken.Length > 0) await _cloud.SaveCredentialAsync(CloudProviderKind.Ali, oauth.RefreshToken, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> VerifyBaiduCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie) || !cookie.Contains("BDUSS=", StringComparison.OrdinalIgnoreCase)) return false;
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie, ["Referer"] = "https://pan.baidu.com/" };
        var login = await _ucAccountHttp.GetAsync("https://pan.baidu.com/api/loginStatus", headers, cancellationToken).ConfigureAwait(false);
        if (login.StatusCode == System.Net.HttpStatusCode.OK && login.Json.ValueKind == JsonValueKind.Object &&
            login.Json.TryGetProperty("errno", out var loginError) && loginError.ValueKind == JsonValueKind.Number && loginError.GetInt32() == 0 &&
            Path(login.Json, "res").ValueKind == JsonValueKind.Object && String(Path(login.Json, "res"), "uname").Length > 0) return true;
        Diagnostics.Log($"百度 loginStatus 验证: HTTP {(int)login.StatusCode}, errno={Int(login.Json, "errno")}");
        var result = await _ucAccountHttp.GetAsync("https://pan.baidu.com/api/quota?checkfree=1&checkexpire=1", headers, cancellationToken).ConfigureAwait(false);
        var valid = result.StatusCode == System.Net.HttpStatusCode.OK && result.Json.ValueKind == JsonValueKind.Object &&
            result.Json.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number && error.GetInt32() == 0;
        if (!valid) Diagnostics.Log($"百度 quota 验证: HTTP {(int)result.StatusCode}, errno={Int(result.Json, "errno")}");
        return valid;
    }

    public async Task<bool> VerifyUcCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return false;
        var response = await _ucAccountHttp.GetAsync(UcAccountEndpoint, new Dictionary<string, string>
            { ["Cookie"] = cookie, ["Referer"] = "https://drive.uc.cn/" }, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == System.Net.HttpStatusCode.OK &&
            Path(response.Json, "data", "list").ValueKind == JsonValueKind.Array;
    }

    public async Task<CloudLoginSession> StartAsync(CloudProviderKind provider, CancellationToken cancellationToken = default)
    {
        var state = provider switch
        {
            CloudProviderKind.Ali => await StartAliAsync(cancellationToken).ConfigureAwait(false),
            CloudProviderKind.Quark => await StartQuarkAsync(cancellationToken).ConfigureAwait(false),
            CloudProviderKind.UC => await StartUcAsync(cancellationToken).ConfigureAwait(false),
            CloudProviderKind.Baidu => await StartBaiduAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException("不支持此网盘。")
        };
        _states[state.Session.Id] = state;
        return state.Session;
    }

    public CloudLoginSession? Get(string id) => _states.TryGetValue(id, out var state) ? state.Session : null;
    public void Cancel(string id) => _states.TryRemove(id, out _);

    public async Task<CloudLoginSession> PollAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(id, out var state)) throw new InvalidOperationException("登录会话已失效，请重新生成二维码。");
        var session = state.Session;
        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            session.Status = "二维码已过期";
            return session;
        }
        if (session.Provider == CloudProviderKind.Baidu) await PollBaiduAsync(state, cancellationToken).ConfigureAwait(false);
        else if (session.Provider == CloudProviderKind.Ali) await PollAliAsync(state, cancellationToken).ConfigureAwait(false);
        else if (session.Provider == CloudProviderKind.UC) await PollUcAsync(state, cancellationToken).ConfigureAwait(false);
        else await PollQuarkAsync(state, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<State> StartAliAsync(CancellationToken cancellationToken)
    {
        const string url = "https://passport.aliyundrive.com/newlogin/qrcode/generate.do?appName=aliyun_drive&fromSite=52&appEntrance=web&isMobile=false&lang=zh_CN&returnUrl=&bizParams=&_bx-v=2.2.3";
        var result = await _http.GetAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = Path(result.Json, "content", "data");
        var content = String(data, "codeContent");
        if (content.Length == 0) throw new InvalidOperationException("阿里二维码生成失败。");
        var session = CreateSession(CloudProviderKind.Ali, content);
        var state = new State { Session = session };
        state.Parameters["t"] = String(data, "t");
        state.Parameters["ck"] = String(data, "ck");
        state.Parameters["appName"] = "aliyun_drive";
        state.Parameters["appEntrance"] = "web";
        state.Parameters["isMobile"] = "false";
        state.Parameters["lang"] = "zh_CN";
        state.Parameters["returnUrl"] = string.Empty;
        state.Parameters["fromSite"] = "52";
        state.Parameters["bizParams"] = string.Empty;
        state.Parameters["navlanguage"] = "zh-CN";
        state.Parameters["navPlatform"] = "Win32";
        return state;
    }

    private async Task<State> StartQuarkAsync(CancellationToken cancellationToken)
    {
        const string clientId = "386";
        var url = "https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin?client_id=" + clientId + "&v=1.2&request_id=" + Guid.NewGuid();
        var result = await _http.GetAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
        var token = String(Path(result.Json, "data", "members"), "token");
        if (token.Length == 0) throw new InvalidOperationException("网盘二维码生成失败。");
        var content = "https://su.quark.cn/4_eMHBJ?uc_param_str=&token=" + Uri.EscapeDataString(token) + "&client_id=" + clientId;
        var state = new State { Session = CreateSession(CloudProviderKind.Quark, content), Token = token, Cookie = CookieHeader(result.Cookies) };
        state.Parameters["client_id"] = clientId;
        state.Parameters["v"] = "1.2";
        state.Parameters["request_id"] = Guid.NewGuid().ToString();
        return state;
    }

    private Task<CloudHttpResult> UcQrRequestAsync(string action, string? token, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["client_id"] = "381", ["v"] = "1.2",
            ["request_id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (token is not null) form["token"] = token;
        return _ucAccountHttp.PostFormAsync("https://api.open.uc.cn/cas/ajax/" + action, form,
            new Dictionary<string, string> { ["Origin"] = "https://broccoli.uc.cn", ["Referer"] = "https://broccoli.uc.cn/" }, ct);
    }

    private async Task<State> StartUcAsync(CancellationToken ct)
    {
        var response = await UcQrRequestAsync("getTokenForQrcodeLogin", null, ct).ConfigureAwait(false);
        var token = String(Path(response.Json, "data", "members"), "token");
        if (String(response.Json, "status") != "2000000" || token.Length == 0)
            throw new InvalidOperationException("UC 二维码生成失败。");
        var content = "https://su.uc.cn/1_n0ZCv?uc_param_str=dsdnfrpfbivesscpgimibtbmnijblauputogpintnwktprchmt&token="
            + Uri.EscapeDataString(token) + "&client_id=381&uc_biz_str=S%3Acustom%7CC%3Atitlebar_fix";
        return new State { Session = CreateSession(CloudProviderKind.UC, content), Token = token };
    }

    private async Task<State> StartBaiduAsync(CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var path = "v2/api/getqrcode?lp=pc&tpl=netdisk&apiver=v3&tt=" +
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await SendBaiduAsync(new Uri(BaiduPassport, path), cookies, cancellationToken).ConfigureAwait(false);
        if (result.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("百度二维码生成失败。");
        var payload = ReadJsonp(result.Text);
        var sign = String(payload, "sign");
        var image = String(payload, "imgurl");
        if (sign.Length == 0 || image.Length == 0) throw new InvalidOperationException("百度二维码生成失败。");
        if (!image.StartsWith("http", StringComparison.OrdinalIgnoreCase)) image = "https://" + image.TrimStart('/');
        if (!Uri.TryCreate(image, UriKind.Absolute, out var address) || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || !address.IsDefaultPort ||
            !address.IdnHost.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("百度二维码地址无效。");
        var qr = await SendBaiduAsync(address, cookies, cancellationToken).ConfigureAwait(false);
        if (qr.StatusCode != System.Net.HttpStatusCode.OK || qr.Content.Length < 8 || qr.Content[0] != 0x89 || qr.Content[1] != 0x50)
            throw new InvalidDataException("百度二维码图片读取失败。");
        var session = new CloudLoginSession
        {
            Id = Guid.NewGuid().ToString("N"), Provider = CloudProviderKind.Baidu,
            Content = "https://wappass.baidu.com/wp/?qrlogin=&sign=" + Uri.EscapeDataString(sign),
            QrPng = qr.Content, Status = "等待扫码", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3)
        };
        return new State { Session = session, Token = sign, BaiduCookies = cookies };
    }

    private async Task PollBaiduAsync(State state, CancellationToken cancellationToken)
    {
        var cookies = state.BaiduCookies ?? throw new InvalidOperationException("百度扫码会话已失效。");
        CloudRawResult response;
        try
        {
            var poll = new Uri(BaiduPassport, "channel/unicast?channel_id=" + Uri.EscapeDataString(state.Token) + "&tpl=netdisk&apiver=v3&callback=");
            response = await SendBaiduAsync(poll, cookies, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state.Session.Status = "等待扫码";
            return;
        }
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("百度扫码状态读取失败。");
        var root = ReadJsonp(response.Text);
        var channel = Path(root, "channel_v");
        if (channel.ValueKind == JsonValueKind.String && channel.GetString() is { Length: > 0 } json)
            channel = ReadJsonp(json);
        if (String(root, "errno") != "0" || channel.ValueKind != JsonValueKind.Object)
        {
            state.Session.Status = "等待扫码";
            return;
        }
        var scanStatus = String(channel, "status");
        if (scanStatus != "0")
        {
            state.Session.Status = scanStatus == "1" ? "已扫码，请在手机确认" : "等待扫码";
            return;
        }
        var temporary = String(channel, "v");
        if (temporary.Length == 0) throw new InvalidDataException("百度扫码成功但未返回登录票据。");
        state.Session.Status = "正在验证百度登录";
        var exchange = new Uri(BaiduPassport, "v3/login/main/qrbdusslogin?bduss=" + Uri.EscapeDataString(temporary) +
            "&tpl=netdisk&loginVersion=v4&qrcode=1&apiver=v3&callback=bd__cbs__login&u=" + Uri.EscapeDataString(BaiduDriveLanding.AbsoluteUri));
        var login = await SendBaiduAsync(exchange, cookies, cancellationToken).ConfigureAwait(false);
        if ((int)login.StatusCode is < 200 or >= 400) throw new InvalidOperationException("百度登录票据兑换失败。");
        if (cookies.GetCookies(BaiduDrive)["BDUSS"] is null)
        {
            foreach (var value in ReadBaiduSessionCookies(login.Text)) cookies.SetCookies(BaiduDrive, value);
        }
        if (cookies.GetCookies(BaiduDrive)["BDUSS"] is null) throw new InvalidOperationException("百度未返回登录凭据，请重新扫码。");

        // The Drive landing page exchanges the Passport login for Drive-scoped STOKEN and PANPSC cookies.
        await OpenBaiduDriveSessionAsync(cookies, cancellationToken).ConfigureAwait(false);
        var cookie = cookies.GetCookieHeader(BaiduDrive);
        if (!await VerifyBaiduCookieAsync(cookie, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("百度网盘会话验证失败，请重新扫码。");
        cancellationToken.ThrowIfCancellationRequested();
        await _cloud.SaveCredentialAsync(CloudProviderKind.Baidu, cookie, cancellationToken).ConfigureAwait(false);
        state.Session.Credential = cookie;
        state.Session.Status = "登录成功";
    }

    private static JsonElement ReadJsonp(string text)
    {
        var json = text.TrimStart('\uFEFF').Trim();
        if (!json.StartsWith('{'))
        {
            var start = json.IndexOf('(');
            var end = json.LastIndexOf(')');
            if (start >= 0 && end > start) json = json[(start + 1)..end];
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<CloudRawResult> SendBaiduAsync(Uri address, CookieContainer cookies, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = BaiduPassport.AbsoluteUri };
        var cookie = cookies.GetCookieHeader(address);
        if (cookie.Length > 0) headers["Cookie"] = cookie;
        var result = await _ucAccountHttp.SendRawAsync(HttpMethod.Get, address.AbsoluteUri,
            headers, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var value in result.Cookies)
        {
            try { cookies.SetCookies(address, value); }
            catch (CookieException) { }
        }
        return result;
    }

    private async Task OpenBaiduDriveSessionAsync(CookieContainer cookies, CancellationToken cancellationToken)
    {
        var address = BaiduDriveLanding;
        for (var hop = 0; hop < 8; hop++)
        {
            var response = await SendBaiduAsync(address, cookies, cancellationToken).ConfigureAwait(false);
            Diagnostics.Log($"百度网盘会话: {address.IdnHost}, HTTP {(int)response.StatusCode}, Set-Cookie 数量={response.Cookies.Count}");
            if (response.StatusCode == HttpStatusCode.OK && address.IdnHost == BaiduDrive.IdnHost) return;
            var next = BaiduLoginRedirect(address, response);
            if (next is null) throw new InvalidOperationException("百度网盘未建立登录会话。");
            address = next;
        }
        throw new InvalidOperationException("百度网盘登录跳转次数过多，请重新扫码。");
    }

    private static Uri? BaiduLoginRedirect(Uri address, CloudRawResult response)
    {
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) ||
            response.RedirectLocation is not { } location || !Uri.TryCreate(address, location, out var next) ||
            next.Scheme != "https" || next.UserInfo.Length != 0 || !next.IsDefaultPort) return null;
        return next.IdnHost is "pan.baidu.com" or "passport.baidu.com" or "wappass.baidu.com" ? next : null;
    }

    private static List<string> ReadBaiduSessionCookies(string text)
    {
        JsonElement session;
        try { session = Path(ReadJsonp(text), "data", "session"); }
        catch (JsonException) { return []; }
        var values = new List<string>();
        foreach (var name in new[] { "bduss", "ptoken", "stoken" })
        {
            var value = Path(session, name) is { ValueKind: JsonValueKind.String } token ? token.GetString() : null;
            if (!string.IsNullOrEmpty(value) && value.All(c => c is >= '!' and <= '~' and not ('"' or ',' or ';' or '\\')))
                values.Add(name.ToUpperInvariant() + "=" + value);
        }
        return values;
    }

    private async Task PollUcAsync(State state, CancellationToken ct)
    {
        if (state.Cookie.Length == 0)
        {
            var response = await UcQrRequestAsync("getServiceTicketByQrcodeToken", state.Token, ct).ConfigureAwait(false);
            var status = String(response.Json, "status");
            if (status == "50004001") { state.Session.Status = "等待扫码"; return; }
            if ((int)response.StatusCode >= 400 || status.Length == 0) throw new InvalidOperationException("UC 扫码状态读取失败。");
            if (status != "2000000") { state.Session.Status = "二维码已过期"; return; }
            var ticket = String(Path(response.Json, "data", "members"), "service_ticket");
            if (ticket.Length == 0) throw new InvalidOperationException("UC 未返回登录票据。");
            var account = await _ucAccountHttp.GetAsync("https://drive.uc.cn/account/info?st=" + Uri.EscapeDataString(ticket),
                new Dictionary<string, string> { ["Referer"] = "https://drive.uc.cn/" }, ct).ConfigureAwait(false);
            if (account.StatusCode != System.Net.HttpStatusCode.OK || Path(account.Json, "success").ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("UC 网盘未确认登录。");
            var jar = new System.Net.CookieContainer();
            foreach (var cookie in account.Cookies) jar.SetCookies(new Uri("https://drive.uc.cn/account/info"), cookie);
            state.Cookie = jar.GetCookieHeader(new Uri(UcAccountEndpoint));
        }
        state.Session.Status = "正在验证 UC 登录";
        if (!await VerifyUcCookieAsync(state.Cookie, ct).ConfigureAwait(false))
            throw new InvalidOperationException("UC 网盘未接受登录凭据，请重新扫码。");
        ct.ThrowIfCancellationRequested();
        await _cloud.SaveCredentialAsync(CloudProviderKind.UC, state.Cookie, ct).ConfigureAwait(false);
        state.Session.Credential = state.Cookie;
        state.Session.Status = "登录成功";
    }

    private async Task PollAliAsync(State state, CancellationToken cancellationToken)
    {
        var result = await _http.PostFormAsync("https://passport.aliyundrive.com/newlogin/qrcode/query.do?appName=aliyun_drive&fromSite=52&_bx-v=2.2.3", state.Parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = Path(result.Json, "content", "data");
        var status = String(data, "qrCodeStatus");
        state.Session.Status = status switch { "CONFIRMED" => "正在登录", "SCANNED" => "已扫码，请在手机确认", "EXPIRED" => "二维码已过期", _ => "等待扫码" };
        if (status != "CONFIRMED") return;
        var encoded = String(data, "bizExt");
        if (encoded.Length == 0) throw new InvalidDataException("阿里登录确认中未返回授权数据。");
        using var decoded = JsonDocument.Parse(DecodeBase64(encoded));
        var token = String(Path(decoded.RootElement, "pds_login_result"), "refreshToken");
        if (token.Length == 0) throw new InvalidOperationException("阿里登录成功但未返回 Token。");
        var oauth = await AliOAuth.RefreshAsync(_http, token, cancellationToken).ConfigureAwait(false);
        if (oauth is null) throw new InvalidOperationException("阿里扫码已确认，但官方 Token 验证失败，请重试。");
        await _cloud.SaveCredentialAsync(CloudProviderKind.Ali, oauth.RefreshToken, cancellationToken).ConfigureAwait(false);
        state.Session.Credential = oauth.RefreshToken;
        state.Session.Status = "登录成功";
    }

    private async Task PollQuarkAsync(State state, CancellationToken cancellationToken)
    {
        var query = string.Join('&', state.Parameters.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))) + "&token=" + Uri.EscapeDataString(state.Token);
        var result = await _http.GetAsync("https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken?" + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var status = String(result.Json, "status");
        var ticket = String(Path(result.Json, "data", "members"), "service_ticket");
        if (ticket.Length == 0)
        {
            state.Session.Status = status == "2000001" ? "已扫码，请在手机确认" : "等待扫码";
            return;
        }
        state.Session.Status = "正在登录";
        var account = await _http.GetAsync("https://pan.quark.cn/account/info?st=" + Uri.EscapeDataString(ticket) + "&lw=scan", cancellationToken: cancellationToken).ConfigureAwait(false);
        var cookie = CookieHeader(account.Cookies);
        if ((int)account.StatusCode is < 200 or >= 300 || cookie.Length == 0)
            throw new InvalidOperationException("夸克账号未建立登录会话。");
        var combined = string.Join(';', new[] { state.Cookie, cookie }.Where(value => value.Length > 0));
        if (combined.Length == 0) throw new InvalidOperationException("网盘登录成功但未返回 Cookie。");
        await _cloud.SaveCredentialAsync(state.Session.Provider, combined, cancellationToken).ConfigureAwait(false);
        state.Session.Credential = combined;
        state.Session.Status = "登录成功";
    }

    private static CloudLoginSession CreateSession(CloudProviderKind provider, string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return new CloudLoginSession { Id = Guid.NewGuid().ToString("N"), Provider = provider, Content = content, QrPng = qr.GetGraphic(12) };
    }

    private static string CookieHeader(IEnumerable<string> cookies) => string.Join(';', cookies.Select(cookie => cookie.Split(';')[0]).Where(value => value.Contains('=')).Distinct());
    private static byte[] DecodeBase64(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        if (normalized.Length % 4 != 0) normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4), '=');
        return Convert.FromBase64String(normalized);
    }
    private static JsonElement Path(JsonElement element, params string[] path) => path.Aggregate(element, (current, name) => current.ValueKind == JsonValueKind.Object && current.TryGetProperty(name, out var value) ? value : default);
    private static string String(JsonElement element, string name) => Path(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : Path(element, name).ValueKind == JsonValueKind.Number ? Path(element, name).GetRawText() : string.Empty;
    private static int Int(JsonElement element, string name) => Path(element, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var value) ? value
        : Path(element, name) is { ValueKind: JsonValueKind.String } text && int.TryParse(text.GetString(), out value) ? value : 0;
}
