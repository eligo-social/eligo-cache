using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace TenantContextCache.Tests;

/// <summary>
/// Covers "bring your own cache": WithExistingCache(...) hands the library a ready-made
/// <see cref="ITenantContextCache"/> instead of the built-in FusionCache engine. The guarantees
/// are that no engine is built, that every tenant read/write is routed through the caller's
/// implementation, and that engine-only options are rejected rather than silently ignored.
/// </summary>
[TestFixture]
public class BringYourOwnCacheTests
{
    private sealed class TenantInfo
    {
        public string Name { get; set; }
    }

    /// <summary>Minimal single-tier <see cref="ITenantContextCache"/> that records what it is asked to do.</summary>
    private class RecordingCache : ITenantContextCache
    {
        private readonly ConcurrentDictionary<string, object> _entries = new();

        public List<string> Calls { get; } = new();

        private static string Key(string tenantId, string key) => $"{tenantId}/{key}";

        public Task<T> GetAsync<T>(string tenantId, string key)
        {
            Calls.Add($"get:{Key(tenantId, key)}");
            return Task.FromResult(_entries.TryGetValue(Key(tenantId, key), out var value) ? (T)value : default);
        }

        public Task SetAsync<T>(string tenantId, string key, T value)
        {
            Calls.Add($"set:{Key(tenantId, key)}");
            _entries[Key(tenantId, key)] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string tenantId, string key)
        {
            Calls.Add($"remove:{Key(tenantId, key)}");
            _entries.TryRemove(Key(tenantId, key), out _);
            return Task.CompletedTask;
        }

        public Task RemoveAllTenantAsync(string tenantId)
        {
            Calls.Add($"remove-all:{tenantId}");
            foreach (var entry in _entries.Keys.Where(k => k.StartsWith($"{tenantId}/", StringComparison.Ordinal)))
                _entries.TryRemove(entry, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>Parameterless custom cache for the generic WithExistingCache&lt;T&gt; overload.</summary>
    private sealed class ParameterlessCache : RecordingCache { }

    private static ServiceProvider BuildProvider(Action<TenantContextCacheBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddTenantContextCache(c =>
        {
            c.WithTenantDataFetch<TenantInfo>(tenantId =>
                Task.FromResult(new TenantInfo { Name = $"tenant-{tenantId}" }));
            configure(c);
        });
        return services.BuildServiceProvider();
    }

    [Test]
    public void WithExistingCache_Instance_IsResolvedAsTheTenantContextCache()
    {
        var custom = new RecordingCache();

        var resolved = BuildProvider(c => c.WithExistingCache(custom))
            .GetRequiredService<ITenantContextCache>();

        resolved.Should().BeSameAs(custom);
    }

    [Test]
    public void WithExistingCache_RegistersNoFusionCache()
    {
        var sp = BuildProvider(c => c.WithExistingCache(new RecordingCache()));

        // The built-in engine is never registered, so neither the default cache nor the
        // provider that hands out named ones exists.
        sp.GetService<IFusionCache>().Should().BeNull();
        sp.GetService<IFusionCacheProvider>().Should().BeNull();
    }

    [Test]
    public async Task WithExistingCache_TenantInfoProvider_ReadsAndWritesThroughTheCustomCache()
    {
        var custom = new RecordingCache();
        var sp = BuildProvider(c => c.WithExistingCache(custom));

        var provider = sp.GetRequiredService<ITenantInfoProvider>();
        var first = (TenantInfo)await provider.GetTenantInfoAsync("acme");
        var second = (TenantInfo)await provider.GetTenantInfoAsync("acme");

        first.Name.Should().Be("tenant-acme");
        second.Name.Should().Be("tenant-acme");

        // Miss -> fetch -> write, then a hit served by the custom cache (no second write).
        custom.Calls.Should().Equal(
            "get:acme/tenant-info:TenantInfo",
            "set:acme/tenant-info:TenantInfo",
            "get:acme/tenant-info:TenantInfo");
    }

    [Test]
    public async Task WithExistingCache_PerRequestTenantCache_RoutesToTheCustomCache()
    {
        var custom = new RecordingCache();
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>(_ => Task.FromResult<TenantInfo>(null))
            .WithExistingCache(custom));

        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var tenantCache = scope.ServiceProvider.GetRequiredService<ITenantCache>();

        await tenantCache.SetAsync("user-1", "value");
        var value = await tenantCache.GetAsync<string>("user-1");

        value.Should().Be("value");
        // No HttpContext in this scope, so the wrapper falls back to the "default" tenant.
        custom.Calls.Should().Contain("set:default/user-1");
    }

    [Test]
    public async Task WithExistingCache_Factory_ReceivesServiceProviderAndIsUsed()
    {
        var custom = new RecordingCache();
        var factoryCalls = 0;

        var sp = BuildProvider(c => c.WithExistingCache(provider =>
        {
            factoryCalls++;
            provider.Should().NotBeNull();
            return custom;
        }));

        var resolved = sp.GetRequiredService<ITenantContextCache>();
        await resolved.SetAsync("acme", "user-1", "value");

        // Registered as a singleton: the factory runs once no matter how often it is resolved.
        sp.GetRequiredService<ITenantContextCache>().Should().BeSameAs(custom);
        factoryCalls.Should().Be(1);
        custom.Calls.Should().Contain("set:acme/user-1");
    }

    [Test]
    public async Task WithExistingCache_Generic_ResolvesTypeFromDiAndRoundTrips()
    {
        var sp = BuildProvider(c => c.WithExistingCache<ParameterlessCache>());

        var cache = sp.GetRequiredService<ITenantContextCache>();
        await cache.SetAsync("acme", "user-1", "value");

        cache.Should().BeOfType<ParameterlessCache>();
        (await cache.GetAsync<string>("acme", "user-1")).Should().Be("value");
    }

    [Test]
    public void WithExistingCache_Generic_KeepsAnExistingRegistration()
    {
        var custom = new ParameterlessCache();
        var services = new ServiceCollection();
        services.AddSingleton(custom); // the host app registered it first, with its own wiring
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>(_ => Task.FromResult<TenantInfo>(null))
            .WithExistingCache<ParameterlessCache>());

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ITenantContextCache>().Should().BeSameAs(custom);
    }

