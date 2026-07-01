// // ============================================
// // MULTIPLE URL PATTERNS EXAMPLE
// // ============================================
// // Support both /tenants/{tenantID}/** and /Tenants/{tenantSlug}/**
//
// using Microsoft.AspNetCore.Builder;
// using Microsoft.Extensions.DependencyInjection;
// using MultiTierCache;
// using System;
// using System.Threading.Tasks;
// using MultiTierCache.Examples;
//
// // Models
//
//
// // Database
//
//
// // ============================================
// // PROGRAM.CS - OPTION 1: Route Parameters
// // ============================================
//
// var builder = WebApplication.CreateBuilder(args);
//
// builder.Services.AddHttpContextAccessor();
// builder.Services.AddScoped<ITenantService, TenantService>();
//
// builder.Services.AddMultiTierCache(cache =>
// {
//     cache
//         .WithL1TimeToLive(TimeSpan.FromMinutes(5))
//         .WithL2TimeToLive(TimeSpan.FromHours(1))
//         .WithRedisL2("localhost:6379");
// });
//
// var app = builder.Build();
//
// // Method 1: Multiple route patterns using route parameters
// app.UseMultiTierCacheWithPatterns(
//     patterns =>
//     {
//         // Try numeric tenant ID first: /tenants/{tenantId}/**
//         patterns.WithNumericTenantId("tenantId");
//         
//         // Fall back to string slug: /Tenants/{tenantSlug}/**
//         patterns.WithTenantSlug("tenantSlug");
//         
//         // Can also add header fallback
//         patterns.WithHeader("X-Tenant-Id");
//     },
//     async (tenantId) =>
//     {
//         var db = app.Services.GetRequiredService<ITenantDatabase>();
//         
//         // Try to resolve as numeric ID first
//         if (int.TryParse(tenantId, out _))
//         {
//             return await db.GetTenantByIdAsync(tenantId);
//         }
//         
//         // Fall back to slug resolution
//         return await db.GetTenantBySlugAsync(tenantId);
//     }
// );
//
// // ============================================
// // ROUTES - Works with BOTH patterns
// // ============================================
//
// // Route 1: Numeric tenant ID
// // GET /tenants/1/info
// // GET /tenants/2/data
// app.MapGroup("/tenants/{tenantId}")
//     .WithTags("Tenants by ID")
//     .MapTenantEndpoints();
//
// // Route 2: String tenant slug
// // GET /Tenants/acme/info
// // GET /Tenants/globex/data
// app.MapGroup("/Tenants/{tenantSlug}")
//     .WithTags("Tenants by Slug")
//     .MapTenantEndpoints();
//
// static class TenantEndpoints
// {
//     public static void MapTenantEndpoints(this RouteGroupBuilder group)
//     {
//         group.MapGet("info", GetTenantInfo)
//             .WithName("GetTenantInfo")
//             .WithOpenApi();
//
//         group.MapGet("data", GetTenantData)
//             .WithName("GetTenantData")
//             .WithOpenApi();
//
//         group.MapGet("summary", GetTenantSummary)
//             .WithName("GetTenantSummary")
//             .WithOpenApi();
//     }
//
//     private static IResult GetTenantInfo(ITenantContextAccessor ctx)
//     {
//         var tenantId = ctx.GetTenantId();
//         var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
//
//         if (tenantInfo == null)
//             return Results.NotFound(new { error = "Tenant not found", tenantId });
//
//         return Results.Json(new
//         {
//             tenantId,
//             tenantInfo,
//             source = "cached"
//         });
//     }
//
//     private static IResult GetTenantData(ITenantContextAccessor ctx)
//     {
//         var tenantId = ctx.GetTenantId();
//         var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
//
//         if (tenantInfo == null)
//             return Results.NotFound();
//
//         return Results.Json(new
//         {
//             id = tenantInfo.Id,
//             name = tenantInfo.Name,
//             slug = tenantInfo.Slug,
//             created = tenantInfo.CreatedAt,
//             message = "Data resolved from cache, supports both numeric ID and slug"
//         });
//     }
//
//     private static IResult GetTenantSummary(ITenantContextAccessor ctx)
//     {
//         var tenantId = ctx.GetTenantId();
//         var info = ctx.GetTenantInfo<TenantInfo>();
//
//         if (info == null)
//             return Results.NotFound();
//
//         return Results.Json(new
//         {
//             tenant = new
//             {
//                 id = info.Id,
//                 name = info.Name,
//                 slug = info.Slug
//             },
//             metadata = new
//             {
//                 resolvedAs = int.TryParse(tenantId, out _) ? "numeric_id" : "slug",
//                 tenantId = tenantId,
//                 cached = true,
//                 ttl = "5 minutes"
//             }
//         });
//     }
// }
//
// app.Run();
//
// /*
//  * USAGE EXAMPLES:
//  * 
//  * Both of these work and use the same cache:
//  * 
//  * Option A - Numeric tenant ID:
//  *   curl http://localhost:5000/tenants/1/info
//  *   curl http://localhost:5000/tenants/2/data
//  * 
//  * Option B - String tenant slug:
//  *   curl http://localhost:5000/Tenants/acme/info
//  *   curl http://localhost:5000/Tenants/globex/data
//  * 
//  * Option C - Header-based (if configured):
//  *   curl -H "X-Tenant-Id: acme" http://localhost:5000/api/data
//  * 
//  * 
//  * PATTERN RESOLUTION ORDER (in UseMultiTierCacheWithPatterns):
//  * 
//  * 1. Check route parameter "tenantId" (numeric pattern)
//  *    → Found → Use it
//  * 
//  * 2. Check route parameter "tenantSlug" (string pattern)
//  *    → Found → Use it
//  * 
//  * 3. Check "X-Tenant-Id" header
//  *    → Found → Use it
//  * 
//  * 4. Not found → Tenant unresolved
//  * 
//  * This cascading approach allows flexibility while maintaining security.
//  * 
//  * 
//  * CACHING BEHAVIOR:
//  * 
//  * Request 1: GET /tenants/1/info
//  *   → Resolved as: tenantId = "1"
//  *   → Database call: GetTenantByIdAsync("1")
//  *   → Cache key: tenant:1:tenant-info:TenantInfo
//  *   → Store in L1 (5 min) and L2 (1 hour)
//  * 
//  * Request 2: GET /Tenants/acme/info (same tenant)
//  *   → Resolved as: tenantId = "acme"
//  *   → Database call: GetTenantBySlugAsync("acme")
//  *   → Lookup database again (different resolution path)
//  *   → Cache key: tenant:acme:tenant-info:TenantInfo
//  *   → Different cache key!
//  * 
//  * IMPORTANT: The cache key is based on the resolved tenantId
//  * If you resolve the same tenant via different patterns (1 vs acme),
//  * they will have different cache keys unless you normalize them.
//  * 
//  * SOLUTION: Normalize tenant resolution to consistent ID:
//  */
//
// // ============================================
// // OPTION 2: ADVANCED - Normalized Resolution
// // ============================================
// // Ensure same tenant has same cache key regardless of input pattern
//
// var builder2 = WebApplicationBuilder.CreateBuilder(args);
// builder2.Services.AddHttpContextAccessor();
// builder2.Services.AddScoped<ITenantDatabase, TenantDatabase>();
//
// builder2.Services.AddMultiTierCache(cache =>
// {
//     cache
//         .WithL1TimeToLive(TimeSpan.FromMinutes(5))
//         .WithL2TimeToLive(TimeSpan.FromHours(1))
//         .WithRedisL2("localhost:6379");
// });
//
// var app2 = builder2.Build();
//
// // Normalized resolver - always returns the same ID for same tenant
// async Task<object> NormalizedTenantResolver(string input, ITenantDatabase db)
// {
//     // Input could be "1", "acme", or "ACME"
//     // Always normalize to numeric ID for consistent caching
//     
//     TenantInfo info = null;
//     
//     if (int.TryParse(input, out _))
//     {
//         // Numeric ID provided
//         info = await db.GetTenantByIdAsync(input);
//     }
//     else
//     {
//         // Slug provided - fetch and get numeric ID
//         info = await db.GetTenantBySlugAsync(input);
//     }
//     
//     // Return the tenant object with normalized ID
//     return info;
// }
//
// app2.UseMultiTierCacheWithPatterns(
//     patterns =>
//     {
//         patterns.WithNumericTenantId("tenantId");
//         patterns.WithTenantSlug("tenantSlug");
//     },
//     async (resolvedId) =>
//     {
//         var db = app2.Services.GetRequiredService<ITenantDatabase>();
//         return await NormalizedTenantResolver(resolvedId, db);
//     }
// );
//
// /*
//  * COMPARISON TABLE:
//  * 
//  * Pattern              | Route Param | Use Case           | Example
//  * ==================== | =========== | ================== | ==============
//  * Numeric ID          | tenantId    | Internal systems   | /tenants/123/...
//  * String Slug         | tenantSlug  | Customer-facing    | /Tenants/acme/...
//  * Header              | X-Tenant-Id | Mobile/API clients | Header: acme
//  * Subdomain           | -           | SaaS multi-tenant  | acme.example.com
//  * Custom regex        | -           | Complex patterns   | Flexible
//  * 
//  * 
//  * BEST PRACTICES:
//  * 
//  * 1. Order resolvers by frequency (most common first)
//  * 2. Normalize IDs to ensure consistent cache keys
//  * 3. Document which patterns are supported
//  * 4. Test all patterns to verify they resolve to correct tenant
//  * 5. Monitor cache hit rates (should be 80%+)
//  * 6. Consider whether slug->ID mapping should be cached separately
//  * 
//  * 
//  * ADVANCED: Cache slug-to-ID mapping separately
//  */
//
// // Optional: Cache slug mapping for faster resolution
// public interface ISlugMapping
// {
//     Task<string> GetIdBySlugAsync(string slug);
// }
//
// public class CachedSlugMapping : ISlugMapping
// {
//     private readonly ITenantDatabase _db;
//     private readonly ITenantCache _cache;
//
//     public CachedSlugMapping(ITenantDatabase db, ITenantCache cache)
//     {
//         _db = db;
//         _cache = cache;
//     }
//
//     public async Task<string> GetIdBySlugAsync(string slug)
//     {
//         var cached = await _cache.GetAsync<string>($"slug-mapping:{slug}");
//         if (cached != null)
//             return cached;
//
//         var tenant = await _db.GetTenantBySlugAsync(slug);
//         if (tenant != null)
//         {
//             await _cache.SetAsync($"slug-mapping:{slug}", tenant.Id);
//             return tenant.Id;
//         }
//
//         return null;
//     }
// }
//
// // Use slug mapping in resolver:
// // var id = await slugMapping.GetIdBySlugAsync(tenantId);
// // var tenant = await db.GetTenantByIdAsync(id);
