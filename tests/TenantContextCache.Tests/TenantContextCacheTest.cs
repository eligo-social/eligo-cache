using FluentAssertions;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class TenantContextCacheTests
{
    private TenantContextCache _cache;

    [SetUp]
    public void Setup()
    {
        // Backed by a real FusionCache (L1 in-memory only) so tests exercise the
        // actual hybrid-cache behaviour rather than a hand-mocked layer.
        _cache = TestCacheFactory.CreateTenantContextCache();
    }

    [Test]
    public async Task GetAsync_ReturnsValue_AfterSet()
    {
        await _cache.SetAsync("tenant1", "key1", "cached-value");

        var result = await _cache.GetAsync<string>("tenant1", "key1");

        result.Should().Be("cached-value");
    }

    [Test]
    public async Task GetAsync_ReturnsDefault_WhenMissing()
    {
        var result = await _cache.GetAsync<string>("tenant1", "key1");

        result.Should().BeNull();
    }

    [Test]
    public async Task GetAsync_WithEmptyTenantId_ReturnsDefault()
    {
        var result = await _cache.GetAsync<string>(null, "key1");

        result.Should().BeNull();
    }

    [Test]
    public async Task SetAsync_WithEmptyTenantId_IsNoOp()
    {
        await _cache.SetAsync(null, "key1", "value");

        // Nothing was stored, so a subsequent read for a valid key is still a miss.
        (await _cache.GetAsync<string>("tenant1", "key1")).Should().BeNull();
    }

    [Test]
    public async Task Keys_AreIsolatedPerTenant()
    {
        await _cache.SetAsync("tenant1", "shared-key", "tenant1-value");
        await _cache.SetAsync("tenant2", "shared-key", "tenant2-value");

        (await _cache.GetAsync<string>("tenant1", "shared-key")).Should().Be("tenant1-value");
        (await _cache.GetAsync<string>("tenant2", "shared-key")).Should().Be("tenant2-value");
    }

    [Test]
    public async Task RemoveAsync_RemovesEntry()
    {
        await _cache.SetAsync("tenant1", "key1", "value");

        await _cache.RemoveAsync("tenant1", "key1");

        (await _cache.GetAsync<string>("tenant1", "key1")).Should().BeNull();
    }

    [Test]
    public async Task RemoveAllTenantAsync_EvictsOnlyThatTenantsKeys()
    {
        await _cache.SetAsync("tenant1", "key1", "value1");
        await _cache.SetAsync("tenant1", "key2", "value2");
        await _cache.SetAsync("tenant2", "key1", "other");

        await _cache.RemoveAllTenantAsync("tenant1");

        (await _cache.GetAsync<string>("tenant1", "key1")).Should().BeNull();
        (await _cache.GetAsync<string>("tenant1", "key2")).Should().BeNull();
        // A different tenant's data is untouched.
        (await _cache.GetAsync<string>("tenant2", "key1")).Should().Be("other");
    }
}
