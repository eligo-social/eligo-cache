using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace TenantContextCache.Tests;

/// <summary>
/// Helpers to construct FusionCache-backed caches in tests, mirroring the durations the
/// library configures (short L1 <c>Duration</c>, longer L2 <c>DistributedCacheDuration</c>).
/// </summary>
internal static class TestCacheFactory
{
    public static IFusionCache CreateFusionCache(IDistributedCache distributedCache = null)
    {
        var options = new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(5),
                DistributedCacheDuration = TimeSpan.FromHours(1),
                IsFailSafeEnabled = true
            }
        };

        var cache = new FusionCache(Options.Create(options));

        if (distributedCache != null)
            cache.SetupDistributedCache(distributedCache, new FusionCacheSystemTextJsonSerializer());

        return cache;
    }

    public static TenantContextCache CreateTenantContextCache(IDistributedCache distributedCache = null)
        => new TenantContextCache(CreateFusionCache(distributedCache));
}
