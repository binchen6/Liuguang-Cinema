using System.Net;
using System.Net.Http.Headers;
using System.Text;

// CA1068 定向豁免：本类四个核心方法的 CancellationToken 参数位置已作为公共契约被全仓 60+ 站点适配器调用，
// 全量重排收益（纯 API 卫生）远小于回归风险，保持既有签名稳定。
#pragma warning disable CA1068

namespace TV.Services.Http;

public sealed record HttpPayload(byte[] Content, string ContentType, Uri FinalUri);
public sealed record HttpExchangeResult(HttpPayload Payload, HttpStatusCode StatusCode, IReadOnlyList<string> SetCookies, bool HasCloudflareChallenge);

public sealed class SourceHttpClient : IDisposable
{
    public const string DefaultUserAgent = "okhttp/3.12.11";
    private readonly HttpClient _client;
    private readonly DnsResolverService _dns;
    private readonly bool _ownsDns;
    private readonly SiteSessionStore? _sessions;

    public SourceHttpClient(DnsResolverService? dns = null, SiteSessionStore? sessions = null) : this(dns, sessions, false) { }

    internal SourceHttpClient(DnsResolverService? dns, SiteSessionStore? sessions, bool useSystemDns)
    {
        _dns = dns ?? new DnsResolverService(new Persistence.DatabaseService());
        _ownsDns = dns is null;
        _sessions = sessions;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _client = CreateClient(useSystemDns);
    }

    internal SourceHttpClient(HttpMessageHandler handler, SiteSessionStore? sessions = null)
    {
        _dns = new DnsResolverService(new Persistence.DatabaseService());
        _ownsDns = true;
        _sessions = sessions;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _client = new HttpClient(handler);
    }

    public SiteSessionStore? Sessions => _sessions;

