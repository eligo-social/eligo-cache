using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using StackExchange.Redis;

namespace MultiTierCache
{
    /// <summary>
    /// Represents cache configuration settings
    /// </summary>
    public class CacheConfiguration
    {
        public TimeSpan L1TimeToLive { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan L2TimeToLive { get; set; } = TimeSpan.FromHours(1);
        public L2Implementation L2Implementation { get; set; } = L2Implementation.Redis;
        public string RedisConnectionString { get; set; }
        public string HazelcastConnectionString { get; set; }
    }

    /// <summary>
    /// Supported L2 cache implementations
    /// </summary>
    public enum L2Implementation
    {
        Redis,
        Hazelcast,
        Custom
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
    public interface IMultiTierCache
    {
        Task<T> GetAsync<T>(string tenantId, string key);
        Task SetAsync<T>(string tenantId, string key, T value);
        Task RemoveAsync(string tenantId, string key);
        Task RemoveAllTenantAsync(string tenantId);
    }

    /// <summary>
    /// Multi-tiered cache implementation with L1 and L2
    /// </summary>
    public class MultiTierCache : IMultiTierCache
    {
        private readonly ICacheLayer _l1Cache;
        private readonly ICacheLayer _l2Cache;
        private readonly CacheConfiguration _config;
        private readonly ConcurrentDictionary<string, HashSet<string>> _tenantKeyTracking = new();

        public MultiTierCache(ICacheLayer l1Cache, ICacheLayer l2Cache, CacheConfiguration config)
        {
            _l1Cache = l1Cache;
            _l2Cache = l2Cache;
            _config = config;
        }

        private string BuildKey(string tenantId, string key) => $"tenant:{tenantId}:{key}";

        public async Task<T> GetAsync<T>(string tenantId, string key)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return default;

            var fullKey = BuildKey(tenantId, key);

            // Try L1 first
            var l1Value = await _l1Cache.GetAsync<T>(fullKey);
            if (l1Value != null)
                return l1Value;

            // Fall back to L2
            var l2Value = await _l2Cache.GetAsync<T>(fullKey);
            if (l2Value != null)
            {
                // Populate L1 from L2
                await _l1Cache.SetAsync(fullKey, l2Value, _config.L1TimeToLive);
                return l2Value;
            }

            return default;
        }

        public async Task SetAsync<T>(string tenantId, string key, T value)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return;

            var fullKey = BuildKey(tenantId, key);

            // Set in both layers
            await _l1Cache.SetAsync(fullKey, value, _config.L1TimeToLive);
            await _l2Cache.SetAsync(fullKey, value, _config.L2TimeToLive);

            // Track keys per tenant for bulk removal
            _tenantKeyTracking.AddOrUpdate(
                tenantId,
                new HashSet<string> { fullKey },
                (_, keys) => { keys.Add(fullKey); return keys; }
            );
        }

        public async Task RemoveAsync(string tenantId, string key)
        {
            var fullKey = BuildKey(tenantId, key);
            await _l1Cache.RemoveAsync(fullKey);
            await _l2Cache.RemoveAsync(fullKey);

            if (_tenantKeyTracking.TryGetValue(tenantId, out var keys))
            {
                keys.Remove(fullKey);
            }
        }

