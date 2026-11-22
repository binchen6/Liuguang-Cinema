using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Services.Persistence;

namespace TV.Services.Http;

public enum SiteVerificationKind
{
    ImageCaptcha,
    BrowserChallenge
}

public sealed record ImageCaptchaChallenge(
    Uri FormActionUri,
    Uri CaptchaImageUri,
    string CodeFieldName,
    IReadOnlyList<KeyValuePair<string, string>> HiddenFields,
    string SeedCookies,
    Uri RefererUri,
    string UserAgent);

public sealed class SiteVerificationRequiredException : Exception
{
    public SiteVerificationRequiredException(
        string siteKey,
        string siteName,
        Uri targetUri,
        SiteVerificationKind kind,
        string message,
        ImageCaptchaChallenge? imageChallenge = null)
        : base(message)
    {
        SiteKey = siteKey;
        SiteName = siteName;
        TargetUri = targetUri;
        Kind = kind;
        ImageChallenge = imageChallenge;
    }

    public string SiteKey { get; }
    public string SiteName { get; }
    public Uri TargetUri { get; }
    public SiteVerificationKind Kind { get; }
    public ImageCaptchaChallenge? ImageChallenge { get; }
}

public sealed record SiteSession(
    string Origin,
    string CookieHeader,
    string UserAgent,
    DateTimeOffset SavedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Persists user-verified per-origin cookies and matching User-Agent encrypted with DPAPI.
/// Cookies are strictly scoped to their exact scheme+host+port origin and never forwarded cross-origin.
/// </summary>
public sealed class SiteSessionStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);
    private readonly DatabaseService _database;
    private readonly ConcurrentDictionary<string, SiteSession?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SiteSessionStore(DatabaseService database) => _database = database;

    public static string NormalizeOrigin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    public SiteSession? TryGetCached(Uri uri)
    {
        var origin = NormalizeOrigin(uri);
        if (_cache.TryGetValue(origin, out var session) && session is not null && !session.IsExpired)
            return session;
        return null;
    }

    public async Task<SiteSession?> GetAsync(Uri uri, CancellationToken token = default)
    {
        var origin = NormalizeOrigin(uri);
        if (_cache.TryGetValue(origin, out var cached))
            return cached is not null && !cached.IsExpired ? cached : null;

        try
        {
            var raw = await _database.GetSettingAsync(SettingKey(origin), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _cache[origin] = null;
                return null;
            }
            var plain = SecretStore.Unprotect(raw);
            if (string.IsNullOrWhiteSpace(plain))
            {
                _cache[origin] = null;
                return null;
            }
            var session = JsonSerializer.Deserialize<SiteSession>(plain);
            if (session is null || session.IsExpired || string.IsNullOrWhiteSpace(session.CookieHeader))
            {
                _cache[origin] = null;
                return null;
            }
            _cache[origin] = session;
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"读取站点验证会话失败 ({origin}): {ex.GetType().Name}");
            _cache[origin] = null;
            return null;
        }
    }

    public async Task SaveAsync(Uri uri, string cookieHeader, string userAgent, TimeSpan? ttl = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return;
        var origin = NormalizeOrigin(uri);
        var now = DateTimeOffset.UtcNow;
        var session = new SiteSession(origin, cookieHeader.Trim(), userAgent.Trim(), now, now.Add(ttl ?? DefaultTtl));
        _cache[origin] = session;
        try
        {
            var json = JsonSerializer.Serialize(session);
            await _database.SetSettingAsync(SettingKey(origin), SecretStore.Protect(json), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"保存站点验证会话失败 ({origin}): {ex.GetType().Name}");
        }
    }

    public async Task ClearAsync(Uri uri, CancellationToken token = default)
    {
        var origin = NormalizeOrigin(uri);
        _cache[origin] = null;
        try
        {
            await _database.SetSettingAsync(SettingKey(origin), string.Empty, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"清除站点验证会话失败 ({origin}): {ex.GetType().Name}");
        }
    }

    public static string MergeCookies(string existing, IEnumerable<string> setCookieHeaders)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            foreach (var part in existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0) map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        foreach (var header in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            var first = header.Split(';', 2)[0];
            var eq = first.IndexOf('=');
            if (eq > 0)
            {
                var name = first[..eq].Trim();
                var value = first[(eq + 1)..].Trim();
                if (name.Length > 0 && !value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                    map[name] = value;
            }
        }
        return string.Join("; ", map.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string SettingKey(string origin) =>
        "site.session." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..24];
}
