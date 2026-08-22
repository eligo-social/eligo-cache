using ZiggyCreatures.Caching.Fusion;

namespace TenantContextCache
{
    /// <summary>
    /// The storage seam of the library: a plain key/value cache with no tenant vocabulary.
    /// <para>
    /// Everything tenant-aware lives <b>above</b> this interface. By the time a call reaches a
    /// store the tenant has already been folded into <paramref name="key"/> by the configured
    /// <see cref="ICacheKeyBuilder"/>, so the store never has to know what a tenant is — it just
    /// reads and writes the keys it is given. That is what makes key layout the caller's
    /// decision: change the key builder and every key this store sees changes with it.
    /// </para>
    /// <para>
    /// Implement this to bring your own cache (see
    /// <see cref="TenantContextCacheBuilder.WithExistingCache(ICacheStore)"/>); the built-in
    /// engine implements it as <see cref="FusionCacheStore"/>.
    /// </para>
    /// </summary>
    public interface ICacheStore
    {
        /// <summary>Returns the entry stored under <paramref name="key"/>, or <c>default</c> on a miss.</summary>
        Task<T> GetAsync<T>(string key);

        /// <summary>
        /// Stores <paramref name="value"/> under <paramref name="key"/>, associated with
        /// <paramref name="tags"/>. Tags are opaque strings used for group eviction — the library
        /// passes one tag per entry, <see cref="ICacheKeyBuilder.TenantTag"/>, so that a whole
        /// tenant can be dropped in a single call. A store that has no tagging support may ignore
        /// them, as long as <see cref="RemoveByTagAsync"/> is implemented some other way.
        /// </summary>
        Task SetAsync<T>(string key, T value, IReadOnlyCollection<string> tags = null);

        /// <summary>Removes the single entry stored under <paramref name="key"/>.</summary>
        Task RemoveAsync(string key);

        /// <summary>
        /// Removes every entry associated with <paramref name="tag"/>. This backs per-tenant bulk
        /// invalidation, so it cannot be expressed as a loop over <see cref="RemoveAsync"/> — the
        /// library never enumerates keys.
        /// <para>
        /// If your backend has no tag index, the default <see cref="TenantCacheKeyBuilder"/>
        /// guarantees the tag is also a literal prefix of every key it belongs to, so a prefix
        /// scan (Redis <c>SCAN MATCH tag*</c>, dropping a per-tenant hash or logical database, …)
        /// is a valid implementation. Throwing <see cref="NotSupportedException"/> is acceptable
        /// too, if you never call <see cref="ITenantContextCache.RemoveAllTenantAsync"/>.
        /// </para>
        /// </summary>
        Task RemoveByTagAsync(string tag);
    }

    /// <summary>
    /// Composes the flat cache keys and group-eviction tags handed to an <see cref="ICacheStore"/>.
    /// This is the single place where a tenant id turns into a string, so replacing it via
    /// <see cref="TenantContextCacheBuilder.WithCacheKeyBuilder(ICacheKeyBuilder)"/> gives you
    /// complete control over the layout of everything the library stores.
    /// <para>
    /// One invariant: <see cref="TenantTag"/> must be unique per tenant, so that evicting by that
    /// tag never touches another tenant's entries.
    /// </para>
    /// </summary>
    public interface ICacheKeyBuilder
    {
        /// <summary>Full key for <paramref name="key"/> as stored on behalf of <paramref name="tenantId"/>.</summary>
        string BuildKey(string tenantId, string key);

        /// <summary>
        /// Tag applied to every entry belonging to <paramref name="tenantId"/>, and the argument
        /// passed to <see cref="ICacheStore.RemoveByTagAsync"/> for per-tenant bulk eviction.
        /// </summary>
        string TenantTag(string tenantId);
    }

    /// <summary>
    /// Default key layout: <c>{prefix}{separator}{tenantId}{separator}{key}</c>, with the
    /// per-tenant tag being the same string minus the key — e.g. a prefix of "tenant" produces
    /// the key <c>tenant:acme:user-1</c> under the tag <c>tenant:acme</c>, while a prefix of
    /// "TENANT-CONTENT" with separator "-" produces <c>TENANT-CONTENT-acme-user-1</c> under
    /// <c>TENANT-CONTENT-acme</c>.
    /// <para>
    /// Because the tag is a literal prefix of every key it covers, a store without a tag index
    /// can implement <see cref="ICacheStore.RemoveByTagAsync"/> as a prefix scan.
    /// </para>
    /// <para>
    /// Note that neither segment is escaped: a tenant id containing the separator can collide
    /// with another tenant's key. Supply your own <see cref="ICacheKeyBuilder"/> if your tenant
    /// ids are not known to be separator-free.
    /// </para>
    /// </summary>
    public sealed class TenantCacheKeyBuilder : ICacheKeyBuilder
    {
        /// <summary>Prefix used when none is configured, preserving the original key layout.</summary>
        public const string DefaultPrefix = "tenant";

        /// <summary>Separator used when none is configured.</summary>
        public const string DefaultSeparator = ":";

        private readonly string _prefix;
        private readonly string _separator;

        public TenantCacheKeyBuilder(string prefix = DefaultPrefix, string separator = DefaultSeparator)
        {
            _prefix = string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix;
            // A blank separator would run the segments together (tenant "a" + key "bc" and tenant
            // "ab" + key "c" would collide), so fall back rather than honour it.
            _separator = string.IsNullOrEmpty(separator) ? DefaultSeparator : separator;
        }

        public string TenantTag(string tenantId) => $"{_prefix}{_separator}{tenantId}";

        public string BuildKey(string tenantId, string key) => $"{TenantTag(tenantId)}{_separator}{key}";
    }

    /// <summary>
    /// The built-in engine as an <see cref="ICacheStore"/>: a thin adapter over FusionCache, which
    /// supplies the hybrid L1 (in-memory) + L2 (distributed) behaviour natively — reads are served
    /// from L1 and transparently back-filled from L2, writes fan out to both layers — plus
    /// serialization, stampede protection, fail-safe and the tag index behind
    /// <see cref="RemoveByTagAsync"/>.
    /// </summary>
    public sealed class FusionCacheStore : ICacheStore
    {
        private readonly IFusionCache _cache;

        public FusionCacheStore(IFusionCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<T> GetAsync<T>(string key)
        {
            return await _cache.GetOrDefaultAsync<T>(key);
        }

        public async Task SetAsync<T>(string key, T value, IReadOnlyCollection<string> tags = null)
        {
            await _cache.SetAsync(key, value, tags: tags);
        }

        public async Task RemoveAsync(string key)
        {
            await _cache.RemoveAsync(key);
        }

        public async Task RemoveByTagAsync(string tag)
        {
            await _cache.RemoveByTagAsync(tag);
        }
    }
}
