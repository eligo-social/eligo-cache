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
    /// Fetches tenant information — cache-first, then the configured data source — and exposes
    /// the configured tenant-info type so the resolution middleware can inject the result into
    /// the request context under a type-derived key.
    /// </summary>
    public interface ITenantInfoProvider
    {
        /// <summary>The tenant-info type this provider produces, captured at registration.</summary>
        Type TenantInfoType { get; }

        /// <summary>
        /// Returns the tenant info for <paramref name="tenantId"/>, served from the multi-tier
        /// cache when present and otherwise fetched from the configured data source and cached.
        /// Returns <c>null</c> when the tenant id is empty or the source has no data.
        /// </summary>
        Task<object> GetTenantInfoAsync(string tenantId);
    }

    /// <summary>
    /// Default cache-backed tenant info provider. Reads through the multi-tier
    /// <see cref="ITenantContextCache"/> and falls back to the registered data fetch on a miss,
    /// re-caching what it fetches. Entries are stored under the tenant so they participate in
    /// per-tenant bulk invalidation.
    /// </summary>
    public class TenantInfoProvider<TTenantInfo> : ITenantInfoProvider
        where TTenantInfo : class
    {
        private readonly ITenantContextCache _cache;
        private readonly Func<string, Task<TTenantInfo>> _dataFetch;
        private readonly string _cacheKey;

        public TenantInfoProvider(
            ITenantContextCache cache,
            Func<string, Task<TTenantInfo>> dataFetch,
            string cacheKeyPrefix = "tenant-info")
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _dataFetch = dataFetch ?? throw new ArgumentNullException(nameof(dataFetch));
            _cacheKey = $"{cacheKeyPrefix}:{typeof(TTenantInfo).Name}";
        }

        public Type TenantInfoType => typeof(TTenantInfo);

        public async Task<object> GetTenantInfoAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return null;

            // Try the multi-tier cache first (L1 -> L2).
            var cached = await _cache.GetAsync<TTenantInfo>(tenantId, _cacheKey);
            if (cached != null)
                return cached;

            // Cache miss - fetch from the configured data source and cache the result.
            var data = await _dataFetch(tenantId);
            if (data != null)
                await _cache.SetAsync(tenantId, _cacheKey, data);

            return data;
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
    /// Middleware that resolves the tenant for each request, fetches its tenant info through the
    /// multi-tier cache, and injects both the tenant id and the tenant info into
    /// <see cref="HttpContext.Items"/> for the rest of the pipeline (read back via
    /// <see cref="ITenantContextAccessor"/>).
    /// </summary>
    public class TenantResolutionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenantResolver _tenantResolver;

        public TenantResolutionMiddleware(RequestDelegate next, ITenantResolver tenantResolver)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
        }

        // tenantInfoProvider is injected per-request from the request services (scoped), so the
        // fetch runs against the tenant resolved on this request.
        public async Task InvokeAsync(HttpContext context, ITenantInfoProvider tenantInfoProvider)
        {
            var tenantId = _tenantResolver.ResolveTenant(context);

            if (!string.IsNullOrEmpty(tenantId))
            {
                context.Items["TenantId"] = tenantId;

                var tenantInfo = await tenantInfoProvider.GetTenantInfoAsync(tenantId);
                if (tenantInfo != null)
                    context.Items[$"TenantInfo:{tenantInfoProvider.TenantInfoType.Name}"] = tenantInfo;
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
            builder.Build();
            return services;
        }

        /// <summary>
        /// Resolve the tenant for endpoints annotated with <see cref="TenantContextAttribute"/>,
        /// reading it from the route parameter that attribute names. This is the recommended,
        /// risk-free default: only opted-in endpoints participate, so an unrelated path such as
        /// <c>/admin/tenants/list</c> is never mistaken for a tenant route.
        /// <para>
        /// Register it <b>after</b> <c>UseRouting()</c> and before <c>UseEndpoints(...)</c> — the
        /// resolver reads endpoint metadata and route values, which routing populates.
        /// </para>
        /// </summary>
        public static IApplicationBuilder UseTenantContextCache(this IApplicationBuilder app)
        {
            return app.UseMiddleware<TenantResolutionMiddleware>(new EndpointTenantResolver());
        }

        /// <summary>
        /// Resolve the tenant from a single regex pattern with a named "tenant" capture group.
        /// <para>
        /// Note: this matches by URL shape and can false-match any path containing the pattern.
        /// Prefer the annotation-based <see cref="UseTenantContextCache(IApplicationBuilder)"/>
        /// unless you specifically need path-shape matching.
        /// </para>
        /// The tenant data source is configured once via
        /// <see cref="TenantContextCacheBuilder.WithTenantDataFetch{TTenantInfo}"/>.
        /// </summary>
        public static IApplicationBuilder UseTenantContextCache(
            this IApplicationBuilder app,
            string tenantPattern)
        {
            var tenantResolver = new RegexTenantResolver(tenantPattern);
            return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver);
        }

        /// <summary>
        /// Resolve the tenant from an ASP.NET-style route template instead of a raw regex.
        /// For example "/api/tenants/{tenantId:int}" matches numeric tenant ids only.
        /// The template is translated to a regex with a named "tenant" capture group.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="routeTemplate">Route template, e.g. "/api/tenants/{tenantId:int}".</param>
        /// <param name="tenantParameterName">
        /// Name of the template parameter that holds the tenant. Defaults to the first
        /// placeholder when omitted.
        /// </param>
        public static IApplicationBuilder UseTenantContextCacheWithTemplate(
            this IApplicationBuilder app,
            string routeTemplate,
            string tenantParameterName = null)
        {
            var pattern = RouteTemplateConverter.ToRegexPattern(routeTemplate, tenantParameterName);
            return app.UseTenantContextCache(pattern);
        }

        /// <summary>
        /// Resolve the tenant from multiple patterns via MultiPatternRouteResolver.
        /// Supports: /tenants/{tenantId}/**, /Tenants/{tenantSlug}/**, headers, subdomains, etc.
        /// </summary>
        public static IApplicationBuilder UseTenantContextCacheWithPatterns(
            this IApplicationBuilder app,
            Action<MultiPatternRouteResolver> configurePatterns)
        {
            var resolver = new MultiPatternRouteResolver();
            configurePatterns(resolver);
            return app.UseMiddleware<TenantResolutionMiddleware>(resolver);
        }

        /// <summary>
        /// Resolve the tenant with a custom <see cref="ITenantResolver"/>.
        /// </summary>
        public static IApplicationBuilder UseTenantContextCacheWithResolver(
            this IApplicationBuilder app,
            ITenantResolver tenantResolver)
        {
            return app.UseMiddleware<TenantResolutionMiddleware>(tenantResolver);
        }
    }

    /// <summary>
    /// Builder for configuring multi-tier cache
    /// </summary>
    public class TenantContextCacheBuilder
    {
        private readonly IServiceCollection _services;
        private readonly CacheConfiguration _config = new();
        private Func<IServiceProvider, IDistributedCache> _customL2Factory;
        private Action<IServiceCollection> _registerTenantInfoProvider;

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

        /// <summary>
        /// Configure the (required) tenant-data source. On each request the resolution
        /// middleware calls this — cache-first — and injects the returned
        /// <typeparamref name="TTenantInfo"/> into the request context, where it is read back
        /// via <see cref="ITenantContextAccessor.GetTenantInfo{T}"/>. Supplying tenant data is
        /// this library's primary function, so it must be configured.
        /// </summary>
        public TenantContextCacheBuilder WithTenantDataFetch<TTenantInfo>(Func<string, Task<TTenantInfo>> fetch)
            where TTenantInfo : class
        {
            if (fetch == null)
                throw new ArgumentNullException(nameof(fetch));

            return WithTenantDataFetch<TTenantInfo>((_, tenantId) => fetch(tenantId));
        }

        /// <summary>
        /// Configure the (required) tenant-data source, with access to the request-scoped
        /// <see cref="IServiceProvider"/>. Use this overload when the fetch depends on
        /// DI-registered services (a repository, <c>DbContext</c>, <c>HttpClient</c>, …): the
        /// provided <see cref="IServiceProvider"/> is the current request scope, so resolving
        /// scoped services from it is safe.
        /// </summary>
        public TenantContextCacheBuilder WithTenantDataFetch<TTenantInfo>(
            Func<IServiceProvider, string, Task<TTenantInfo>> fetch)
            where TTenantInfo : class
        {
            if (fetch == null)
                throw new ArgumentNullException(nameof(fetch));

            _registerTenantInfoProvider = services =>
                services.AddScoped<ITenantInfoProvider>(sp =>
                    new TenantInfoProvider<TTenantInfo>(
                        sp.GetRequiredService<ITenantContextCache>(),
                        tenantId => fetch(sp, tenantId)));
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

        /// <summary>
        /// Validates the configuration and performs DI registration. Called once by
        /// <see cref="TenantContextCacheExtensions.AddTenantContextCache"/> after the caller's
        /// configuration has run, so builder calls are order-independent.
        /// </summary>
        internal void Build()
        {
            if (_customL2Factory == null)
                throw new InvalidOperationException(
                    "No L2 (distributed) cache configured. Call WithCustomL2(...) with an IDistributedCache backend.");

            if (_registerTenantInfoProvider == null)
                throw new InvalidOperationException(
                    "No tenant-data fetch configured. Call WithTenantDataFetch<TTenantInfo>(...): " +
                    "fetching and injecting tenant data through the cache is this library's primary function.");

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
            fusion.WithDistributedCache(_customL2Factory);

            _services.AddSingleton<ITenantContextCache>(sp =>
                new TenantContextCache(sp.GetRequiredService<IFusionCache>()));

            // Register tenant context accessor
            _services.AddScoped<ITenantContextAccessor, HttpContextTenantAccessor>();

            // Register the (required) tenant info provider
            _registerTenantInfoProvider(_services);

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
