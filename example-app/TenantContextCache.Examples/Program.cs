// ===== Program.cs Configuration =====

using TenantContextCache;
using TenantContextCache.Examples;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddHttpContextAccessor();

// Configure multi-tier cache with database fetch function.
// The library ships no Redis dependency: the L2 (distributed) layer is provided here as a
// custom IDistributedCache. RedisDistributedCache is a small Redis-backed adapter defined in
// this example project (see RedisDistributedCache.cs).
builder.Services.AddTenantContextCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5)) // L1: 5 minutes in-memory
        .WithL2TimeToLive(TimeSpan.FromHours(1)) // L2: 1 hour in the distributed cache
        .WithCustomL2(_ => new RedisDistributedCache("localhost:6379")); // Bring your own IDistributedCache
});

// Register tenant database and service
builder.Services.AddScoped<ITenantService, TenantService>();

var app = builder.Build();

// Use tenant resolution middleware with regex pattern
// Pattern to extract tenant from URL like /api/tenants/{tenantId}/resources
// Example: /api/tenants/acme/users -> tenantId = "acme"
app.UseTenantContextCache(@"/api/tenants/(?<tenant>[^/]+)");

// Alternative: resolve the tenant from several sources with fallback.
// The first matching source wins (path -> header -> subdomain).
// app.UseTenantContextCacheWithPatterns(patterns =>
// {
//     patterns
//         .WithRegexPattern(@"/api/tenants/(?<tenant>[^/]+)") // /api/tenants/acme/...
//         .WithHeader("X-Tenant-Id")                          // fallback to header
//         .WithSubdomain();                                   // fallback to acme.example.com
// });

app.UseRouting();
app.UseEndpoints(endpoints =>
{
    endpoints.MapGet("/api/tenants/{tenantId}/info", async (
        string tenantId,
        ITenantService tenantService) =>
    {
        var tenant = await tenantService.GetTenantByIdAsync(tenantId);
        return Results.Ok(tenant);
    });

    endpoints.MapGet("/api/tenants/{tenantId}/custom-resource", async (
        string tenantId,
        ITenantCache cache) =>
    {
        // Using ITenantCache directly - tenant context is automatic
        var cached = await cache.GetAsync<string>("my-key");

        if (cached == null)
        {
            cached = $"Data for tenant {tenantId}";
            await cache.SetAsync("my-key", cached);
        }

        return Results.Ok(new { data = cached, tenant = tenantId });
    });

    endpoints.MapPost("/api/tenants/{tenantId}/invalidate-cache", async (
        string tenantId,
        ITenantContextCache cache) =>
    {
        // Clear all cache for this tenant
        await cache.RemoveAllTenantAsync(tenantId);
        return Results.Ok(new { message = $"Cache cleared for tenant {tenantId}" });
    });
});

app.Run();

/*
 * ADVANCED USAGE EXAMPLES:
 *
 * 1. Custom Tenant Resolver:
 *
 *    public class CustomTenantResolver : ITenantResolver
 *    {
 *        public string ResolveTenant(HttpContext httpContext)
 *        {
 *            // Extract tenant from header
 *            if (httpContext.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantId))
 *                return tenantId.ToString();
 *
 *            // Or from subdomain
 *            var host = httpContext.Request.Host.Host;
 *            var subdomain = host.Split('.')[0];
 *            return subdomain == "api" ? "default" : subdomain;
 *        }
 *    }
 *
 *    In ConfigureServices:
 *    services.AddSingleton<ITenantResolver, CustomTenantResolver>();
 *
 *
 * 2. Batch Tenant Data Fetching with Cache:
 *
 *    public class TenantService
 *    {
 *        public async Task<List<TenantInfo>> GetTenantsWithCacheAsync(List<string> tenantIds)
 *        {
 *            var tasks = tenantIds.Select(async id =>
 *            {
 *                var cached = await _cache.GetAsync<TenantInfo>($"tenant:{id}");
 *                return cached ?? await _database.GetTenantAsync(id);
 *            });
 *            return (await Task.WhenAll(tasks)).ToList();
 *        }
 *    }
 *
 *
 * 3. Cache Warming Strategy:
 *
 *    public async Task WarmCacheAsync(List<string> tenantIds)
 *    {
 *        foreach (var tenantId in tenantIds)
 *        {
 *            var tenant = await _database.GetTenantAsync(tenantId);
 *            await _cache.SetAsync(tenantId, "info", tenant);
 *        }
 *    }
 *
 *
 * 4. Dependency-based Cache Invalidation:
 *
 *    public async Task UpdateTenantAsync(string tenantId, TenantInfo tenant)
 *    {
 *        await _database.UpdateAsync(tenantId, tenant);
 *
 *        // Invalidate related cache entries
 *        await _cache.RemoveAsync(tenantId, "info");
 *        await _cache.RemoveAsync(tenantId, "settings");
 *        await _cache.RemoveAsync(tenantId, "users-list");
 *    }
 *
 *
 * 5. URL Pattern Variations:
 *
 *    // Subdomain-based: api.acme.example.com
 *    @"^(?<tenant>[^.]+)\.example\.com"
 *
 *    // Header-based (use custom resolver):
 *    "X-Tenant-Id"
 *
 *    // Path-based: /api/v1/tenants/acme/resources
 *    @"/api/v\d+/tenants/(?<tenant>[^/]+)"
 *
 *    // Multi-segment: /api/customers/acme/region/us/
 *    @"/api/customers/(?<tenant>[^/]+)/region"
 */
