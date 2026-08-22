using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <para>
    /// The TTLs and <see cref="CacheName"/> describe the <b>built-in</b> FusionCache engine, so
    /// they are not honoured when you bring your own cache via
    /// <see cref="TenantContextCacheBuilder.WithExistingCache(ICacheStore)"/>. The key settings
    /// are different: key layout is decided by the library on both paths, so
    /// <see cref="CacheKeyPrefix"/>, <see cref="CacheKeySeparator"/> and
    /// <see cref="TenantInfoKeyPrefix"/> apply to your own store exactly as they do to the
    /// built-in one.
    /// </para>
    /// </summary>
    public class CacheConfiguration
    {
        public TimeSpan L1TimeToLive { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan L2TimeToLive { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Leading segment of every cache key and per-tenant tag, e.g. the "tenant" in
        /// <c>tenant:acme:tenant-info:TenantInfo</c>. Configurable via
        /// <see cref="TenantContextCacheBuilder.WithCacheKeyPrefix"/>; defaults to "tenant".
        /// </summary>
        public string CacheKeyPrefix { get; set; } = TenantCacheKeyBuilder.DefaultPrefix;

        /// <summary>
        /// String placed between the key segments, e.g. the ":" in <c>tenant:acme:user-1</c>.
        /// Configurable via <see cref="TenantContextCacheBuilder.WithCacheKeyPrefix"/>; defaults
        /// to ":".
        /// </summary>
        public string CacheKeySeparator { get; set; } = TenantCacheKeyBuilder.DefaultSeparator;

        /// <summary>
        /// Leading segment of the key the library stores tenant info under, which becomes
        /// <c>{TenantInfoKeyPrefix}:{TenantInfoTypeName}</c> before the tenant prefix is applied.
        /// Configurable via <see cref="TenantContextCacheBuilder.WithTenantInfoKeyPrefix"/>;
        /// defaults to "tenant-info".
        /// </summary>
        public string TenantInfoKeyPrefix { get; set; } = TenantInfoProviderDefaults.KeyPrefix;

        /// <summary>
        /// Name of the FusionCache instance the library registers for its own use. Using a named
        /// instance (rather than the default) leaves the unnamed <c>IFusionCache</c> free for the
        /// rest of your app. Configurable via <see cref="TenantContextCacheBuilder.WithCacheName"/>;
        /// defaults to <see cref="TenantContextCache.DefaultCacheName"/>. Retrieve it with
        /// <c>IFusionCacheProvider.GetCache(name)</c>.
        /// </summary>
        public string CacheName { get; set; } = TenantContextCache.DefaultCacheName;
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
    /// Tenant-aware cache orchestrator: the library's public caching surface, and the layer that
    /// turns a (tenant, key) pair into the flat keys and tags an <see cref="ICacheStore"/> sees.
    /// </summary>
    public interface ITenantContextCache
    {
        Task<T> GetAsync<T>(string tenantId, string key);
        Task SetAsync<T>(string tenantId, string key, T value);
        Task RemoveAsync(string tenantId, string key);
        Task RemoveAllTenantAsync(string tenantId);
    }

    /// <summary>
    /// Default tenant-aware cache: composes keys with an <see cref="ICacheKeyBuilder"/> and stores
    /// them through an <see cref="ICacheStore"/>.
    /// <para>
    /// This is the only tenant-aware layer, and it sits above the store on both paths — the
    /// built-in <see cref="FusionCacheStore"/> and any store you bring yourself — so key layout
    /// and per-tenant tagging behave identically whichever engine is underneath. Every entry
    /// carries its tenant's tag, so a whole tenant can be evicted in one call via
    /// <see cref="RemoveAllTenantAsync"/>.
    /// </para>
    /// </summary>
    public class TenantContextCache : ITenantContextCache
    {
        /// <summary>Prefix used when none is configured, preserving the original key layout.</summary>
        public const string DefaultCacheKeyPrefix = TenantCacheKeyBuilder.DefaultPrefix;

        /// <summary>Name of the FusionCache instance the library registers when none is configured.</summary>
        public const string DefaultCacheName = "tenant-context";

        private readonly ICacheStore _store;
        private readonly ICacheKeyBuilder _keys;

        public TenantContextCache(ICacheStore store, ICacheKeyBuilder keyBuilder)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _keys = keyBuilder ?? throw new ArgumentNullException(nameof(keyBuilder));
        }

        /// <summary>
        /// Convenience overload for the built-in engine: wraps <paramref name="cache"/> in a
        /// <see cref="FusionCacheStore"/> and uses the default key layout.
        /// </summary>
        public TenantContextCache(IFusionCache cache, string cacheKeyPrefix = DefaultCacheKeyPrefix)
            : this(new FusionCacheStore(cache), new TenantCacheKeyBuilder(cacheKeyPrefix))
        {
        }

        public async Task<T> GetAsync<T>(string tenantId, string key)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return default;

            return await _store.GetAsync<T>(_keys.BuildKey(tenantId, key));
        }

        public async Task SetAsync<T>(string tenantId, string key, T value)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return;

            await _store.SetAsync(_keys.BuildKey(tenantId, key), value,
                tags: new[] { _keys.TenantTag(tenantId) });
        }

        public async Task RemoveAsync(string tenantId, string key)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(key))
                return;

            await _store.RemoveAsync(_keys.BuildKey(tenantId, key));
        }

        public async Task RemoveAllTenantAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return;

            await _store.RemoveByTagAsync(_keys.TenantTag(tenantId));
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

    /// <summary>Defaults for <see cref="TenantInfoProvider{TTenantInfo}"/>.</summary>
    public static class TenantInfoProviderDefaults
    {
        /// <summary>Leading segment of the tenant-info key when none is configured.</summary>
        public const string KeyPrefix = "tenant-info";
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
            string cacheKeyPrefix = TenantInfoProviderDefaults.KeyPrefix)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _dataFetch = dataFetch ?? throw new ArgumentNullException(nameof(dataFetch));
            if (string.IsNullOrWhiteSpace(cacheKeyPrefix))
                cacheKeyPrefix = TenantInfoProviderDefaults.KeyPrefix;

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

    /// <summary>Why the resolution middleware could not put a tenant on the request.</summary>
    public enum TenantResolutionFailureReason
    {
        /// <summary>
        /// No tenant id could be resolved from the request at all.
        /// <para>
        /// This is the <b>normal</b> outcome for any request that is not tenant-scoped — with the
        /// annotation-based resolver it fires on every endpoint without a
        /// <see cref="TenantContextAttribute"/>, and with the pattern-based ones on every path
        /// that does not match. Handle it only if every request through this middleware is meant
        /// to carry a tenant; otherwise return <c>null</c> and let the pipeline continue.
        /// </para>
        /// </summary>
        TenantNotResolved,

        /// <summary>
        /// A tenant id was resolved, but the configured data fetch produced no tenant for it —
        /// an unknown or deleted tenant. Typically a 404.
        /// </summary>
        TenantNotFound,

        /// <summary>
        /// Fetching the tenant threw. Covers the whole cache-first path, so both the cache read
        /// and the configured data fetch behind it. Typically a 503 or 500.
        /// </summary>
        TenantRetrievalFailed
    }

    /// <summary>
    /// What the resolution middleware knows about a failed tenant resolution, handed to the
    /// handler configured with
    /// <see cref="TenantContextCacheBuilder.WithTenantResolutionFailureHandler(Func{TenantResolutionFailure, IResult})"/>.
    /// </summary>
    public sealed class TenantResolutionFailure
    {
        public TenantResolutionFailure(
            HttpContext httpContext,
            TenantResolutionFailureReason reason,
            string tenantId,
            Exception exception)
        {
            HttpContext = httpContext;
            Reason = reason;
            TenantId = tenantId;
            Exception = exception;
        }

        /// <summary>The request being resolved.</summary>
        public HttpContext HttpContext { get; }

        /// <summary>What went wrong.</summary>
        public TenantResolutionFailureReason Reason { get; }

        /// <summary>
        /// The resolved tenant id, or <c>null</c> when <see cref="Reason"/> is
        /// <see cref="TenantResolutionFailureReason.TenantNotResolved"/>.
        /// </summary>
        public string TenantId { get; }

        /// <summary>
        /// The exception that was thrown, set only when <see cref="Reason"/> is
        /// <see cref="TenantResolutionFailureReason.TenantRetrievalFailed"/>.
        /// </summary>
        public Exception Exception { get; }
    }

    /// <summary>
    /// Options for <see cref="TenantResolutionMiddleware"/>, registered as a singleton by
    /// <see cref="TenantContextCacheExtensions.AddTenantContextCache"/>.
    /// </summary>
    public class TenantResolutionOptions
    {
        /// <summary>
        /// Called when the middleware cannot put a tenant on the request. Returning an
        /// <see cref="IResult"/> writes it to the response and short-circuits the pipeline;
        /// returning <c>null</c> lets the request continue as if no handler were configured.
        /// When unset, every failure continues (and a retrieval exception propagates).
        /// </summary>
        public Func<TenantResolutionFailure, Task<IResult>> FailureHandler { get; set; }
    }

    /// <summary>
    /// Middleware that resolves the tenant for each request, fetches its tenant info through the
    /// multi-tier cache, and injects both the tenant id and the tenant info into
    /// <see cref="HttpContext.Items"/> for the rest of the pipeline (read back via
    /// <see cref="ITenantContextAccessor"/>).
    /// <para>
    /// When resolution fails, the handler on <see cref="TenantResolutionOptions"/> decides what
    /// happens. Without one the behaviour is unchanged: an unresolved or unknown tenant simply
    /// continues down the pipeline with no tenant attached, and a retrieval exception propagates.
    /// </para>
    /// </summary>
    public class TenantResolutionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenantResolver _tenantResolver;
        private readonly TenantResolutionOptions _options;

        public TenantResolutionMiddleware(
            RequestDelegate next,
            ITenantResolver tenantResolver,
            TenantResolutionOptions options = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
            _options = options ?? new TenantResolutionOptions();
        }

        // tenantInfoProvider is injected per-request from the request services (scoped), so the
        // fetch runs against the tenant resolved on this request.
        public async Task InvokeAsync(HttpContext context, ITenantInfoProvider tenantInfoProvider)
        {
            var tenantId = _tenantResolver.ResolveTenant(context);

            if (string.IsNullOrEmpty(tenantId))
            {
                if (await TryHandleAsync(context, TenantResolutionFailureReason.TenantNotResolved, null, null))
                    return;

                await _next(context);
                return;
            }

            context.Items["TenantId"] = tenantId;

            object tenantInfo = null;
            ExceptionDispatchInfo retrievalFailure = null;
            try
            {
                tenantInfo = await tenantInfoProvider.GetTenantInfoAsync(tenantId);
            }
            catch (Exception ex)
            {
                // Captured rather than handled inline so the handler runs outside the catch block
                // and an unhandled failure can be rethrown with its original stack trace.
                retrievalFailure = ExceptionDispatchInfo.Capture(ex);
            }

            if (retrievalFailure != null)
            {
                if (await TryHandleAsync(context, TenantResolutionFailureReason.TenantRetrievalFailed,
                        tenantId, retrievalFailure.SourceException))
                    return;

                retrievalFailure.Throw();
            }

            if (tenantInfo == null)
            {
                if (await TryHandleAsync(context, TenantResolutionFailureReason.TenantNotFound, tenantId, null))
                    return;
            }
            else
            {
                context.Items[$"TenantInfo:{tenantInfoProvider.TenantInfoType.Name}"] = tenantInfo;
            }

            await _next(context);
        }

        /// <summary>
        /// Runs the configured handler, if any. Returns true when it produced a result, which has
        /// been written to the response — the caller must then short-circuit.
        /// </summary>
        private async Task<bool> TryHandleAsync(
            HttpContext context,
            TenantResolutionFailureReason reason,
            string tenantId,
            Exception exception)
        {
            var handler = _options.FailureHandler;
            if (handler == null)
                return false;

            var result = await handler(new TenantResolutionFailure(context, reason, tenantId, exception));
            if (result == null)
                return false;

            await result.ExecuteAsync(context);
            return true;
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
        private readonly TenantResolutionOptions _resolutionOptions = new();
        private Func<IServiceProvider, IDistributedCache> _customL2Factory;
        private Func<IServiceProvider, ICacheStore> _existingStoreFactory;
        private Func<IServiceProvider, ICacheKeyBuilder> _keyBuilderFactory;
        private Action<IServiceCollection> _registerTenantInfoProvider;

        // Which engine-specific setters the caller actually invoked. Tracked so Build() can
        // reject combinations that would be silently ignored when a custom store replaces the
        // built-in engine, regardless of the order the builder calls were made in. Key layout is
        // deliberately absent: it belongs to the library on both paths, so the key options stay
        // valid when you bring your own store.
        private readonly List<string> _engineSettersUsed = new();

        // Which key-layout setters were used, so Build() can reject a custom key builder being
        // combined with the prefix options it would override. Order-independent, like the engine
        // checks: calling either one twice is harmless, only the combination is a conflict.
        private readonly List<string> _keySettersUsed = new();

        public TenantContextCacheBuilder(IServiceCollection services)
        {
            _services = services;
        }

        public TenantContextCacheBuilder WithL1TimeToLive(TimeSpan ttl)
        {
            _config.L1TimeToLive = ttl;
            _engineSettersUsed.Add(nameof(WithL1TimeToLive));
            return this;
        }

        public TenantContextCacheBuilder WithL2TimeToLive(TimeSpan ttl)
        {
            _config.L2TimeToLive = ttl;
            _engineSettersUsed.Add(nameof(WithL2TimeToLive));
            return this;
        }

        /// <summary>
        /// Override the leading segment of every cache key and per-tenant tag (default "tenant"),
        /// and optionally the string between segments (default ":").
        /// <para>
        /// A prefix of "myapp" produces keys like <c>myapp:acme:tenant-info:TenantInfo</c> under
        /// the tag <c>myapp:acme</c>; <c>WithCacheKeyPrefix("TENANT-CONTENT", "-")</c> produces
        /// <c>TENANT-CONTENT-acme-user-1</c> under <c>TENANT-CONTENT-acme</c>. Useful to namespace
        /// entries when several apps share one Redis instance, or to match the key convention of a
        /// cache you already run.
        /// </para>
        /// <para>
        /// This applies to the built-in engine and to your own <see cref="ICacheStore"/> alike —
        /// keys are composed above the store on both paths. For a layout this cannot express, see
        /// <see cref="WithCacheKeyBuilder(ICacheKeyBuilder)"/>.
        /// </para>
        /// </summary>
        public TenantContextCacheBuilder WithCacheKeyPrefix(string prefix, string separator = TenantCacheKeyBuilder.DefaultSeparator)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Cache key prefix must not be null or empty.", nameof(prefix));

            if (string.IsNullOrEmpty(separator))
                throw new ArgumentException("Cache key separator must not be null or empty.", nameof(separator));

            _config.CacheKeyPrefix = prefix;
            _config.CacheKeySeparator = separator;
            _keySettersUsed.Add(nameof(WithCacheKeyPrefix));
            return this;
        }

        /// <summary>
        /// Override the leading segment of the key the library stores tenant info under (default
        /// "tenant-info"), which becomes <c>{prefix}:{TenantInfoTypeName}</c> before the tenant
        /// prefix is applied. This is the one key the library chooses on your behalf rather than
        /// receiving from a caller.
        /// </summary>
        public TenantContextCacheBuilder WithTenantInfoKeyPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Tenant info key prefix must not be null or empty.", nameof(prefix));

            _config.TenantInfoKeyPrefix = prefix;
            return this;
        }

        /// <summary>
        /// Take full control of key layout by supplying your own <see cref="ICacheKeyBuilder"/>.
        /// It decides both the key a (tenant, key) pair is stored under and the tag that backs
        /// per-tenant bulk eviction, so it replaces — and cannot be combined with —
        /// <see cref="WithCacheKeyPrefix"/>.
        /// </summary>
        public TenantContextCacheBuilder WithCacheKeyBuilder(ICacheKeyBuilder keyBuilder)
        {
            if (keyBuilder == null)
                throw new ArgumentNullException(nameof(keyBuilder));

            return WithCacheKeyBuilder(_ => keyBuilder);
        }

        /// <summary>
        /// Take full control of key layout with a key builder resolved from a factory. The factory
        /// receives the application (root) <see cref="IServiceProvider"/> and is invoked once, so
        /// it should depend only on singleton services. See
        /// <see cref="WithCacheKeyBuilder(ICacheKeyBuilder)"/>.
        /// </summary>
        public TenantContextCacheBuilder WithCacheKeyBuilder(Func<IServiceProvider, ICacheKeyBuilder> factory)
        {
            _keyBuilderFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            _keySettersUsed.Add(nameof(WithCacheKeyBuilder));
            return this;
        }

        /// <summary>
        /// Override the name of the FusionCache instance the library registers (default
        /// "tenant-context"). The library uses a named instance so the default, unnamed
        /// <c>IFusionCache</c> stays available for your own <c>AddFusionCache()</c> registration.
        /// Retrieve the library's instance via <c>IFusionCacheProvider.GetCache(name)</c>.
        /// </summary>
        public TenantContextCacheBuilder WithCacheName(string cacheName)
        {
            if (string.IsNullOrWhiteSpace(cacheName))
                throw new ArgumentException("Cache name must not be null or empty.", nameof(cacheName));

            _config.CacheName = cacheName;
            _engineSettersUsed.Add(nameof(WithCacheName));
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
                        tenantId => fetch(sp, tenantId),
                        sp.GetRequiredService<CacheConfiguration>().TenantInfoKeyPrefix));
            return this;
        }

        /// <summary>
        /// Handle requests where the middleware cannot put a tenant on the request: no tenant id
        /// in the request, no tenant found for the id, or the retrieval throwing. The handler
        /// returns the HTTP result to reply with, which short-circuits the pipeline.
        /// <para>
        /// Return <c>null</c> for a failure you do not want to intercept and that request carries
        /// on exactly as it would without a handler — which matters for
        /// <see cref="TenantResolutionFailureReason.TenantNotResolved"/>, the normal outcome for
        /// every request that is not tenant-scoped:
        /// </para>
        /// <code>
        /// .WithTenantResolutionFailureHandler(failure => failure.Reason switch
        /// {
        ///     TenantResolutionFailureReason.TenantNotFound       => Results.NotFound($"Unknown tenant '{failure.TenantId}'."),
        ///     TenantResolutionFailureReason.TenantRetrievalFailed => Results.Problem("Tenant lookup failed.", statusCode: 503),
        ///     _                                                   => null,
        /// })
        /// </code>
        /// <para>
        /// Note that an unhandled <see cref="TenantResolutionFailureReason.TenantRetrievalFailed"/>
        /// — no handler, or one that returned <c>null</c> — rethrows the original exception, so
        /// your own exception middleware still sees it.
        /// </para>
        /// </summary>
        public TenantContextCacheBuilder WithTenantResolutionFailureHandler(
            Func<TenantResolutionFailure, IResult> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return WithTenantResolutionFailureHandler(failure => Task.FromResult(handler(failure)));
        }

        /// <summary>
        /// Handle failed tenant resolution with an asynchronous handler — use this overload when
        /// deciding the response needs to await something (a lookup, a log write, …). See
        /// <see cref="WithTenantResolutionFailureHandler(Func{TenantResolutionFailure, IResult})"/>
        /// for the semantics.
        /// </summary>
        public TenantContextCacheBuilder WithTenantResolutionFailureHandler(
            Func<TenantResolutionFailure, Task<IResult>> handler)
        {
            _resolutionOptions.FailureHandler = handler ?? throw new ArgumentNullException(nameof(handler));
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
        /// Bring your own cache: store through an existing <see cref="ICacheStore"/> implementation
        /// instead of the built-in FusionCache engine.
        /// <para>
        /// This replaces the <b>whole</b> caching engine, not just the L2 layer (for that, see
        /// <see cref="WithCustomL2(IDistributedCache)"/>). The library registers no FusionCache at
        /// all and routes every read/write through your store, so cache tiers, TTLs, serialization
        /// and the tag index behind <see cref="ICacheStore.RemoveByTagAsync"/> become yours to
        /// honour. Everything else the library provides — tenant resolution, the middleware, the
        /// cache-first <see cref="ITenantInfoProvider"/> and the per-request
        /// <see cref="ITenantCache"/> — keeps working unchanged.
        /// </para>
        /// <para>
        /// Keys are <b>not</b> yours to compose: your store receives flat keys already built by the
        /// configured <see cref="ICacheKeyBuilder"/>, so it never sees a tenant id. Shape those keys
        /// with <see cref="WithCacheKeyPrefix"/> or <see cref="WithCacheKeyBuilder(ICacheKeyBuilder)"/>,
        /// which stay valid on this path.
        /// </para>
        /// <para>
        /// The engine-specific options (<see cref="WithCustomL2(IDistributedCache)"/>,
        /// <see cref="WithL1TimeToLive"/>, <see cref="WithL2TimeToLive"/> and
        /// <see cref="WithCacheName"/>) do describe an engine that is no longer there; combining
        /// them with this call is rejected at startup rather than silently ignored.
        /// </para>
        /// </summary>
        public TenantContextCacheBuilder WithExistingCache(ICacheStore store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            return WithExistingCache(_ => store);
        }

        /// <summary>
        /// Bring your own cache, resolved from a factory. The factory receives the application
        /// (root) <see cref="IServiceProvider"/> and is invoked once, so it should depend only on
        /// singleton services. See <see cref="WithExistingCache(ICacheStore)"/> for what supplying
        /// your own store implies.
        /// </summary>
        public TenantContextCacheBuilder WithExistingCache(Func<IServiceProvider, ICacheStore> factory)
        {
            _existingStoreFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            return this;
        }

        /// <summary>
        /// Bring your own cache, resolved from DI by its type. The type is registered as a
        /// singleton only if you have not registered it yourself, so an existing registration
        /// (with its own lifetime and dependencies) always wins. See
        /// <see cref="WithExistingCache(ICacheStore)"/> for what supplying your own store implies.
        /// </summary>
        public TenantContextCacheBuilder WithExistingCache<TStore>()
            where TStore : class, ICacheStore
        {
            _services.TryAddSingleton<TStore>();
            return WithExistingCache(sp => sp.GetRequiredService<TStore>());
        }

        /// <summary>
        /// Validates the configuration and performs DI registration. Called once by
        /// <see cref="TenantContextCacheExtensions.AddTenantContextCache"/> after the caller's
        /// configuration has run, so builder calls are order-independent.
        /// </summary>
        internal void Build()
        {
            if (_registerTenantInfoProvider == null)
                throw new InvalidOperationException(
                    "No tenant-data fetch configured. Call WithTenantDataFetch<TTenantInfo>(...): " +
                    "fetching and injecting tenant data through the cache is this library's primary function.");

            if (_keyBuilderFactory != null && _keySettersUsed.Contains(nameof(WithCacheKeyPrefix)))
                throw new InvalidOperationException(
                    "WithCacheKeyBuilder(...) decides the complete key layout, so WithCacheKeyPrefix(...) " +
                    "would have no effect. Fold the prefix into your ICacheKeyBuilder, or drop " +
                    "WithCacheKeyBuilder(...) to use the default prefix-based layout.");

            _services.AddSingleton(_config);

            // Picked up by TenantResolutionMiddleware, which every UseTenantContextCache*
            // overload registers, so the failure handler applies whichever resolver is used.
            _services.AddSingleton(_resolutionOptions);

            // Key layout is the library's on both paths, so it is registered before the engine
            // choice and applies identically to a custom store and the built-in one.
            Func<IServiceProvider, ICacheKeyBuilder> keyBuilderFactory = _keyBuilderFactory ??
                (_ => new TenantCacheKeyBuilder(_config.CacheKeyPrefix, _config.CacheKeySeparator));
            _services.AddSingleton(keyBuilderFactory);

            // Exactly one engine backs ICacheStore: either the caller's own implementation
            // (bring your own cache) or the built-in FusionCache one.
            if (_existingStoreFactory != null)
                RegisterExistingCache();
            else
                RegisterFusionCache();

            // The tenant-aware layer is the same either way: it composes keys and tags, then
            // delegates to whichever store was registered above.
            _services.AddSingleton<ITenantContextCache>(sp => new TenantContextCache(
                sp.GetRequiredService<ICacheStore>(),
                sp.GetRequiredService<ICacheKeyBuilder>()));

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

        /// <summary>
        /// Bring-your-own-cache path: register the caller's <see cref="ICacheStore"/> and build no
        /// engine of our own. Options that only describe the built-in engine are rejected here
        /// instead of being silently dropped. Key layout is not among them — the library composes
        /// keys above the store either way.
        /// </summary>
        private void RegisterExistingCache()
        {
            var ignored = new List<string>(_engineSettersUsed);
            if (_customL2Factory != null)
                ignored.Insert(0, nameof(WithCustomL2));

            if (ignored.Count > 0)
                throw new InvalidOperationException(
                    $"WithExistingCache(...) supplies the complete caching engine, so {string.Join(", ", ignored)} " +
                    "would have no effect: tiers, TTLs and instance naming are decided by your ICacheStore " +
                    "implementation. Remove those calls, or drop WithExistingCache(...) to use the built-in " +
                    "FusionCache engine. (Key layout is unaffected: WithCacheKeyPrefix(...) and " +
                    "WithCacheKeyBuilder(...) apply to your store too.)");

            _services.AddSingleton(_existingStoreFactory);
        }

        /// <summary>
        /// Built-in engine path: a named FusionCache over the caller-supplied L2, adapted to
        /// <see cref="ICacheStore"/> by <see cref="FusionCacheStore"/>.
        /// </summary>
        private void RegisterFusionCache()
        {
            if (_customL2Factory == null)
                throw new InvalidOperationException(
                    "No L2 (distributed) cache configured. Call WithCustomL2(...) with an IDistributedCache backend, " +
                    "or WithExistingCache(...) to supply your own ICacheStore implementation instead.");

            // FusionCache provides the hybrid L1 (in-memory) + L2 (distributed) engine.
            // L1 uses the shorter Duration; L2 keeps entries for the longer
            // DistributedCacheDuration, matching the previous two-tier TTL semantics.
            // The instance is registered under a name (default "tenant-context") so the default,
            // unnamed IFusionCache stays free for the host app's own AddFusionCache().
            var fusion = _services.AddFusionCache(_config.CacheName)
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

            // Resolve the library's named FusionCache through the provider (not the default
            // IFusionCache), so it never collides with a default cache the host app registers.
            _services.AddSingleton<ICacheStore>(sp =>
            {
                var fusionCache = sp.GetRequiredService<IFusionCacheProvider>().GetCache(_config.CacheName);
                return new FusionCacheStore(fusionCache);
            });
        }
    }
}
