using System.Net;
using System.Text;
using System.Text.Json;
using TV.Services.Http;

namespace TV.Services.Cloud;

public sealed record CloudHttpResult(HttpStatusCode StatusCode, JsonElement Json, IReadOnlyList<string> Cookies);
public sealed record CloudRawResult(HttpStatusCode StatusCode, byte[] Content, string Text, Uri FinalUri, IReadOnlyList<string> Cookies)
{
    public Uri? RedirectLocation { get; init; }
}
internal readonly record struct CloudMediaProbe(HttpStatusCode? StatusCode, long? TotalLength, string? FileName, string? ErrorText, string? ContentType);

public sealed class CloudHttpClient : IDisposable
{
    internal const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36";
    private readonly HttpClient _client;
    private readonly bool _allowAutoRedirect;

    public CloudHttpClient(DnsResolverService? dns = null, bool allowAutoRedirect = true)
        : this(CreateHandler(dns), allowAutoRedirect)
    {
    }

    internal CloudHttpClient(HttpMessageHandler handler, bool allowAutoRedirect)
    {
        _client = new HttpClient(handler);
        _allowAutoRedirect = allowAutoRedirect;
    }

    private static SocketsHttpHandler CreateHandler(DnsResolverService? dns)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        if (dns is not null) handler.ConnectCallback = dns.ConnectAsync;
        return handler;
    }

    public void Dispose() => _client.Dispose();

    public async Task<CloudHttpResult> SendAsync(
        HttpMethod method,
        string url,
        object? json = null,
        IReadOnlyDictionary<string, string>? headers = null,
        IEnumerable<KeyValuePair<string, string>>? form = null,
        CancellationToken cancellationToken = default)
    {
        var origin = new Uri(url, UriKind.Absolute);
        using HttpContent? body = json is not null ? new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") :
            form is not null ? new FormUrlEncodedContent(form) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var (message, finalUri) = await SendHeadersAsync(method, origin, headers, body, null, timeout.Token).ConfigureAwait(false);
        using var response = message;
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        JsonElement payload = default;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                payload = document.RootElement.Clone();
            }
            catch (JsonException) when (!response.IsSuccessStatusCode) { }
            catch (JsonException ex) { throw new InvalidDataException($"网盘返回了非 JSON 内容（HTTP {(int)response.StatusCode}）。", ex); }
        }
        return new CloudHttpResult(response.StatusCode, payload, ReadCookies(response, origin, finalUri));
    }

    public Task<CloudHttpResult> PostJsonAsync(string url, object json, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, url, json, headers, cancellationToken: cancellationToken);

    public Task<CloudHttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, url, headers: headers, cancellationToken: cancellationToken);

    public Task<CloudHttpResult> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, url, headers: headers, form: form, cancellationToken: cancellationToken);

    public async Task<CloudRawResult> SendRawAsync(HttpMethod method, string url,
        IReadOnlyDictionary<string, string>? headers = null,
        IEnumerable<KeyValuePair<string, string>>? form = null,
        CancellationToken cancellationToken = default)
    {
        var origin = new Uri(url, UriKind.Absolute);
        using var body = form is not null ? new FormUrlEncodedContent(form) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var (message, finalUri) = await SendHeadersAsync(method, origin, headers, body, null, timeout.Token).ConfigureAwait(false);
        using var response = message;
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return new CloudRawResult(response.StatusCode, bytes, Encoding.UTF8.GetString(bytes), finalUri, ReadCookies(response, origin, finalUri))
        {
            RedirectLocation = response.Headers.Location
        };
    }

    /// <summary>
    /// Inspect media headers and a bounded prefix of any text error page without buffering the media.
    /// </summary>
    internal async Task<CloudMediaProbe> ProbeMediaAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var (message, _) = await SendHeadersAsync(HttpMethod.Get, new Uri(url, UriKind.Absolute), headers, null, "bytes=0-0", timeout.Token).ConfigureAwait(false);
            using var response = message;
            var totalLength = response.IsSuccessStatusCode ? response.Content.Headers.ContentRange?.Length : null;
            if (totalLength is null && response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentEncoding.Count == 0)
                totalLength = response.Content.Headers.ContentLength;
            var disposition = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            string? errorText = null;
            if (response.StatusCode != HttpStatusCode.Forbidden && contentType is "text/html" or "application/json" or "text/plain")
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var prefix = new byte[4096];
                var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, false, timeout.Token).ConfigureAwait(false);
                errorText = SourceHttpClient.DecodeText(prefix.AsSpan(0, read).ToArray());
            }
            return new CloudMediaProbe(response.StatusCode, totalLength, disposition, errorText, contentType);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Diagnostics.Log($"网盘媒体探测跳过: {ex.GetType().Name}");
            return default;
        }
    }

    private static string[] ReadCookies(HttpResponseMessage response, Uri origin, Uri finalUri) =>
        MediaRequestHeaders.SameOrigin(origin, finalUri) && response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray() : [];

    private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendHeadersAsync(HttpMethod method, Uri origin,
        IReadOnlyDictionary<string, string>? headers, HttpContent? content, string? range, CancellationToken token)
    {
        if (!MediaRequestHeaders.IsHttpAddress(origin)) throw new HttpRequestException("网盘地址无效。");
        var uri = origin;
        for (var count = 0; ; count++)
        {
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            if (headers is not null)
            {
                foreach (var (name, value) in MediaRequestHeaders.ForTarget(headers, origin, uri))
                {
                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            finally { request.Content = null; } // The caller owns the body across same-origin redirects.
            if (!_allowAutoRedirect || !MediaRequestHeaders.IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return (response, uri);
            using (response)
            {
                if (count >= 7) throw new HttpRequestException("网盘重定向次数过多。");
                var target = MediaRequestHeaders.RedirectTarget(uri, location);
                var nextMethod = MediaRequestHeaders.RedirectMethod(method, response.StatusCode);
                if (nextMethod != method) content = null;
                if (content is not null && !MediaRequestHeaders.SameOrigin(uri, target))
                    throw new HttpRequestException("网盘请求正文不能跨域重定向。");
                method = nextMethod;
                uri = target;
            }
        }
    }
}
