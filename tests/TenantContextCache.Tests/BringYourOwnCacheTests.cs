using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ZiggyCreatures.Caching.Fusion;

namespace TenantContextCache.Tests;

/// <summary>
/// Covers "bring your own cache": WithExistingCache(...) hands the library a ready-made
/// <see cref="ICacheStore"/> instead of the built-in FusionCache engine. The guarantees are that
/// no engine is built, that every read/write is routed through the caller's store, that the store
/// sees flat library-composed keys (never a tenant id), and that engine-only options are rejected
/// rather than silently ignored — while the key options stay valid.
/// </summary>
[TestFixture]
public class BringYourOwnCacheTests
{
    private sealed class TenantInfo
    {
        public string Name { get; set; }
    }

    /// <summary>Minimal single-tier <see cref="ICacheStore"/> that records what it is asked to do.</summary>
    private class RecordingStore : ICacheStore
    {
        private readonly ConcurrentDictionary<string, object> _entries = new();

        public List<string> Calls { get; } = new();

        public Task<T> GetAsync<T>(string key)
        {
            Calls.Add($"get:{key}");
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? (T)value : default);
        }

        public Task SetAsync<T>(string key, T value, IReadOnlyCollection<string> tags = null)
        {
            Calls.Add($"set:{key}[{string.Join(",", tags ?? Array.Empty<string>())}]");
            _entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            Calls.Add($"remove:{key}");
            _entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        // No tag index here: the default key builder guarantees the tag is a literal key prefix,
        // so a prefix scan is a valid implementation.
        public Task RemoveByTagAsync(string tag)
        {
            Calls.Add($"remove-by-tag:{tag}");
            foreach (var entry in _entries.Keys.Where(k => k.StartsWith(tag, StringComparison.Ordinal)))
                _entries.TryRemove(entry, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>Parameterless custom store for the generic WithExistingCache&lt;T&gt; overload.</summary>
    private sealed class ParameterlessStore : RecordingStore { }

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
    public void WithExistingCache_Instance_IsResolvedAsTheCacheStore()
    {
        var custom = new RecordingStore();

        var resolved = BuildProvider(c => c.WithExistingCache(custom))
            .GetRequiredService<ICacheStore>();

        resolved.Should().BeSameAs(custom);
    }

    [Test]
    public void WithExistingCache_StillProvidesTheTenantAwareLayer()
    {
        // ITenantContextCache is the library's own wrapper on both paths; only the store swaps.
        var sp = BuildProvider(c => c.WithExistingCache(new RecordingStore()));

        sp.GetRequiredService<ITenantContextCache>().Should().BeOfType<TenantContextCache>();
    }

    [Test]
    public void WithExistingCache_RegistersNoFusionCache()
    {
        var sp = BuildProvider(c => c.WithExistingCache(new RecordingStore()));

        // The built-in engine is never registered, so neither the default cache nor the
        // provider that hands out named ones exists.
        sp.GetService<IFusionCache>().Should().BeNull();
        sp.GetService<IFusionCacheProvider>().Should().BeNull();
    }

    [Test]
    public async Task WithExistingCache_TenantInfoProvider_ReadsAndWritesThroughTheCustomStore()
    {
        var custom = new RecordingStore();
        var sp = BuildProvider(c => c.WithExistingCache(custom));

        var provider = sp.GetRequiredService<ITenantInfoProvider>();
        var first = (TenantInfo)await provider.GetTenantInfoAsync("acme");
        var second = (TenantInfo)await provider.GetTenantInfoAsync("acme");

        first.Name.Should().Be("tenant-acme");
        second.Name.Should().Be("tenant-acme");

        // Miss -> fetch -> write, then a hit served by the custom store (no second write). The
        // store sees a flat, fully composed key and the tenant tag — never a tenant id.
        custom.Calls.Should().Equal(
            "get:tenant:acme:tenant-info:TenantInfo",
            "set:tenant:acme:tenant-info:TenantInfo[tenant:acme]",
            "get:tenant:acme:tenant-info:TenantInfo");
    }

    [Test]
    public async Task WithExistingCache_PerRequestTenantCache_RoutesToTheCustomStore()
    {
        var custom = new RecordingStore();
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
        custom.Calls.Should().Contain("set:tenant:default:user-1[tenant:default]");
    }

    [Test]
    public async Task WithExistingCache_RemoveAllTenant_EvictsByTenantTag()
    {
        var custom = new RecordingStore();
        var cache = BuildProvider(c => c.WithExistingCache(custom))
            .GetRequiredService<ITenantContextCache>();

        await cache.SetAsync("acme", "user-1", "value");
        await cache.SetAsync("other", "user-1", "untouched");

        await cache.RemoveAllTenantAsync("acme");

        custom.Calls.Should().Contain("remove-by-tag:tenant:acme");
        (await cache.GetAsync<string>("acme", "user-1")).Should().BeNull();
        (await cache.GetAsync<string>("other", "user-1")).Should().Be("untouched");
    }

    [Test]
    public async Task WithExistingCache_Factory_ReceivesServiceProviderAndIsUsed()
    {
        var custom = new RecordingStore();
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
        sp.GetRequiredService<ICacheStore>().Should().BeSameAs(custom);
        factoryCalls.Should().Be(1);
        custom.Calls.Should().Contain("set:tenant:acme:user-1[tenant:acme]");
    }

    [Test]
    public async Task WithExistingCache_Generic_ResolvesTypeFromDiAndRoundTrips()
    {
        var sp = BuildProvider(c => c.WithExistingCache<ParameterlessStore>());

        var cache = sp.GetRequiredService<ITenantContextCache>();
        await cache.SetAsync("acme", "user-1", "value");

        sp.GetRequiredService<ICacheStore>().Should().BeOfType<ParameterlessStore>();
        (await cache.GetAsync<string>("acme", "user-1")).Should().Be("value");
    }

    [Test]
    public void WithExistingCache_Generic_KeepsAnExistingRegistration()
    {
        var custom = new ParameterlessStore();
        var services = new ServiceCollection();
        services.AddSingleton(custom); // the host app registered it first, with its own wiring
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<TenantInfo>(_ => Task.FromResult<TenantInfo>(null))
            .WithExistingCache<ParameterlessStore>());

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ICacheStore>().Should().BeSameAs(custom);
    }

    [Test]
    public void WithExistingCache_Throws_WhenCombinedWithCustomL2()
    {
        Action act = () => BuildProvider(c => c
            .WithExistingCache(new RecordingStore())
            .WithCustomL2(new InProcessDistributedCache()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithCustomL2*");
    }

    [TestCase("WithL1TimeToLive")]
    [TestCase("WithL2TimeToLive")]
    [TestCase("WithCacheName")]
    public void WithExistingCache_Throws_WhenCombinedWithEngineOnlyOptions(string setter)
    {
        Action act = () => BuildProvider(c =>
        {
            c.WithExistingCache(new RecordingStore());
            switch (setter)
            {
                case "WithL1TimeToLive": c.WithL1TimeToLive(TimeSpan.FromMinutes(1)); break;
                case "WithL2TimeToLive": c.WithL2TimeToLive(TimeSpan.FromMinutes(1)); break;
                case "WithCacheName": c.WithCacheName("orders"); break;
            }
        });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{setter}*");
    }

    [Test]
    public async Task WithExistingCache_HonoursWithCacheKeyPrefix()
    {
        // Key layout is the library's job on both paths, so this combination is valid — unlike
        // the engine-only options above — and reaches the custom store.
        var custom = new RecordingStore();
        var sp = BuildProvider(c => c
            .WithExistingCache(custom)
            .WithCacheKeyPrefix("TENANT-CONTENT", "-"));

        await sp.GetRequiredService<ITenantContextCache>().SetAsync("acme", "user-1", "value");

        custom.Calls.Should().Contain("set:TENANT-CONTENT-acme-user-1[TENANT-CONTENT-acme]");
    }

    [Test]
    public async Task WithExistingCache_HonoursACustomKeyBuilder()
    {
        var custom = new RecordingStore();
        var sp = BuildProvider(c => c
            .WithExistingCache(custom)
            .WithCacheKeyBuilder(new UppercaseKeyBuilder()));

        await sp.GetRequiredService<ITenantContextCache>().SetAsync("acme", "user-1", "value");

        custom.Calls.Should().Contain("set:ACME/user-1[ACME]");
    }

    private sealed class UppercaseKeyBuilder : ICacheKeyBuilder
    {
        public string BuildKey(string tenantId, string key) => $"{TenantTag(tenantId)}/{key}";
        public string TenantTag(string tenantId) => tenantId.ToUpperInvariant();
    }

    [Test]
    public void WithExistingCache_Throws_WhenEngineOnlyOptionCameFirst()
    {
        // Builder calls are order-independent: the conflict is detected at Build() either way.
        Action act = () => BuildProvider(c => c
            .WithCacheName("orders")
            .WithExistingCache(new RecordingStore()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithCacheName*");
    }

    [Test]
    public void WithExistingCache_StillRequiresATenantDataFetch()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddTenantContextCache(c =>
            c.WithExistingCache(new RecordingStore()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithTenantDataFetch*");
    }

    [Test]
    public void WithExistingCache_RejectsNull()
    {
        var nullInstance = () => BuildProvider(c => c.WithExistingCache((ICacheStore)null));
        var nullFactory = () => BuildProvider(c =>
            c.WithExistingCache((Func<IServiceProvider, ICacheStore>)null));

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
