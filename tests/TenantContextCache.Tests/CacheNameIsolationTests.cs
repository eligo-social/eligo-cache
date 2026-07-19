using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace TenantContextCache.Tests;

/// <summary>
/// The library registers its FusionCache under a name (default "tenant-context") instead of the
/// default instance, so a host app can register its own unnamed <see cref="IFusionCache"/> without
/// colliding. These tests verify that coexistence and that the two instances are distinct.
/// </summary>
[TestFixture]
public class CacheNameIsolationTests
{
    private sealed class TenantStub { }

    private static ServiceProvider BuildProvider(Action<TenantContextCacheBuilder> configure = null)
    {
        var services = new ServiceCollection();
        services.AddTenantContextCache(c =>
        {
            c.WithTenantDataFetch<TenantStub>(_ => Task.FromResult<TenantStub>(null));
            c.WithCustomL2(new InProcessDistributedCache());
            configure?.Invoke(c);
        });
        return services.BuildServiceProvider();
    }

    [Test]
    public void LibraryRegistersNamedCache_NotTheDefault()
    {
        // No default AddFusionCache() was called, so resolving the default IFusionCache must fail...
        var sp = BuildProvider();
        sp.GetService<IFusionCache>().Should().BeNull();

        // ...but the named instance is present via the provider.
        var provider = sp.GetRequiredService<IFusionCacheProvider>();
        provider.GetCache(TenantContextCache.DefaultCacheName).Should().NotBeNull();
    }

    [Test]
    public void HostDefaultCache_CoexistsWithLibraryNamedCache()
    {
        var services = new ServiceCollection();

        // Host app registers its own default (unnamed) FusionCache.
        services.AddFusionCache();

        // Library registers its named instance.
        services.AddTenantContextCache(c =>
        {
            c.WithTenantDataFetch<TenantStub>(_ => Task.FromResult<TenantStub>(null));
            c.WithCustomL2(new InProcessDistributedCache());
        });

        var sp = services.BuildServiceProvider();

        var hostDefault = sp.GetRequiredService<IFusionCache>();
        var libraryNamed = sp.GetRequiredService<IFusionCacheProvider>()
            .GetCache(TenantContextCache.DefaultCacheName);

        hostDefault.Should().NotBeNull();
        libraryNamed.Should().NotBeNull();
        libraryNamed.Should().NotBeSameAs(hostDefault); // genuinely separate instances
        libraryNamed.CacheName.Should().Be(TenantContextCache.DefaultCacheName);
    }

    [Test]
    public void WithCacheName_OverridesTheInstanceName()
    {
        var sp = BuildProvider(c => c.WithCacheName("orders"));

        var provider = sp.GetRequiredService<IFusionCacheProvider>();
        provider.GetCache("orders").CacheName.Should().Be("orders");
    }

    [Test]
    public void WithCacheName_RejectsBlank()
    {
        var act = () => BuildProvider(c => c.WithCacheName("  "));
        act.Should().Throw<ArgumentException>();
    }
}
