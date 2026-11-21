using System.Collections.Concurrent;

namespace TV.Services.Cloud;

internal sealed class CloudShareTokenCache
{
    private sealed record Entry(Lazy<Task<string>> Request, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<(string ShareId, string Passcode), Entry> _entries = new();

    public async Task<string> GetAsync(string shareId, string passcode, Func<Task<string>> fetch, CancellationToken cancellationToken)
    {
        var key = (shareId, passcode);
        var now = DateTimeOffset.UtcNow;
        if (_entries.Count >= 512)
            foreach (var pair in _entries)
                if (pair.Value.ExpiresAt <= now) _entries.TryRemove(pair);

        var replacement = new Entry(new Lazy<Task<string>>(fetch), now.AddMinutes(10));
        var entry = _entries.AddOrUpdate(key, replacement, (_, current) => current.ExpiresAt > now ? current : replacement);
        var pending = entry.Request.Value;
        try { return await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            if (pending.IsFaulted || pending.IsCanceled) _entries.TryRemove(new(key, entry));
            throw;
        }
    }
}