    private HttpClient CreateClient(bool useSystemDns) => new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = useSystemDns ? null : _dns.ConnectAsync,
            UseCookies = false
        });

    public async Task<HttpPayload> GetBytesAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 12 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        bool scopeRedirectHeaders = false,
        TimeSpan? requestTimeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持有效的 HTTP/HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendFollowingAsync(HttpMethod.Get, uri, headers, null, scopeRedirectHeaders, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await ReadPayloadAsync(response, uri, maxBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接超时，请稍后重试或调整网络环境。", ex);
        }
    }

    /// <summary>
    /// Performs an HTTP GET returning status code, Set-Cookie headers, and body without throwing on 4xx/5xx,
    /// allowing providers to detect CAPTCHA / WAF verification challenges accurately.
    /// With a matching content-type predicate, a successful media response is returned without buffering its body.
    /// </summary>
    public async Task<HttpExchangeResult> GetExchangeAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        bool scopeRedirectHeaders = false,
        TimeSpan? requestTimeout = null,
        Func<string, bool>? skipBodyForContentType = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持有效的 HTTP/HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendFollowingAsync(HttpMethod.Get, uri, headers, null, scopeRedirectHeaders, timeout.Token).ConfigureAwait(false);
            var setCookies = ReadSetCookies(response);
            var hasCf = response.Headers.TryGetValues("cf-mitigated", out var cfValues) &&
                cfValues.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var payload = response.IsSuccessStatusCode && !hasCf && skipBodyForContentType?.Invoke(contentType) == true
                ? new HttpPayload([], contentType, response.RequestMessage?.RequestUri ?? uri)
                : await ReadPayloadAsync(response, uri, maxBytes, timeout.Token).ConfigureAwait(false);
            return new HttpExchangeResult(payload, response.StatusCode, setCookies, hasCf);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("请求超时，请稍后重试。", ex);
        }
    }

    /// <summary>
    /// Submits an application/x-www-form-urlencoded POST request (used for user-entered image CAPTCHA forms).
    /// </summary>
    public async Task<HttpExchangeResult> PostFormAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>> formFields,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        bool followRedirects = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持有效的 HTTP/HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var content = new FormUrlEncodedContent(formFields);
        try
        {
            using var response = followRedirects
                ? await SendFollowingAsync(HttpMethod.Post, uri, headers, content, false, timeout.Token).ConfigureAwait(false)
                : await SendWithSessionAsync(_client, HttpMethod.Post, uri, headers, content, timeout.Token).ConfigureAwait(false);
            var setCookies = ReadSetCookies(response);
            var hasCf = response.Headers.TryGetValues("cf-mitigated", out var cfValues) &&
                cfValues.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));
            var payload = await ReadPayloadAsync(response, uri, maxBytes, timeout.Token).ConfigureAwait(false);
            return new HttpExchangeResult(payload, response.StatusCode, setCookies, hasCf);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("提交超时，请稍后重试。", ex);
        }
    }

    /// <summary>
    /// Submits an application/json POST request using the shared DNS/timeout/decompression pipeline.
    /// </summary>
    public async Task<HttpExchangeResult> PostJsonAsync(
        string url,
        string jsonBody,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持有效的 HTTP/HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(20));
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        try
        {
            using var response = await SendFollowingAsync(HttpMethod.Post, uri, headers, content, false, timeout.Token).ConfigureAwait(false);
            var setCookies = ReadSetCookies(response);
            var hasCf = response.Headers.TryGetValues("cf-mitigated", out var cfValues) &&
                cfValues.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));
            var payload = await ReadPayloadAsync(response, uri, maxBytes, timeout.Token).ConfigureAwait(false);
            return new HttpExchangeResult(payload, response.StatusCode, setCookies, hasCf);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("提交超时，请稍后重试。", ex);
        }
    }

    private static IReadOnlyList<string> ReadSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];

    private static async Task<HttpPayload> ReadPayloadAsync(HttpResponseMessage response, Uri fallbackUri, int maxBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("响应内容超过允许大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
            ? (int)response.Content.Headers.ContentLength.Value
            : 64 * 1024);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maxBytes) throw new InvalidDataException("响应内容超过允许大小。");
            await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return new HttpPayload(memory.ToArray(), response.Content.Headers.ContentType?.MediaType ?? string.Empty, response.RequestMessage?.RequestUri ?? fallbackUri);
    }

    internal async Task<string?> ProbeMediaTypeAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await SendFollowingAsync(HttpMethod.Head, uri, headers, null, true, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentType?.MediaType : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    internal async Task<HttpPayload> GetTrustedBytesAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, int maxBytes,
        Func<Uri, bool> allowDestination, CancellationToken token)
    {
        using var response = await SendFollowingAsync(HttpMethod.Get, uri, headers, null, true, token, allowDestination).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadPayloadAsync(response, uri, maxBytes, token).ConfigureAwait(false);
    }

    internal async Task<HttpExchangeResult> SendTrustedApiAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        string? jsonBody, int maxBytes, TimeSpan requestTimeout, CancellationToken token, Func<Uri, bool>? allowRedirect = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(requestTimeout);
        using var body = jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await SendFollowingAsync(body is null ? HttpMethod.Get : HttpMethod.Post, uri, headers, body,
            true, timeout.Token, destination => MediaRequestHeaders.SameOrigin(uri, destination) ||
                allowRedirect?.Invoke(destination) == true).ConfigureAwait(false);
        var payload = await ReadPayloadAsync(response, uri, maxBytes, timeout.Token).ConfigureAwait(false);
        return new HttpExchangeResult(payload, response.StatusCode, [], false);
    }

    private async Task<HttpResponseMessage> SendFollowingAsync(HttpMethod method, Uri origin,
        IReadOnlyDictionary<string, string>? headers, HttpContent? content, bool scopeReferer, CancellationToken token,
        Func<Uri, bool>? allowDestination = null)
    {
        if (!MediaRequestHeaders.IsHttpAddress(origin)) throw new HttpRequestException("请求地址无效。");
        var target = origin;
        for (var count = 0; ; count++)
        {
            if (allowDestination is not null && !allowDestination(target))
                throw new HttpRequestException("HTTP 请求跳转到了未受信任的地址。");
            var sameOrigin = MediaRequestHeaders.SameOrigin(origin, target);
            var scoped = headers is null ? null : MediaRequestHeaders.ForTarget(headers, origin, target);
            var response = await SendWithSessionAsync(_client, method, target, scoped, content, token,
                addDefaultReferer: !scopeReferer || sameOrigin, allowSession: sameOrigin && allowDestination is null).ConfigureAwait(false);
            if (!MediaRequestHeaders.IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;
            using (response)
            {
                if (count >= 7) throw new HttpRequestException("HTTP 重定向次数过多。");
                var next = MediaRequestHeaders.RedirectTarget(target, location);
                var nextMethod = MediaRequestHeaders.RedirectMethod(method, response.StatusCode);
                if (nextMethod != method) content = null;
                if (content is not null && !MediaRequestHeaders.SameOrigin(target, next))
                    throw new HttpRequestException("HTTP 请求正文不能跨域重定向。");
                method = nextMethod;
                target = next;
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithSessionAsync(HttpClient client, HttpMethod method, Uri uri,
        IReadOnlyDictionary<string, string>? headers, HttpContent? content, CancellationToken token,
        bool addDefaultReferer = true, bool allowSession = true)
    {
        var effectiveHeaders = headers;
        if (allowSession && _sessions is not null)
        {
            var hasExplicitCookie = headers is not null && headers.Keys.Any(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
            if (!hasExplicitCookie)
            {
                var session = await _sessions.GetAsync(uri, token).ConfigureAwait(false);
                if (session is not null)
                {
                    var merged = headers is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                    merged["Cookie"] = session.CookieHeader;
                    if (!string.IsNullOrWhiteSpace(session.UserAgent))
                        merged["User-Agent"] = session.UserAgent;
                    effectiveHeaders = merged;
                }
            }
        }
        return await SendAsync(client, method, uri, effectiveHeaders, content, token, addDefaultReferer).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri,
        IReadOnlyDictionary<string, string>? headers, HttpContent? content, CancellationToken token, bool addDefaultReferer = true)
    {
        uri = AsWireUri(uri);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (addDefaultReferer) request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.Remove(key);
                if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(value, UriKind.Absolute, out var referer) && MediaRequestHeaders.IsHttpAddress(referer))
                {
                    request.Headers.Referrer = AsWireUri(referer);
                    continue;
                }
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        finally { request.Content = null; }
    }

    private static Uri AsWireUri(Uri uri) => uri.HostNameType == UriHostNameType.Dns &&
        !uri.Host.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase)
        ? new UriBuilder(uri) { Host = uri.IdnHost }.Uri
        : uri;

    public async Task<string> GetStringAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetBytesAsync(url, headers, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DecodeText(payload.Content);
    }

    internal static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsDns) _dns.Dispose();
    }
}
