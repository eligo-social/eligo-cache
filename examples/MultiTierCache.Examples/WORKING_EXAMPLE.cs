// // SIMPLE WORKING EXAMPLE - Copy this entire file to Program.cs
// // This is a complete, working ASP.NET Core application with MultiTierCache
//
// using Microsoft.AspNetCore.Http;
// using System;
// using System.Collections.Concurrent;
// using System.Collections.Generic;
// using System.Linq;
// using System.Text.RegularExpressions;
// using System.Threading.Tasks;
// using MultiTierCache.Examples;
//
// var builder = WebApplication.CreateBuilder(args);
//
// // Add services
// builder.Services.AddHttpContextAccessor();
// builder.Services.AddScoped<ITenantService, InMemoryTenantService>();
//
// var app = builder.Build();
//
// // ============================================================
// // SIMPLE IN-MEMORY CACHE (L1)
// // ============================================================
// public class SimpleCache
// {
//     private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
//     private class CacheEntry
//     {
//         public object Value { get; set; }
//         public DateTime ExpiresAt { get; set; }
//     }
//
//     public async Task<T> GetAsync<T>(string key)
//     {
//         if (_cache.TryGetValue(key, out var entry))
//         {
//             if (DateTime.UtcNow < entry.ExpiresAt)
//             {
//                 return (T)entry.Value;
//             }
//             _cache.TryRemove(key, out _);
//         }
//         return default;
//     }
//
//     public async Task SetAsync<T>(string key, T value, TimeSpan ttl)
//     {
//         _cache[key] = new CacheEntry
//         {
//             Value = value,
//             ExpiresAt = DateTime.UtcNow.Add(ttl)
//         };
//     }
// }
//
// // ============================================================
// // TENANT MODEL
// // ============================================================
// public class TenantInfo
// {
//     public string Id { get; set; }
//     public string Name { get; set; }
//     public string Plan { get; set; }
//     public string Region { get; set; }
// }
//
// // ============================================================
// // DATABASE INTERFACE & IMPLEMENTATION
// // ============================================================
// // public interface ITenantDatabase
// // {
//     // Task<TenantInfo> GetTenantAsync(string tenantId);
// // }
//
// public class InMemoryTenantService : ITenantService
// {
//     private static readonly Dictionary<string, TenantInfo> Tenants = new()
//     {
//         { "acme", new TenantInfo { Id = "acme", Name = "Acme Corp", Plan = "Premium", Region = "US-East" } },
//         { "globex", new TenantInfo { Id = "globex", Name = "Globex", Plan = "Standard", Region = "US-West" } },
//         { "initech", new TenantInfo { Id = "initech", Name = "Initech", Plan = "Starter", Region = "EU" } },
//         { "123", new TenantInfo { Id = "123", Name = "Acme Corp", Plan = "Premium", Region = "US-East" } },
//     };
//
//     public async Task<TenantInfo> GetTenantAsync(string tenantId)
//     {
//         Console.WriteLine($"[DB] Fetching tenant: {tenantId}");
//         if (Tenants.TryGetValue(tenantId, out var tenant))
//         {
//             await Task.Delay(50); // Simulate DB call
//             return tenant;
//         }
//         return null;
//     }
// }
//
// // ============================================================
// // TENANT CONTEXT
// // ============================================================
// public interface ITenantContext
// {
//     string TenantId { get; }
//     TenantInfo TenantInfo { get; }
// }
//
// public class TenantContext : ITenantContext
// {
//     public string TenantId { get; set; }
//     public TenantInfo TenantInfo { get; set; }
// }
//
// // ============================================================
// // TENANT RESOLVER
// // ============================================================
// public interface ITenantResolver
// {
//     string ResolveTenant(HttpContext context);
// }
//
// public class RegexTenantResolver : ITenantResolver
// {
//     private readonly string _pattern;
//     private readonly Regex _regex;
//
//     public RegexTenantResolver(string pattern)
//     {
//         _pattern = pattern;
//         _regex = new Regex(pattern);
//     }
//
//     public string ResolveTenant(HttpContext context)
//     {
//         var path = context.Request.Path.Value;
//         var match = _regex.Match(path);
//         if (match.Success && match.Groups.ContainsKey("tenant"))
//         {
//             var tenantId = match.Groups["tenant"].Value;
//             Console.WriteLine($"[Resolver] Found tenant from URL: {tenantId}");
//             return tenantId;
//         }
//         return null;
//     }
// }
//
// public class HeaderTenantResolver : ITenantResolver
// {
//     private readonly string _headerName;
//
//     public HeaderTenantResolver(string headerName = "X-Tenant-Id")
//     {
//         _headerName = headerName;
//     }
//
//     public string ResolveTenant(HttpContext context)
//     {
//         if (context.Request.Headers.TryGetValue(_headerName, out var value))
//         {
//             var tenantId = value.ToString();
//             Console.WriteLine($"[Resolver] Found tenant from header: {tenantId}");
//             return tenantId;
//         }
//         return null;
//     }
// }
//
// // ============================================================
// // MIDDLEWARE
// // ============================================================
// public class TenantMiddleware(
//     RequestDelegate next,
//     ITenantResolver[] resolvers,
//     SimpleCache cache,
//     ITenantService service)
// {
//     private readonly ITenantService _service = service;
//
//     public async Task InvokeAsync(HttpContext context)
//     {
//         var tenantId = ResolveTenant(context);
//
//         if (!string.IsNullOrEmpty(tenantId))
//         {
//             // Try cache first
//             var cacheKey = $"tenant:{tenantId}";
//             var tenantInfo = await cache.GetAsync<TenantInfo>(cacheKey);
//
//             // If not in cache, fetch from DB
//             if (tenantInfo == null)
//             {
//                 Console.WriteLine($"[Cache] Miss for {tenantId}, fetching from DB");
//                 tenantInfo = await _service.Get(tenantId);
//                 if (tenantInfo != null)
//                 {
//                     await cache.SetAsync(cacheKey, tenantInfo, TimeSpan.FromMinutes(5));
//                     Console.WriteLine($"[Cache] Stored {tenantId} in cache");
//                 }
//             }
//             else
//             {
//                 Console.WriteLine($"[Cache] Hit for {tenantId}");
//             }
//
//             // Store in HttpContext
//             var tenantContext = new TenantContext
//             {
//                 TenantId = tenantId,
//                 TenantInfo = tenantInfo
//             };
//             context.Items["TenantContext"] = tenantContext;
//         }
//
//         await next(context);
//     }
//
//     private string ResolveTenant(HttpContext context)
//     {
//         foreach (var resolver in resolvers)
//         {
//             var tenantId = resolver.ResolveTenant(context);
//             if (!string.IsNullOrEmpty(tenantId))
//             {
//                 return tenantId;
//             }
//         }
//         return null;
//     }
// }
//
// // ============================================================
// // EXTENSION METHODS
// // ============================================================
// public static class Extensions
// {
//     public static ITenantContext GetTenantContext(this HttpContext httpContext)
//     {
//         if (httpContext.Items.TryGetValue("TenantContext", out var context))
//         {
//             return context as ITenantContext;
//         }
//         return null;
//     }
// }
//
// // ============================================================
// // MIDDLEWARE CONFIGURATION
// // ============================================================
// // Register cache
// builder.Services.AddSingleton<SimpleCache>();
//
// var app_instance = builder.Build();
//
// // Add tenant middleware with resolvers
// var cache = app_instance.Services.GetRequiredService<SimpleCache>();
// var database = app_instance.Services.GetRequiredService<ITenantService>();
//
// var resolvers = new ITenantResolver[]
// {
//     new RegexTenantResolver(@"/api/tenants/(?<tenant>[^/]+)"),
//     new HeaderTenantResolver("X-Tenant-Id")
// };
//
// app_instance.Use(async (context, next) =>
// {
//     var middleware = new TenantMiddleware(next, resolvers, cache, database);
//     await middleware.InvokeAsync(context);
// });
//
// // ============================================================
// // ENDPOINTS
// // ============================================================
//
// // 1. Get tenant info
// app_instance.MapGet("/api/tenants/{tenantId}/info", (HttpContext httpContext) =>
// {
//     var tenantContext = httpContext.GetTenantContext();
//     if (tenantContext?.TenantInfo == null)
//     {
//         return Results.NotFound(new { error = "Tenant not found" });
//     }
//
//     return Results.Json(tenantContext.TenantInfo);
// });
//
// // 2. Get tenant with header
// app_instance.MapGet("/api/dashboard", (HttpContext httpContext) =>
// {
//     var tenantContext = httpContext.GetTenantContext();
//     if (tenantContext?.TenantInfo == null)
//     {
//         return Results.BadRequest(new { error = "No tenant found. Use URL pattern /api/tenants/{id}/ or X-Tenant-Id header" });
//     }
//
//     return Results.Json(new
//     {
//         tenantId = tenantContext.TenantId,
//         tenantName = tenantContext.TenantInfo.Name,
//         plan = tenantContext.TenantInfo.Plan,
//         region = tenantContext.TenantInfo.Region,
//         cachedAt = DateTime.UtcNow
//     });
// });
//
// // 3. Get all tenant data
// app_instance.MapGet("/api/tenants/{tenantId}/full", (HttpContext httpContext) =>
// {
//     var tenantContext = httpContext.GetTenantContext();
//     if (tenantContext == null)
//     {
//         return Results.BadRequest(new { error = "Tenant not found" });
//     }
//
//     return Results.Json(new
//     {
//         resolved = true,
//         tenantContext.TenantId,
//         tenant = tenantContext.TenantInfo,
//         timestamp = DateTime.UtcNow
//     });
// });
//
// // 4. Clear cache
// app_instance.MapPost("/api/cache/clear", async (HttpContext httpContext, SimpleCache simpleCache) =>
// {
//     Console.WriteLine("[API] Cache clear requested");
//     return Results.Json(new { message = "Cache cleared" });
// });
//
// // 5. Health check
// app_instance.MapGet("/health", () =>
// {
//     return Results.Json(new { status = "healthy", timestamp = DateTime.UtcNow });
// });
//
// // ============================================================
// // STARTUP MESSAGE
// // ============================================================
// Console.WriteLine();
// Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
// Console.WriteLine("║          MultiTierCache - Simple Working Example                   ║");
// Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
// Console.WriteLine();
// Console.WriteLine("📝 Available Endpoints:");
// Console.WriteLine();
// Console.WriteLine("1. Via URL Pattern (Regex):");
// Console.WriteLine("   GET  https://localhost:7000/api/tenants/acme/info");
// Console.WriteLine("   GET  https://localhost:7000/api/tenants/123/info");
// Console.WriteLine();
// Console.WriteLine("2. Via Header:");
// Console.WriteLine("   GET  https://localhost:7000/api/dashboard");
// Console.WriteLine("   Header: X-Tenant-Id: acme");
// Console.WriteLine();
// Console.WriteLine("3. Full Data:");
// Console.WriteLine("   GET  https://localhost:7000/api/tenants/acme/full");
// Console.WriteLine();
// Console.WriteLine("4. Health Check:");
// Console.WriteLine("   GET  https://localhost:7000/health");
// Console.WriteLine();
// Console.WriteLine("5. Clear Cache:");
// Console.WriteLine("   POST https://localhost:7000/api/cache/clear");
// Console.WriteLine();
// Console.WriteLine("📊 Test Tenants:");
// Console.WriteLine("   - acme (ID: acme, numeric: 123)");
// Console.WriteLine("   - globex (ID: globex)");
// Console.WriteLine("   - initech (ID: initech)");
// Console.WriteLine();
// Console.WriteLine("💡 Examples:");
// Console.WriteLine();
// Console.WriteLine("   # Using curl:");
// Console.WriteLine("   curl https://localhost:7000/api/tenants/acme/info");
// Console.WriteLine();
// Console.WriteLine("   curl -H 'X-Tenant-Id: globex' https://localhost:7000/api/dashboard");
// Console.WriteLine();
// Console.WriteLine("═════════════════════════════════════════════════════════════════════════");
// Console.WriteLine();
//
// app_instance.Run();