    [Test]
    public void WithExistingCache_Throws_WhenCombinedWithCustomL2()
    {
        Action act = () => BuildProvider(c => c
            .WithExistingCache(new RecordingCache())
            .WithCustomL2(new InProcessDistributedCache()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithCustomL2*");
    }

    [TestCase("WithL1TimeToLive")]
    [TestCase("WithL2TimeToLive")]
    [TestCase("WithCacheKeyPrefix")]
    [TestCase("WithCacheName")]
    public void WithExistingCache_Throws_WhenCombinedWithEngineOnlyOptions(string setter)
    {
        Action act = () => BuildProvider(c =>
        {
            c.WithExistingCache(new RecordingCache());
            switch (setter)
            {
                case "WithL1TimeToLive": c.WithL1TimeToLive(TimeSpan.FromMinutes(1)); break;
                case "WithL2TimeToLive": c.WithL2TimeToLive(TimeSpan.FromMinutes(1)); break;
                case "WithCacheKeyPrefix": c.WithCacheKeyPrefix("myapp"); break;
                case "WithCacheName": c.WithCacheName("orders"); break;
            }
        });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{setter}*");
    }

    [Test]
    public void WithExistingCache_Throws_WhenEngineOnlyOptionCameFirst()
    {
        // Builder calls are order-independent: the conflict is detected at Build() either way.
        Action act = () => BuildProvider(c => c
            .WithCacheKeyPrefix("myapp")
            .WithExistingCache(new RecordingCache()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithCacheKeyPrefix*");
    }

    [Test]
    public void WithExistingCache_StillRequiresATenantDataFetch()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantContextCache(c =>
            c.WithExistingCache(new RecordingCache()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithTenantDataFetch*");
    }

    [Test]
    public void WithExistingCache_RejectsNull()
    {
        var nullInstance = () => BuildProvider(c => c.WithExistingCache((ITenantContextCache)null));
        var nullFactory = () => BuildProvider(c =>
            c.WithExistingCache((Func<IServiceProvider, ITenantContextCache>)null));

        nullInstance.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void WithoutExistingCache_StillRequiresAnL2_AndMentionsBothOptions()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantContextCache(c =>
            c.WithTenantDataFetch<TenantInfo>(_ => Task.FromResult<TenantInfo>(null)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithCustomL2*")
            .WithMessage("*WithExistingCache*");
    }
}
