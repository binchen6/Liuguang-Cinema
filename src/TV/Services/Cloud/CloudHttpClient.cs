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

public sealed class CloudHttpClient : IDisposable
{
    private readonly HttpClient _client;

    public CloudHttpClient(DnsResolverService? dns = null, bool allowAutoRedirect = true)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
            UseCookies = false,
            AllowAutoRedirect = allowAutoRedirect
        };
        if (dns is not null) handler.ConnectCallback = dns.ConnectAsync;
        _client = new HttpClient(handler);
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
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (headers is not null)
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        if (json is not null) request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        else if (form is not null) request.Content = new FormUrlEncodedContent(form);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
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
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        return new CloudHttpResult(response.StatusCode, payload, cookies);
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
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
        if (headers is not null)
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        return new CloudRawResult(response.StatusCode, bytes, Encoding.UTF8.GetString(bytes),
            response.RequestMessage?.RequestUri ?? new Uri(url), cookies)
        {
            RedirectLocation = response.Headers.Location
        };
    }
}