        public async Task RemoveAllTenantAsync(string tenantId)
        {
            if (_tenantKeyTracking.TryRemove(tenantId, out var keys))
            {
                var tasks = new List<Task>();
                foreach (var key in keys)
                {
                    tasks.Add(_l1Cache.RemoveAsync(key));
                    tasks.Add(_l2Cache.RemoveAsync(key));
                }
                await Task.WhenAll(tasks);
            }
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
        private readonly IMultiTierCache _cache;
        private readonly string _tenantId;

        public string TenantId => _tenantId;

        public TenantCache(IMultiTierCache cache, string tenantId)
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
    public static class MultiTierCacheExtensions
    {
        public static IServiceCollection AddMultiTierCache(
            this IServiceCollection services,
            Action<MultiTierCacheBuilder> configure)
        {
            var builder = new MultiTierCacheBuilder(services);
            configure(builder);
            return services;
        }

        /// <summary>
        /// Use middleware with single regex pattern (backward compatible)
        /// </summary>
        public static IApplicationBuilder UseMultiTierCache(
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
        public static IApplicationBuilder UseMultiTierCacheWithTemplate(
            this IApplicationBuilder app,
            string routeTemplate,
            Func<string, Task<object>> tenantDataFetch = null,
            string tenantParameterName = null)
        {
            var pattern = RouteTemplateConverter.ToRegexPattern(routeTemplate, tenantParameterName);
            return app.UseMultiTierCache(pattern, tenantDataFetch);
        }

        /// <summary>
        /// Use middleware with multiple patterns via MultiPatternRouteResolver
        /// Supports: /tenants/{tenantId}/**, /Tenants/{tenantSlug}/**, headers, subdomains, etc.
        /// </summary>
        public static IApplicationBuilder UseMultiTierCacheWithPatterns(
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
        public static IApplicationBuilder UseMultiTierCacheWithResolver(
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

        public static IApplicationBuilder UseMultiTierCacheWithResolvers(
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
    public class MultiTierCacheBuilder
    {
        private readonly IServiceCollection _services;
        private CacheConfiguration _config = new();
        private Func<string, Task<object>> _tenantDataFetch;
        private Func<IServiceProvider, ICacheLayer> _customL2Factory;

        public MultiTierCacheBuilder(IServiceCollection services)
        {
            _services = services;
        }

        public MultiTierCacheBuilder WithL1TimeToLive(TimeSpan ttl)
        {
            _config.L1TimeToLive = ttl;
            return this;
        }

        public MultiTierCacheBuilder WithL2TimeToLive(TimeSpan ttl)
        {
            _config.L2TimeToLive = ttl;
            return this;
        }

        public MultiTierCacheBuilder WithTenantDataFetch(Func<string, Task<object>> fetchFunc)
        {
            _tenantDataFetch = fetchFunc;
            return this;
        }

        public MultiTierCacheBuilder WithRedisL2(string connectionString)
        {
            _config.L2Implementation = L2Implementation.Redis;
            _config.RedisConnectionString = connectionString;
            RegisterCaches();
            return this;
        }

        public MultiTierCacheBuilder WithHazelcastL2(string connectionString)
        {
            _config.L2Implementation = L2Implementation.Hazelcast;
            _config.HazelcastConnectionString = connectionString;
            RegisterCaches();
            return this;
        }

        /// <summary>
        /// Use a custom L2 cache implementation.
        /// The instance must implement <see cref="ICacheLayer"/>.
        /// </summary>
        public MultiTierCacheBuilder WithCustomL2(ICacheLayer implementation)
        {
            if (implementation == null)
                throw new ArgumentNullException(nameof(implementation));

            return WithCustomL2(_ => implementation);
        }

        /// <summary>
        /// Use a custom L2 cache implementation resolved from a factory.
        /// The produced instance must implement <see cref="ICacheLayer"/>.
        /// The factory receives the application <see cref="IServiceProvider"/> so the
        /// implementation can pull its own dependencies from DI.
        /// </summary>
        public MultiTierCacheBuilder WithCustomL2(Func<IServiceProvider, ICacheLayer> factory)
        {
            _customL2Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _config.L2Implementation = L2Implementation.Custom;
            RegisterCaches();
            return this;
        }

        /// <summary>
        /// Use a custom L2 cache implementation resolved from DI by its type.
        /// <typeparamref name="TCacheLayer"/> must implement <see cref="ICacheLayer"/>.
        /// </summary>
        public MultiTierCacheBuilder WithCustomL2<TCacheLayer>()
            where TCacheLayer : class, ICacheLayer
        {
            _services.AddSingleton<TCacheLayer>();
            return WithCustomL2(sp => sp.GetRequiredService<TCacheLayer>());
        }

        private ICacheLayer CreateL2(IServiceProvider sp)
        {
            return _config.L2Implementation switch
            {
                L2Implementation.Redis => new RedisL2Cache(_config.RedisConnectionString),
                L2Implementation.Hazelcast => new HazelcastL2Cache(_config.HazelcastConnectionString),
                L2Implementation.Custom => (_customL2Factory
                    ?? throw new InvalidOperationException(
                        "L2Implementation is Custom but no custom L2 factory was configured."))(sp),
                _ => throw new InvalidOperationException(
                    $"Unsupported L2 implementation: {_config.L2Implementation}.")
            };
        }

        private void RegisterCaches()
        {
            _services.AddSingleton(_config);

            // Build L1 and L2 explicitly so each layer resolves to the correct
            // ICacheLayer instance (a bare ICacheLayer registration would inject
            // the same layer for both constructor parameters).
            _services.AddSingleton<IMultiTierCache>(sp =>
                new MultiTierCache(new InMemoryL1Cache(), CreateL2(sp), _config));

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
                var multiTierCache = sp.GetRequiredService<IMultiTierCache>();
                return new TenantCache(multiTierCache, tenantId);
            });
        }
    }
}
