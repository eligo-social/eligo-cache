using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace TenantContextCache.Tests;

/// <summary>
/// Covers the two core guarantees of the tenant-data feature: registration requires a fetch
/// (and an L2), and the provider serves cache-first, fetching + caching only on a miss.
/// </summary>
[TestFixture]
public class TenantInfoProviderTests
{
    private sealed class TenantInfo
    {
        public string Name { get; set; }
    }

    [Test]
    public void AddTenantContextCache_Throws_WhenNoTenantDataFetchConfigured()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantContextCache(c =>
            c.WithCustomL2(new InProcessDistributedCache()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithTenantDataFetch*");
    }

    [Test]
    public void AddTenantContextCache_Throws_WhenNoL2Configured()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantContextCache(c =>
            c.WithTenantDataFetch<TenantInfo>(_ => Task.FromResult<TenantInfo>(null)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithCustomL2*");
    }

    [Test]
    public async Task Provider_FetchesOnMiss_ThenServesFromCache()
    {
        var fetchCount = 0;
        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>(tenantId =>
            {
                fetchCount++;
                return Task.FromResult(new TenantInfo { Name = $"tenant-{tenantId}" });
            })
            .WithCustomL2(new InProcessDistributedCache()));

        var provider = services.BuildServiceProvider().GetRequiredService<ITenantInfoProvider>();

        var first = (TenantInfo)await provider.GetTenantInfoAsync("acme");
        var second = (TenantInfo)await provider.GetTenantInfoAsync("acme");

        first.Name.Should().Be("tenant-acme");
        second.Name.Should().Be("tenant-acme");
        fetchCount.Should().Be(1); // second call served from cache, not re-fetched
        provider.TenantInfoType.Should().Be<TenantInfo>();
    }

    [Test]
    public async Task Provider_FetchOverload_ResolvesServiceFromRequestScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantSource, TenantSource>();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>((sp, tenantId) =>
                sp.GetRequiredService<ITenantSource>().LoadAsync(tenantId))
            .WithCustomL2(new InProcessDistributedCache()));

        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ITenantInfoProvider>();

        var info = (TenantInfo)await provider.GetTenantInfoAsync("acme");

        info.Name.Should().Be("from-di-acme");
    }

    private interface ITenantSource
    {
        Task<TenantInfo> LoadAsync(string tenantId);
    }

    private sealed class TenantSource : ITenantSource
    {
        public Task<TenantInfo> LoadAsync(string tenantId) =>
            Task.FromResult(new TenantInfo { Name = $"from-di-{tenantId}" });
    }

    [Test]
    public async Task Provider_ReturnsNull_ForEmptyTenantId_WithoutFetching()
    {
        var fetchCount = 0;
        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>(_ =>
            {
                fetchCount++;
                return Task.FromResult(new TenantInfo());
            })
            .WithCustomL2(new InProcessDistributedCache()));

        var provider = services.BuildServiceProvider().GetRequiredService<ITenantInfoProvider>();

        (await provider.GetTenantInfoAsync("")).Should().BeNull();
        fetchCount.Should().Be(0);
    }
}
