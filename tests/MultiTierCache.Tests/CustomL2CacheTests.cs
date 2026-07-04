using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace MultiTierCache.Tests;

/// <summary>
/// Tests for custom L2 cache support (WithCustomL2 builder overloads) and the
/// L1/L2 wiring that keeps the two layers as distinct ICacheLayer instances.
/// </summary>
[TestFixture]
public class CustomL2CacheTests
{
    /// <summary>
    /// Recording ICacheLayer used as a spy so tests can assert how the L2 layer
    /// was exercised. Backed by an in-memory store so reads/writes work end to end.
    /// </summary>
    private class RecordingCacheLayer : ICacheLayer
    {
        private readonly ConcurrentDictionary<string, object> _store = new();

        public int SetCount { get; private set; }
        public int GetCount { get; private set; }

        public Task<T> GetAsync<T>(string key)
        {
            GetCount++;
            return Task.FromResult(_store.TryGetValue(key, out var v) ? (T)v : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl)
        {
            SetCount++;
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string key) => Task.FromResult(_store.ContainsKey(key));
    }

    /// <summary>Parameterless custom layer for the generic WithCustomL2&lt;T&gt; overload.</summary>
    private class ParameterlessCustomL2 : RecordingCacheLayer { }

    private static IMultiTierCache BuildCache(Action<MultiTierCacheBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddMultiTierCache(configure);
        return services.BuildServiceProvider().GetRequiredService<IMultiTierCache>();
    }

    [Test]
    public async Task WithCustomL2_Instance_IsUsedAsL2Layer()
    {
        // Arrange
        var custom = new RecordingCacheLayer();
        var cache = BuildCache(c => c.WithCustomL2(custom));

        // Act
        await cache.SetAsync("acme", "user-1", "value");

        // Assert - the provided instance received the write
        var direct = await custom.GetAsync<string>("tenant:acme:user-1");
        direct.Should().Be("value");
    }

    [Test]
    public async Task WithCustomL2_Factory_ReceivesServiceProviderAndIsUsed()
    {
        // Arrange
        var custom = new RecordingCacheLayer();
        var factoryCalled = false;
        var cache = BuildCache(c => c.WithCustomL2(sp =>
        {
            factoryCalled = true;
            sp.Should().NotBeNull();
            return custom;
        }));

        // Act
        await cache.SetAsync("acme", "user-1", "value");

        // Assert
        factoryCalled.Should().BeTrue();
        (await custom.GetAsync<string>("tenant:acme:user-1")).Should().Be("value");
    }

    [Test]
    public async Task WithCustomL2_Generic_ResolvesTypeFromDiAndIsUsed()
    {
        // Arrange
        var cache = BuildCache(c => c.WithCustomL2<ParameterlessCustomL2>());

        // Act
        await cache.SetAsync("acme", "user-1", "value");
        var result = await cache.GetAsync<string>("acme", "user-1");

        // Assert
        result.Should().Be("value");
    }

    [Test]
    public async Task WithCustomL2_L1AndL2AreDistinctLayers()
    {
        // Arrange - a single Set should write to L1 (in-memory) and the custom L2
        // exactly once each. If both layers resolved to the same instance, the
        // spy would see two writes.
        var custom = new RecordingCacheLayer();
        var cache = BuildCache(c => c.WithCustomL2(custom));

        // Act
        await cache.SetAsync("acme", "user-1", "value");

        // Assert - L2 was written exactly once (L1 is a separate InMemoryL1Cache)
        custom.SetCount.Should().Be(1);
    }

    [Test]
    public async Task WithCustomL2_L1MissFallsBackToCustomL2AndPopulatesL1()
    {
        // Arrange - pre-populate only the custom L2, leaving L1 empty
        var custom = new RecordingCacheLayer();
        var cache = BuildCache(c => c.WithCustomL2(custom));
        await custom.SetAsync("tenant:acme:user-1", "from-l2", TimeSpan.FromHours(1));

        // Act - first read misses L1 and falls back to the custom L2
        var first = await cache.GetAsync<string>("acme", "user-1");

        // Assert
        first.Should().Be("from-l2");
        custom.GetCount.Should().Be(1);

        // Act - second read should now be served from L1 (no extra L2 hit)
        var second = await cache.GetAsync<string>("acme", "user-1");
        second.Should().Be("from-l2");
        custom.GetCount.Should().Be(1);
    }
}
