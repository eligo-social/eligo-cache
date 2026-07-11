using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;

namespace TenantContextCache.Tests;

/// <summary>
/// A minimal in-process <see cref="IDistributedCache"/> used to stand in for a real
/// distributed L2 (Redis, etc.) in tests. Records read/write counts so tests can assert
/// that FusionCache actually exercised the L2 layer. TTLs are intentionally ignored —
/// tests never rely on distributed expiry.
/// </summary>
public class InProcessDistributedCache : IDistributedCache
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public int SetCount { get; private set; }
    public int GetCount { get; private set; }

    public byte[] Get(string key)
    {
        GetCount++;
        return _store.TryGetValue(key, out var value) ? value : null;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        SetCount++;
        _store[key] = value;
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => _store.TryRemove(key, out _);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
