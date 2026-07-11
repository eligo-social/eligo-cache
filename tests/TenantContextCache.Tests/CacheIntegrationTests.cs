using FluentAssertions;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class CacheIntegrationTests
{
    [Test]
    public async Task FullWorkflow_L1Miss_PopulatesFromL2()
    {
        // Two independent TenantContextCache instances (each with its own in-memory L1)
        // sharing a single distributed L2 - a stand-in for two application nodes.
        var sharedL2 = new InProcessDistributedCache();
        var nodeA = TestCacheFactory.CreateTenantContextCache(sharedL2);
        var nodeB = TestCacheFactory.CreateTenantContextCache(sharedL2);

        await nodeA.SetAsync("acme", "user-123", new { id = 1, name = "John" });

        // Node B has never seen this key in its L1, so it must fall back to the shared L2.
        var result = await nodeB.GetAsync<Dictionary<string, object>>("acme", "user-123");

        result.Should().NotBeNull();
        sharedL2.SetCount.Should().BeGreaterThan(0);
        sharedL2.GetCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task FullWorkflow_MultiTenantIsolation()
    {
        var cache = TestCacheFactory.CreateTenantContextCache();

        await cache.SetAsync("tenant-a", "config", "dark");
        await cache.SetAsync("tenant-b", "config", "light");

        (await cache.GetAsync<string>("tenant-a", "config")).Should().Be("dark");
        (await cache.GetAsync<string>("tenant-b", "config")).Should().Be("light");
    }

    [Test]
    public async Task FullWorkflow_CacheKeyIsolation()
    {
        var cache = TestCacheFactory.CreateTenantContextCache();

        await cache.SetAsync("tenant1", "shared-key", "tenant1-value");
        await cache.SetAsync("tenant2", "shared-key", "tenant2-value");

        (await cache.GetAsync<string>("tenant1", "shared-key")).Should().Be("tenant1-value");
        (await cache.GetAsync<string>("tenant2", "shared-key")).Should().Be("tenant2-value");
    }

    [Test]
    public async Task FullWorkflow_RemoveAllTenant_ClearsTenantAcrossL1AndL2()
    {
        var sharedL2 = new InProcessDistributedCache();
        var cache = TestCacheFactory.CreateTenantContextCache(sharedL2);

        await cache.SetAsync("acme", "a", "1");
        await cache.SetAsync("acme", "b", "2");
        await cache.SetAsync("other", "a", "keep");

        await cache.RemoveAllTenantAsync("acme");

        (await cache.GetAsync<string>("acme", "a")).Should().BeNull();
        (await cache.GetAsync<string>("acme", "b")).Should().BeNull();
        (await cache.GetAsync<string>("other", "a")).Should().Be("keep");
    }
}
