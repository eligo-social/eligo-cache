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
    public async Task WithCacheKeyPrefix_OverridesSeparator()
    {
        var (cache, l2) = BuildCache(c => c.WithCacheKeyPrefix("TENANT-CONTENT", "-"));

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains("TENANT-CONTENT-acme-user-1"));
    }

    [Test]
    public void WithCacheKeyPrefix_RejectsBlank()
    {
        var act = () => BuildCache(c => c.WithCacheKeyPrefix("  "));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCacheKeyPrefix_RejectsBlankSeparator()
    {
        // A blank separator would run segments together, so tenant "a" + key "bc" and tenant
        // "ab" + key "c" would collide.
        var act = () => BuildCache(c => c.WithCacheKeyPrefix("myapp", ""));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task WithCacheKeyBuilder_TakesOverTheWholeLayout()
    {
        var (cache, l2) = BuildCache(c => c.WithCacheKeyBuilder(new SuffixKeyBuilder()));

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains("user-1@acme"));
        l2.Keys.Should().NotContain(k => k.Contains("tenant:acme:user-1"));
    }

    [Test]
    public void WithCacheKeyBuilder_Throws_WhenCombinedWithWithCacheKeyPrefix()
    {
        var act = () => BuildCache(c => c
            .WithCacheKeyPrefix("myapp")
            .WithCacheKeyBuilder(new SuffixKeyBuilder()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithCacheKeyPrefix*");
    }

    [Test]
    public async Task WithCacheKeyBuilder_TwiceIsNotAConflict_LastOneWins()
    {
        var (cache, l2) = BuildCache(c => c
            .WithCacheKeyBuilder(new SuffixKeyBuilder())
            .WithCacheKeyBuilder(new SuffixKeyBuilder()));

        await cache.SetAsync("acme", "user-1", "value");

        l2.Keys.Should().Contain(k => k.Contains("user-1@acme"));
    }

    [Test]
    public void WithCacheKeyBuilder_RejectsNull()
    {
        var nullInstance = () => BuildCache(c => c.WithCacheKeyBuilder((ICacheKeyBuilder)null));
        var nullFactory = () => BuildCache(c =>
            c.WithCacheKeyBuilder((Func<IServiceProvider, ICacheKeyBuilder>)null));

        nullInstance.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Tenant as a key suffix rather than a prefix — a layout the built-in one cannot express.</summary>
    private sealed class SuffixKeyBuilder : ICacheKeyBuilder
    {
        public string BuildKey(string tenantId, string key) => $"{key}@{tenantId}";
        public string TenantTag(string tenantId) => $"@{tenantId}";
    }

    [Test]
    public async Task WithTenantInfoKeyPrefix_OverridesTheLibrarysOwnKey()
    {
        var l2 = new InProcessDistributedCache();
        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantStub>(_ => Task.FromResult(new TenantStub()))
            .WithCustomL2(l2)
            .WithTenantInfoKeyPrefix("ti"));

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<ITenantInfoProvider>().GetTenantInfoAsync("acme");

        l2.Keys.Should().Contain(k => k.Contains("tenant:acme:ti:TenantStub"));
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
