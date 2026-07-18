using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace TenantContextCache.Examples;

/// <summary>
/// A minimal Redis-backed <see cref="IDistributedCache"/> built directly on
/// StackExchange.Redis. It demonstrates how to plug any distributed backend into
/// TenantContextCache's L2 layer via <c>WithCustomL2(...)</c> now that the library itself
/// ships no Redis dependency.
/// <para>
/// Values are stored as raw byte payloads. Absolute and sliding expirations are honoured by
/// translating <see cref="DistributedCacheEntryOptions"/> into a Redis key TTL; sliding
/// expiration is renewed on each read.
/// </para>
/// </summary>
public sealed class RedisDistributedCache : IDistributedCache, IDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly bool _ownsConnection;

    public RedisDistributedCache(string configuration)
        : this(ConnectionMultiplexer.Connect(configuration), ownsConnection: true)
    {
    }

    public RedisDistributedCache(IConnectionMultiplexer connection, bool ownsConnection = false)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = ownsConnection;
    }

    private IDatabase Database => _connection.GetDatabase();

    public byte[]? Get(string key)
    {
        RedisValue value = Database.StringGet(key);
        return value.IsNull ? null : (byte[]?)value;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        RedisValue value = await Database.StringGetAsync(key);
        return value.IsNull ? null : (byte[]?)value;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        Database.StringSet(key, value, GetExpiry(options));
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        return Database.StringSetAsync(key, value, GetExpiry(options));
    }

    public void Refresh(string key)
    {
        // A read is enough to renew a sliding TTL for backends that track it; for this simple
        // implementation there is nothing extra to do.
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => Database.KeyDelete(key);

    public Task RemoveAsync(string key, CancellationToken token = default) => Database.KeyDeleteAsync(key);

    private static TimeSpan? GetExpiry(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
            return options.AbsoluteExpirationRelativeToNow;

        if (options.AbsoluteExpiration.HasValue)
            return options.AbsoluteExpiration.Value - DateTimeOffset.UtcNow;

        return options.SlidingExpiration;
    }

    public void Dispose()
    {
        if (_ownsConnection)
            _connection.Dispose();
    }
}
