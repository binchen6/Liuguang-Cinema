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
}
