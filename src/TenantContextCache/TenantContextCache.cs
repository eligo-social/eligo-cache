using Microsoft.Extensions.Caching.Distributed;
using ZiggyCreatures.Caching.Fusion;

namespace TenantContextCache
{
    /// <summary>
    /// Represents cache configuration settings.
    /// <para>
    /// The two TTLs map onto FusionCache durations: <see cref="L1TimeToLive"/> becomes the
    /// in-memory (L1) <c>Duration</c> and <see cref="L2TimeToLive"/> becomes the distributed
    /// (L2) <c>DistributedCacheDuration</c>. A shorter L1 duration means the local copy is
    /// refreshed from L2 more often, exactly as with the previous hand-rolled two-tier cache.
    /// </para>
    /// </summary>
    public class CacheConfiguration
    {
        public TimeSpan L1TimeToLive { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan L2TimeToLive { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Represents a tenant resolution result
    /// </summary>
    public class TenantResolutionResult
    {
        public string TenantId { get; set; }
        public bool Success => !string.IsNullOrEmpty(TenantId);
        
        public static TenantResolutionResult Ok(string tenantId) => 
            new() { TenantId = tenantId };
        
        public static TenantResolutionResult Failed => 
            new() { TenantId = null };
    }

    /// <summary>
    /// Multi-tiered cache orchestrator
    /// </summary>
    public interface ITenantContextCache
    {
        Task<T> GetAsync<T>(string tenantId, string key);
        Task SetAsync<T>(string tenantId, string key, T value);
        Task RemoveAsync(string tenantId, string key);
        Task RemoveAllTenantAsync(string tenantId);
    }

    /// <summary>
    /// Multi-tiered, tenant-aware cache implementation backed by FusionCache.
    /// <para>
    /// FusionCache provides the hybrid L1 (in-memory) + L2 (distributed) behaviour natively:
    /// reads are served from L1 and transparently back-filled from L2, writes fan out to both
    /// layers. Every entry is tagged with its tenant so an entire tenant can be evicted in one
    /// call via <see cref="RemoveAllTenantAsync"/>.
    /// </para>
    /// </summary>
    public class TenantContextCache : ITenantContextCache
    {
        private readonly IFusionCache _cache;

        public TenantContextCache(IFusionCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        private static string BuildKey(string tenantId, string key) => $"tenant:{tenantId}:{key}";

        // Tag applied to every entry belonging to a tenant, enabling one-shot bulk eviction.
        private static string TenantTag(string tenantId) => $"tenant:{tenantId}";

        public async Task<T> GetAsync<T>(string tenantId, string key)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return default;

            return await _cache.GetOrDefaultAsync<T>(BuildKey(tenantId, key));
        }

        public async Task SetAsync<T>(string tenantId, string key, T value)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return;

            await _cache.SetAsync(BuildKey(tenantId, key), value, tags: new[] { TenantTag(tenantId) });
        }

        public async Task RemoveAsync(string tenantId, string key)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return;

            await _cache.RemoveAsync(BuildKey(tenantId, key));
        }

        public async Task RemoveAllTenantAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return;

            await _cache.RemoveByTagAsync(TenantTag(tenantId));
        }
    }

