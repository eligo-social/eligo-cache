using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace TenantContextCache.Tests;

/// <summary>
/// Verifies the configurable cache-key prefix (WithCacheKeyPrefix). The prefix is the leading
/// segment of every L2 key and per-tenant tag; when unconfigured it falls back to "tenant".
/// </summary>
[TestFixture]
public class CacheKeyPrefixTests
{
    private sealed class TenantStub { }

    private static (ITenantContextCache cache, InProcessDistributedCache l2) BuildCache(
        Action<TenantContextCacheBuilder> configure)
    {
        var l2 = new InProcessDistributedCache();
        var services = new ServiceCollection();
        services.AddTenantContextCache(c =>
        {
            c.WithTenantDataFetch<TenantStub>(_ => Task.FromResult<TenantStub>(null));
            c.WithCustomL2(l2);
            configure(c);
        });
        var cache = services.BuildServiceProvider().GetRequiredService<ITenantContextCache>();
        return (cache, l2);
    }

    [Test]
    public async Task DefaultPrefix_IsTenant()
    {
        var (cache, l2) = BuildCache(_ => { });

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains("tenant:acme:user-1"));
    }

    [Test]
    public async Task WithCacheKeyPrefix_OverridesLeadingSegment()
    {
        var (cache, l2) = BuildCache(c => c.WithCacheKeyPrefix("myapp"));

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains("myapp:acme:user-1"));
        l2.Keys.Should().NotContain(k => k.Contains("tenant:acme:user-1"));
    }

    [Test]
    public void WithCacheKeyPrefix_RejectsBlank()
    {
        var act = () => BuildCache(c => c.WithCacheKeyPrefix("  "));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task DirectConstructor_FallsBackToDefault_WhenPrefixBlank()
    {
        // The constructor guards against a blank prefix, preserving the original layout.
        var l2 = new InProcessDistributedCache();
        var cache = new TenantContextCache(TestCacheFactory.CreateFusionCache(l2), cacheKeyPrefix: " ");

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains($"{TenantContextCache.DefaultCacheKeyPrefix}:acme:user-1"));
    }
}
