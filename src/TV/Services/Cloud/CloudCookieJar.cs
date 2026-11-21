namespace TV.Services.Cloud;

/// <summary>
/// 网盘接口会在每次调用时轮换会话 Cookie（夸克的 __puus 等），而播放地址的 CDN 只接受最新值。
/// 这里把存储的凭据与响应里的 Set-Cookie 合并成一份，供后续接口请求和播放地址复用。
/// </summary>
public sealed class CloudCookieJar
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void MergeHeader(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return;
        lock (_gate)
            foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) MergePair(part);
    }

    public void Absorb(IReadOnlyList<string> setCookieHeaders)
    {
        lock (_gate)
            foreach (var header in setCookieHeaders)
            {
                var end = header.IndexOf(';');
                MergePair(end < 0 ? header : header[..end]);
            }
    }

    public string ToHeader()
    {
        lock (_gate) return string.Join("; ", _values.Select(pair => pair.Key + "=" + pair.Value));
    }

    private void MergePair(string pair)
    {
        var index = pair.IndexOf('=');
        if (index <= 0) return;
        var name = pair[..index].Trim();
        if (name.Length == 0) return;
        _values[name] = pair[(index + 1)..].Trim();
    }
}