    /// <summary>
    /// Tenant-aware cache context
    /// </summary>
    public interface ITenantCache
    {
        Task<T> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value);
        Task RemoveAsync(string key);
        string TenantId { get; }
    }

    /// <summary>
    /// Tenant cache wrapper that handles tenant context automatically
    /// </summary>
    public class TenantCache : ITenantCache
    {
        private readonly ITenantContextCache _cache;
        private readonly string _tenantId;

        public string TenantId => _tenantId;

        public TenantCache(ITenantContextCache cache, string tenantId)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        }

        public Task<T> GetAsync<T>(string key) => _cache.GetAsync<T>(_tenantId, key);
        public Task SetAsync<T>(string key, T value) => _cache.SetAsync(_tenantId, key, value);
        public Task RemoveAsync(string key) => _cache.RemoveAsync(_tenantId, key);
    }

    /// <summary>
    /// Provides tenant information from cache or database
    /// </summary>
    public interface ITenantInfoProvider
    {
        Task<T> GetTenantInfoAsync<T>(string tenantId);
    }

    /// <summary>
    /// Default tenant info provider with cache-backed database fetch
    /// </summary>
    public class TenantInfoProvider : ITenantInfoProvider
    {
        private readonly ITenantCache _cache;
        private readonly Func<string, Task<object>> _databaseFetch;
        private readonly string _cacheKeyPrefix;

        public TenantInfoProvider(
            ITenantCache cache,
            Func<string, Task<object>> databaseFetch,
            string cacheKeyPrefix = "tenant-info")
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _databaseFetch = databaseFetch ?? throw new ArgumentNullException(nameof(databaseFetch));
            _cacheKeyPrefix = cacheKeyPrefix;
        }

        public async Task<T> GetTenantInfoAsync<T>(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return default;

            var cacheKey = $"{_cacheKeyPrefix}:{typeof(T).Name}";

            // Try cache first
            var cached = await _cache.GetAsync<T>(cacheKey);
            if (cached != null)
                return cached;

            // Cache miss - fetch from database
            var data = await _databaseFetch(tenantId);
            if (data != null)
            {
                var typedData = (T)Convert.ChangeType(data, typeof(T));
                await _cache.SetAsync(cacheKey, typedData);
                return typedData;
            }

            return default;
        }
    }

    /// <summary>
    /// Accessor to get current tenant context
    /// </summary>
    public interface ITenantContextAccessor
    {
        string GetTenantId();
        T GetTenantInfo<T>();
        void SetTenantInfo<T>(T info);
    }

    /// <summary>
    /// Default implementation using HttpContext
    /// </summary>
    public class HttpContextTenantAccessor : ITenantContextAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpContextTenantAccessor(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string GetTenantId()
        {
            return _httpContextAccessor.HttpContext?.Items["TenantId"] as string;
        }

        public T GetTenantInfo<T>()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null)
                return default;

            var key = $"TenantInfo:{typeof(T).Name}";
            return context.Items.TryGetValue(key, out var value) ? (T)value : default;
        }

        public void SetTenantInfo<T>(T info)
        {
            if (_httpContextAccessor.HttpContext == null)
                return;

            var key = $"TenantInfo:{typeof(T).Name}";
            _httpContextAccessor.HttpContext.Items[key] = info;
        }
    }

    /// <summary>
    /// Middleware to extract tenant, resolve tenant info, and inject into HttpContext
    /// </summary>
    public class TenantResolutionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenantResolver _tenantResolver;
        private readonly ITenantInfoProvider _tenantInfoProvider;
        private readonly List<Func<string, Task<(string key, object value)>>> _tenantDataResolvers;

        public TenantResolutionMiddleware(
            RequestDelegate next,
            ITenantResolver tenantResolver,
            ITenantInfoProvider tenantInfoProvider,
            List<Func<string, Task<(string key, object value)>>> tenantDataResolvers = null)
        {
            _next = next;
            _tenantResolver = tenantResolver;
            _tenantInfoProvider = tenantInfoProvider;
            _tenantDataResolvers = tenantDataResolvers ?? new List<Func<string, Task<(string key, object value)>>>();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var tenantId = _tenantResolver.ResolveTenant(context);
            
            if (!string.IsNullOrEmpty(tenantId))
            {
                context.Items["TenantId"] = tenantId;

                // Resolve additional tenant data if providers registered
                foreach (var resolver in _tenantDataResolvers)
                {
                    try
                    {
                        var (key, value) = await resolver(tenantId);
                        if (key != null && value != null)
                        {
                            context.Items[key] = value;
                        }
                    }
                    catch
                    {
                        // Log but don't fail the request
                    }
                }
            }

            await _next(context);
        }
    }

    /// <summary>
    /// Service collection extensions for DI registration
    /// </summary>
    public static class TenantContextCacheExtensions
    {
        public static IServiceCollection AddTenantContextCache(
            this IServiceCollection services,
            Action<TenantContextCacheBuilder> configure)
        {
            var builder = new TenantContextCacheBuilder(services);
            configure(builder);
            return services;
        }

        /// <summary>
        /// Use middleware with single regex pattern (backward compatible)
        /// </summary>
        public static IApplicationBuilder UseTenantContextCache(
            this IApplicationBuilder app,
            string tenantPattern,
            Func<string, Task<object>> tenantDataFetch = null)
        {
            var tenantResolver = new RegexTenantResolver(tenantPattern);
            
            if (tenantDataFetch != null)
            {
                var tenantCache = app.ApplicationServices.GetRequiredService<ITenantCache>();
                var tenantInfoProvider = new TenantInfoProvider(tenantCache, tenantDataFetch);
                return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver, tenantInfoProvider, new List<Func<string, Task<(string, object)>>>());
            }

            var emptyProvider = new NullTenantInfoProvider();
            return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver, emptyProvider, new List<Func<string, Task<(string, object)>>>());
        }

        /// <summary>
        /// Use middleware with an ASP.NET-style route template instead of a raw regex.
        /// For example "/api/tenants/{tenantId:int}" matches numeric tenant ids only.
        /// The template is translated to a regex with a named "tenant" capture group.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="routeTemplate">Route template, e.g. "/api/tenants/{tenantId:int}".</param>
        /// <param name="tenantDataFetch">Optional tenant data fetch callback.</param>
        /// <param name="tenantParameterName">
        /// Name of the template parameter that holds the tenant. Defaults to the first
        /// placeholder when omitted.
        /// </param>
        public static IApplicationBuilder UseTenantContextCacheWithTemplate(
            this IApplicationBuilder app,
            string routeTemplate,
            Func<string, Task<object>> tenantDataFetch = null,
            string tenantParameterName = null)
        {
            var pattern = RouteTemplateConverter.ToRegexPattern(routeTemplate, tenantParameterName);
            return app.UseTenantContextCache(pattern, tenantDataFetch);
        }

        /// <summary>
        /// Use middleware with multiple patterns via MultiPatternRouteResolver
        /// Supports: /tenants/{tenantId}/**, /Tenants/{tenantSlug}/**, headers, subdomains, etc.
        /// </summary>
        public static IApplicationBuilder UseTenantContextCacheWithPatterns(
            this IApplicationBuilder app,
            Action<MultiPatternRouteResolver> configurePatterns,
            Func<string, Task<object>> tenantDataFetch = null)
        {
            var resolver = new MultiPatternRouteResolver();
            configurePatterns(resolver);

            if (tenantDataFetch != null)
            {
                var tenantCache = app.ApplicationServices.GetRequiredService<ITenantCache>();
                var tenantInfoProvider = new TenantInfoProvider(tenantCache, tenantDataFetch);
                return app.UseMiddleware<TenantResolutionMiddleware>(resolver, tenantInfoProvider, new List<Func<string, Task<(string, object)>>>());
            }

            var emptyProvider = new NullTenantInfoProvider();
            return app.UseMiddleware<TenantResolutionMiddleware>(resolver, emptyProvider, new List<Func<string, Task<(string, object)>>>());
        }

        /// <summary>
        /// Use middleware with custom resolver
        /// </summary>
        public static IApplicationBuilder UseTenantContextCacheWithResolver(
            this IApplicationBuilder app,
            ITenantResolver tenantResolver,
            Func<string, Task<object>> tenantDataFetch = null)
        {
            if (tenantDataFetch != null)
            {
                var tenantCache = app.ApplicationServices.GetRequiredService<ITenantCache>();
                var tenantInfoProvider = new TenantInfoProvider(tenantCache, tenantDataFetch);
                return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver, tenantInfoProvider, new List<Func<string, Task<(string, object)>>>());
            }

            var emptyProvider = new NullTenantInfoProvider();
            return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver, emptyProvider, new List<Func<string, Task<(string, object)>>>());
        }

        public static IApplicationBuilder UseTenantContextCacheWithResolvers(
            this IApplicationBuilder app,
            string tenantPattern,
            Func<string, Task<object>> tenantDataFetch,
            params Func<string, Task<(string key, object value)>>[] additionalResolvers)
        {
            var tenantResolver = new RegexTenantResolver(tenantPattern);
            var tenantCache = app.ApplicationServices.GetRequiredService<ITenantCache>();
            var tenantInfoProvider = new TenantInfoProvider(tenantCache, tenantDataFetch);
            var resolversList = new List<Func<string, Task<(string, object)>>>(additionalResolvers);
            return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver, tenantInfoProvider, resolversList);
        }
    }

    /// <summary>
    /// Null implementation for when no tenant data provider is configured
    /// </summary>
    public class NullTenantInfoProvider : ITenantInfoProvider
    {
        public Task<T> GetTenantInfoAsync<T>(string tenantId)
        {
            return Task.FromResult<T>(default);
        }
    }

    /// <summary>
    /// Builder for configuring multi-tier cache
    /// </summary>
    public class TenantContextCacheBuilder
    {
        private readonly IServiceCollection _services;
        private CacheConfiguration _config = new();
        private Func<string, Task<object>> _tenantDataFetch;
        private Func<IServiceProvider, IDistributedCache> _customL2Factory;

        public TenantContextCacheBuilder(IServiceCollection services)
        {
            _services = services;
        }

        public TenantContextCacheBuilder WithL1TimeToLive(TimeSpan ttl)
        {
            _config.L1TimeToLive = ttl;
            return this;
        }

        public TenantContextCacheBuilder WithL2TimeToLive(TimeSpan ttl)
        {
            _config.L2TimeToLive = ttl;
            return this;
        }

        public TenantContextCacheBuilder WithTenantDataFetch(Func<string, Task<object>> fetchFunc)
        {
            _tenantDataFetch = fetchFunc;
            return this;
        }

        /// <summary>
        /// Use a custom L2 (distributed) cache implementation. The instance must implement
        /// <see cref="IDistributedCache"/> — FusionCache's abstraction for the distributed layer.
        /// </summary>
        public TenantContextCacheBuilder WithCustomL2(IDistributedCache implementation)
        {
            if (implementation == null)
                throw new ArgumentNullException(nameof(implementation));

            return WithCustomL2(_ => implementation);
        }

        /// <summary>
        /// Use a custom L2 (distributed) cache implementation resolved from a factory.
        /// The produced instance must implement <see cref="IDistributedCache"/>.
        /// The factory receives the application <see cref="IServiceProvider"/> so the
        /// implementation can pull its own dependencies from DI.
        /// </summary>
        public TenantContextCacheBuilder WithCustomL2(Func<IServiceProvider, IDistributedCache> factory)
        {
            _customL2Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            RegisterCaches();
            return this;
        }

        /// <summary>
        /// Use a custom L2 (distributed) cache implementation resolved from DI by its type.
        /// <typeparamref name="TDistributedCache"/> must implement <see cref="IDistributedCache"/>.
        /// </summary>
        public TenantContextCacheBuilder WithCustomL2<TDistributedCache>()
            where TDistributedCache : class, IDistributedCache
        {
            _services.AddSingleton<TDistributedCache>();
            return WithCustomL2(sp => sp.GetRequiredService<TDistributedCache>());
        }

        private void RegisterCaches()
        {
            _services.AddSingleton(_config);

            // FusionCache provides the hybrid L1 (in-memory) + L2 (distributed) engine.
            // L1 uses the shorter Duration; L2 keeps entries for the longer
            // DistributedCacheDuration, matching the previous two-tier TTL semantics.
            var fusion = _services.AddFusionCache()
                .WithDefaultEntryOptions(options =>
                {
                    options.Duration = _config.L1TimeToLive;
                    options.DistributedCacheDuration = _config.L2TimeToLive;
                    // Serve stale data if a refresh/factory fails, instead of throwing.
                    options.IsFailSafeEnabled = true;
                })
                .WithSystemTextJsonSerializer();

            // The L2 (distributed) layer is provided by the caller as an IDistributedCache.
            // Any backend with an IDistributedCache adapter (Redis, SQL Server, etc.) can be
            // plugged in via WithCustomL2(...).
            var factory = _customL2Factory
                ?? throw new InvalidOperationException(
                    "No custom L2 (distributed) cache was configured. Call WithCustomL2(...).");
            fusion.WithDistributedCache(factory);

            _services.AddSingleton<ITenantContextCache>(sp =>
                new TenantContextCache(sp.GetRequiredService<IFusionCache>()));

            // Register tenant context accessor
            _services.AddScoped<ITenantContextAccessor, HttpContextTenantAccessor>();

            // Register tenant info provider if data fetch function is provided
            if (_tenantDataFetch != null)
            {
                _services.AddScoped<ITenantInfoProvider>(sp =>
                {
                    var tenantCache = sp.GetRequiredService<ITenantCache>();
                    return new TenantInfoProvider(tenantCache, _tenantDataFetch);
                });
            }
            else
            {
                _services.AddScoped<ITenantInfoProvider, NullTenantInfoProvider>();
            }

            // Register factory for tenant-specific cache
            _services.AddScoped<ITenantCache>(sp =>
            {
                var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
                var context = httpContextAccessor.HttpContext;
                var tenantId = context?.Items["TenantId"] as string ?? "default";
                var multiTierCache = sp.GetRequiredService<ITenantContextCache>();
                return new TenantCache(multiTierCache, tenantId);
            });
        }
    }
}
