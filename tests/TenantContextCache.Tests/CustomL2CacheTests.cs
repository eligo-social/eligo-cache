using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace TenantContextCache.Tests;

/// <summary>
/// Tests for custom L2 support (WithCustomL2 builder overloads). Since the migration to
/// FusionCache, the L2 layer is any <see cref="IDistributedCache"/>. FusionCache manages the
/// L2 keys/serialization internally, so these tests assert observable behaviour and that the
/// custom cache is actually exercised, rather than reaching into L2 by a known key.
/// </summary>
[TestFixture]
public class CustomL2CacheTests
{
    /// <summary>Parameterless custom L2 for the generic WithCustomL2&lt;T&gt; overload.</summary>
    private sealed class ParameterlessCustomL2 : InProcessDistributedCache { }

    private sealed class TenantStub { }

    // A tenant-data fetch is required, but these tests exercise the L2 cache layer directly,
    // so a no-op fetch is enough.
    private static ITenantContextCache BuildCache(Action<TenantContextCacheBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddTenantContextCache(c =>
        {
            c.WithTenantDataFetch<TenantStub>(_ => Task.FromResult<TenantStub>(null));
            configure(c);
        });
        return services.BuildServiceProvider().GetRequiredService<ITenantContextCache>();
    }

    [Test]
    public async Task WithCustomL2_Instance_ReceivesWrites()
    {
        var custom = new InProcessDistributedCache();
        var cache = BuildCache(c => c.WithCustomL2(custom));

        await cache.SetAsync("acme", "user-1", "value");

        // The write fanned out to the custom distributed cache.
        custom.SetCount.Should().BeGreaterThan(0);
        (await cache.GetAsync<string>("acme", "user-1")).Should().Be("value");
    }

    [Test]
    public async Task WithCustomL2_Factory_ReceivesServiceProviderAndIsUsed()
    {
        var custom = new InProcessDistributedCache();
        var factoryCalled = false;
        var cache = BuildCache(c => c.WithCustomL2(sp =>
        {
            factoryCalled = true;
            sp.Should().NotBeNull();
            return custom;
        }));

        await cache.SetAsync("acme", "user-1", "value");

        factoryCalled.Should().BeTrue();
        custom.SetCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task WithCustomL2_Generic_ResolvesTypeFromDiAndRoundTrips()
    {
        var cache = BuildCache(c => c.WithCustomL2<ParameterlessCustomL2>());

        await cache.SetAsync("acme", "user-1", "value");
        var result = await cache.GetAsync<string>("acme", "user-1");

        result.Should().Be("value");
    }

    [Test]
    public async Task WithCustomL2_ValueSurvivesInL2_ForAnotherNode()
    {
        // Two service providers sharing the same custom L2 instance model two nodes.
        var shared = new InProcessDistributedCache();
        var nodeA = BuildCache(c => c.WithCustomL2(shared));
        var nodeB = BuildCache(c => c.WithCustomL2(shared));

        await nodeA.SetAsync("acme", "user-1", "from-node-a");

        // Node B's L1 is empty, so serving the value proves it came from the shared L2.
        (await nodeB.GetAsync<string>("acme", "user-1")).Should().Be("from-node-a");
        shared.GetCount.Should().BeGreaterThan(0);
    }
}
