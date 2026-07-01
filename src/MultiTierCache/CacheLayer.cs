using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace MultiTierCache;

/// <summary>
    /// L1 cache layer - in-memory with TTL tracking
    /// </summary>
    public interface ICacheLayer
    {
        Task<T> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value, TimeSpan ttl);
        Task RemoveAsync(string key);
        Task<bool> ExistsAsync(string key);
    }

    /// <summary>
    /// In-memory L1 cache implementation
    /// </summary>
    public class InMemoryL1Cache : ICacheLayer
    {
        private class CacheEntry
        {
            public object Value { get; set; }
            public DateTime ExpirationTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

        public Task<T> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.ExpirationTime)
                {
                    return Task.FromResult((T)entry.Value);
                }
                _cache.TryRemove(key, out _);
            }
            return Task.FromResult<T>(default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl)
        {
            var entry = new CacheEntry
            {
                Value = value,
                ExpirationTime = DateTime.UtcNow.Add(ttl)
            };
            _cache.AddOrUpdate(key, entry, (_, __) => entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            _cache.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.ExpirationTime)
                {
                    return Task.FromResult(true);
                }
                _cache.TryRemove(key, out _);
            }
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Redis-based L2 cache implementation
    /// </summary>
    public class RedisL2Cache : ICacheLayer
    {
        private readonly IConnectionMultiplexer _connection;

        public RedisL2Cache(string connectionString)
        {
            _connection = ConnectionMultiplexer.Connect(connectionString);
        }

        public async Task<T> GetAsync<T>(string key)
        {
            var db = _connection.GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.IsNull ? default : JsonSerializer.Deserialize<T>(value.ToString());
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan ttl)
        {
            var db = _connection.GetDatabase();
            var serialized = JsonSerializer.Serialize(value);
            await db.StringSetAsync(key, serialized, ttl);
        }

        public async Task RemoveAsync(string key)
        {
            var db = _connection.GetDatabase();
            await db.KeyDeleteAsync(key);
        }

        public async Task<bool> ExistsAsync(string key)
        {
            var db = _connection.GetDatabase();
            return await db.KeyExistsAsync(key);
        }
    }

    /// <summary>
    /// Hazelcast-based L2 cache implementation (simplified for example)
    /// In production, use the official Hazelcast .NET client
    /// </summary>
    public class HazelcastL2Cache : ICacheLayer
    {
        private readonly string _connectionString;
        private readonly ConcurrentDictionary<string, (object Value, DateTime Expiry)> _cache = new();

        public HazelcastL2Cache(string connectionString)
        {
            _connectionString = connectionString;
        }

        public Task<T> GetAsync<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.Expiry)
                {
                    return Task.FromResult((T)entry.Value);
                }
                _cache.TryRemove(key, out _);
            }
            return Task.FromResult<T>(default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl)
        {
            _cache.AddOrUpdate(key, (value, DateTime.UtcNow.Add(ttl)), (_, __) => (value, DateTime.UtcNow.Add(ttl)));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            _cache.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.Expiry)
                {
                    return Task.FromResult(true);
                }
                _cache.TryRemove(key, out _);
            }
            return Task.FromResult(false);
        }
    }