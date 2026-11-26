using System.Net;

namespace TV.Services.Http;

internal static class MediaRequestHeaders
{
    public static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;

    public static IReadOnlyDictionary<string, string> ForTarget(IReadOnlyDictionary<string, string> headers, Uri origin, Uri target) =>
        SameOrigin(origin, target) ? headers : headers
            .Where(pair => pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public static bool RequiresProxy(IReadOnlyDictionary<string, string> headers) =>
        headers.Keys.Any(key => !key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) &&
            !key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && !key.Equals("Referrer", StringComparison.OrdinalIgnoreCase));

    internal static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    internal static Uri RedirectTarget(Uri current, Uri location)
    {
        if (!Uri.TryCreate(current, location, out var target) || !IsHttpAddress(target) ||
            current.Scheme == "https" && target.Scheme != "https")
            throw new HttpRequestException("HTTP 重定向地址无效。");
        return target;
    }

    internal static bool IsHttpAddress(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    internal static HttpMethod RedirectMethod(HttpMethod method, HttpStatusCode status) =>
        ((int)status is 301 or 302 && method == HttpMethod.Post) ||
        ((int)status == 303 && method != HttpMethod.Head) ? HttpMethod.Get : method;
}
