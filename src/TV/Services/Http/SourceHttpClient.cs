using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace TV.Services.Http;

public sealed record HttpPayload(byte[] Content, string ContentType, Uri FinalUri);

public sealed class SourceHttpClient
{
    public const string DefaultUserAgent = "okhttp/3.12.11";
    private readonly HttpClient _client;
    private readonly Lazy<HttpClient> _mediaClient;
    private readonly DnsResolverService _dns;

    public SourceHttpClient(DnsResolverService? dns = null)
    {
        _dns = dns ?? new DnsResolverService(new Persistence.DatabaseService());
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _client = CreateClient(true);
        _mediaClient = new Lazy<HttpClient>(() => CreateClient(false));
    }

    private HttpClient CreateClient(bool allowRedirects) => new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowRedirects,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            ConnectCallback = _dns.ConnectAsync,
            UseCookies = allowRedirects
        });

    public async Task<HttpPayload> GetBytesAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 12 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        bool scopeRedirectHeaders = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持有效的 HTTP/HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = scopeRedirectHeaders
            ? await SendMediaAsync(HttpMethod.Get, uri, headers, timeout.Token).ConfigureAwait(false)
            : await SendAsync(_client, HttpMethod.Get, uri, headers, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("响应内容超过允许大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream(response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
            ? (int)response.Content.Headers.ContentLength.Value
            : 64 * 1024);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maxBytes) throw new InvalidDataException("响应内容超过允许大小。");
            await memory.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
        return new HttpPayload(memory.ToArray(), response.Content.Headers.ContentType?.MediaType ?? string.Empty, response.RequestMessage?.RequestUri ?? uri);
    }

    internal async Task<string?> ProbeMediaTypeAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await SendMediaAsync(HttpMethod.Head, uri, headers, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentType?.MediaType : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private async Task<HttpResponseMessage> SendMediaAsync(HttpMethod method, Uri origin, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        var target = origin;
        for (var count = 0; ; count++)
        {
            var scoped = headers is null ? null : MediaRequestHeaders.ForTarget(headers, origin, target);
            var response = await SendAsync(_mediaClient.Value, method, target, scoped, token,
                addDefaultReferer: MediaRequestHeaders.SameOrigin(origin, target)).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                return response;
            using (response)
            {
                if (count >= 7 || !Uri.TryCreate(target, response.Headers.Location, out var next) || next.Scheme is not ("http" or "https") ||
                    next.UserInfo.Length > 0 || target.Scheme == "https" && next.Scheme != "https")
                    throw new HttpRequestException("媒体重定向无效。");
                target = next;
            }
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri,
        IReadOnlyDictionary<string, string>? headers, CancellationToken token, bool addDefaultReferer = true)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (addDefaultReferer) request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.Remove(key);
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

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
}
