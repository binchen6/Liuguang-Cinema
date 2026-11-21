using System.Net;
using System.Text.Json;
using QRCoder;
using TV.Services.Cloud;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Vod.Native;

internal sealed record BilibiliLoginCode(string Key, byte[] Image);
internal enum BilibiliLoginState { WaitingForScan, WaitingForConfirmation, Expired, SignedIn }
internal sealed record BilibiliLoginResult(BilibiliLoginState State, string? AccountName = null);

public sealed class BilibiliAccountService(DatabaseService database, DnsResolverService? dns = null) : IDisposable
{
    internal const string Api = "https://api.bilibili.com";
    internal const string Site = "https://www.bilibili.com/";
    private const string CredentialKey = "vod.bilibili.session";
    private const string Passport = "https://passport.bilibili.com";
    private const string LoginPath = Passport + "/x/passport-login/web/qrcode/";
    private readonly CloudHttpClient _http = new(dns, allowAutoRedirect: false);

    internal async Task<BilibiliLoginCode> CreateQrAsync(CancellationToken token)
    {
        var response = await GetAsync(LoginPath + "generate", null, token).ConfigureAwait(false);
        var data = RequireData(response.Json);
        var key = Text(data, "qrcode_key");
        var address = Text(data, "url");
        if (key.Length is 0 or > 256 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Host is not ("account.bilibili.com" or "passport.bilibili.com"))
            throw new InvalidDataException("B 站没有返回有效的扫码地址。");

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(qrData);
        return new BilibiliLoginCode(key, qr.GetGraphic(6));
    }

    internal async Task<BilibiliLoginResult> PollAsync(BilibiliLoginCode login, CancellationToken token)
    {
        var response = await GetAsync(LoginPath + "poll?qrcode_key=" + Uri.EscapeDataString(login.Key), null, token)
            .ConfigureAwait(false);
        var data = RequireData(response.Json);
        switch (Code(data))
        {
            case 86101: return new BilibiliLoginResult(BilibiliLoginState.WaitingForScan);
            case 86090: return new BilibiliLoginResult(BilibiliLoginState.WaitingForConfirmation);
            case 86038: return new BilibiliLoginResult(BilibiliLoginState.Expired);
            case 0: break;
            default: throw new InvalidDataException("B 站未能完成扫码登录，请刷新二维码。");
        }

        var session = ReadSession(response.Cookies);
        if (session.Length == 0) throw new InvalidDataException("B 站没有签发可用的登录会话。");
        var name = await VerifyAsync("SESSDATA=" + session, token).ConfigureAwait(false);
        if (name is null) throw new InvalidDataException("B 站未确认登录会话，请重新扫码。");
        token.ThrowIfCancellationRequested();
        await database.SetSettingAsync(CredentialKey, SecretStore.Protect(session), token).ConfigureAwait(false);
        return new BilibiliLoginResult(BilibiliLoginState.SignedIn, name);
    }

    internal async Task<string?> GetAccountNameAsync(CancellationToken token) =>
        await GetPlaybackCookieAsync(token).ConfigureAwait(false) is { } cookie
            ? await VerifyAsync(cookie, token).ConfigureAwait(false) : null;

    internal Task SignOutAsync(CancellationToken token) => database.SetSettingAsync(CredentialKey, string.Empty, token);

    internal async Task<string?> GetPlaybackCookieAsync(CancellationToken token)
    {
        var session = SecretStore.Unprotect(await database.GetSettingAsync(CredentialKey, token).ConfigureAwait(false));
        return IsSessionValue(session) ? "SESSDATA=" + session : null;
    }

    private async Task<string?> VerifyAsync(string cookie, CancellationToken token)
    {
        var response = await GetAsync(Api + "/x/web-interface/nav", cookie, token).ConfigureAwait(false);
        if (Code(response.Json) == -101) return null;
        var data = RequireData(response.Json);
        if (Value(data, "isLogin").ValueKind != JsonValueKind.True) return null;
        return Text(data, "uname") is { Length: > 0 } name ? name : "B 站用户";
    }

    private async Task<CloudHttpResult> GetAsync(string address, string? cookie, CancellationToken token)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = Site };
        if (cookie is not null) headers["Cookie"] = cookie;
        CloudHttpResult result;
        try { result = await _http.GetAsync(address, headers, token).ConfigureAwait(false); }
        catch (InvalidDataException) { throw new InvalidDataException("B 站账号接口返回了无法识别的响应。"); }
        if (result.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"B 站账号接口暂不可用（HTTP {(int)result.StatusCode}）。", null, result.StatusCode);
        return result;
    }

    private static string ReadSession(IReadOnlyList<string> headers)
    {
        var cookies = new CookieContainer();
        var issuer = new Uri(Passport);
        foreach (var header in headers)
        {
            try { cookies.SetCookies(issuer, header); }
            catch (CookieException) { }
        }
        var value = cookies.GetCookies(new Uri(Api))["SESSDATA"]?.Value ?? string.Empty;
        return IsSessionValue(value) ? value : string.Empty;
    }

    private static bool IsSessionValue(string value) => value.Length is > 0 and <= 2048 && value.All(character =>
        character is '!' or (>= '#' and <= '+') or (>= '-' and <= ':') or (>= '<' and <= '[') or (>= ']' and <= '~'));

    private static JsonElement RequireData(JsonElement root) => Code(root) == 0 && Value(root, "data") is { ValueKind: JsonValueKind.Object } data
        ? data : throw new InvalidDataException("B 站账号接口暂未返回有效数据。");

    private static int? Code(JsonElement root) =>
        Value(root, "code") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var result) ? result : null;
    private static string Text(JsonElement root, string key) =>
        Value(root, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;
    private static JsonElement Value(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;

    public void Dispose() => _http.Dispose();
}
