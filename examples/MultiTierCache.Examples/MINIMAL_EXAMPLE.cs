// // ============================================
// // MINIMAL WORKING EXAMPLE
// // ============================================
// // Copy this entire file into a new .NET 8 project and run
// // Update appsettings.json with Redis connection string
//
// using Microsoft.AspNetCore.Builder;
// using Microsoft.Extensions.DependencyInjection;
// using MultiTierCache;
// using System;
// using System.Collections.Generic;
// using System.Threading.Tasks;
//
// // Models
// public class CompanyInfo
// {
//     public string Id { get; set; }
//     public string Name { get; set; }
//     public string Industry { get; set; }
//     public int EmployeeCount { get; set; }
//     public DateTime FoundedDate { get; set; }
// }
//
// // Database simulation
// public interface ICompanyDatabase
// {
//     Task<CompanyInfo> GetCompanyAsync(string companyId);
// }
//
// public class CompanyDatabase : ICompanyDatabase
// {
//     public async Task<CompanyInfo> GetCompanyAsync(string companyId)
//     {
//         // Simulate network/database call
//         await Task.Delay(100);
//
//         var companies = new Dictionary<string, CompanyInfo>
//         {
//             ["acme"] = new()
//             {
//                 Id = "acme",
//                 Name = "ACME Corporation",
//                 Industry = "Manufacturing",
//                 EmployeeCount = 500,
//                 FoundedDate = new DateTime(1990, 1, 1)
//             },
//             ["globex"] = new()
//             {
//                 Id = "globex",
//                 Name = "Globex Corporation",
//                 Industry = "Technology",
//                 EmployeeCount = 1200,
//                 FoundedDate = new DateTime(1995, 6, 15)
//             },
//             ["initech"] = new()
//             {
//                 Id = "initech",
//                 Name = "Initech",
//                 Industry = "Software",
//                 EmployeeCount = 300,
//                 FoundedDate = new DateTime(2000, 3, 20)
//             }
//         };
//
//         return companies.TryGetValue(companyId, out var company) ? company : null;
//     }
// }
//
// // Tenant context service
// public interface ICompanyContext
// {
//     string CompanyId { get; }
//     CompanyInfo CompanyInfo { get; }
// }
//
// public class CompanyContext : ICompanyContext
// {
//     private readonly ITenantContextAccessor _accessor;
//
//     public CompanyContext(ITenantContextAccessor accessor)
//     {
//         _accessor = accessor;
//     }
//
//     public string CompanyId => _accessor.GetTenantId();
//     public CompanyInfo CompanyInfo => _accessor.GetTenantInfo<CompanyInfo>();
// }
//
// // ============================================
// // PROGRAM.CS
// // ============================================
//
// var builder = WebApplication.CreateBuilder(args);
//
// // Configuration
// var config = builder.Configuration;
//
// // Services
// builder.Services.AddHttpContextAccessor();
// builder.Services.AddScoped<ICompanyDatabase, CompanyDatabase>();
// builder.Services.AddScoped<ICompanyContext, CompanyContext>();
//
// // Multi-tier cache configuration
// builder.Services.AddMultiTierCache(cache =>
// {
//     cache
//         .WithL1TimeToLive(TimeSpan.FromMinutes(5))
//         .WithL2TimeToLive(TimeSpan.FromHours(1))
//         .WithRedisL2(config["Redis:ConnectionString"] ?? "localhost:6379");
// });
//
// var app = builder.Build();
//
// // Middleware: Extract tenant from URL and load company info
// app.UseMultiTierCache(
//     @"/api/companies/(?<tenant>[^/]+)",
//     async (companyId) =>
//     {
//         var db = app.Services.GetRequiredService<ICompanyDatabase>();
//         return await db.GetCompanyAsync(companyId);
//     }
// );
//
// // ============================================
// // ENDPOINTS
// // ============================================
//
// // Example 1: Simple - return company info
// app.MapGet("/api/companies/{companyId}", (ICompanyContext ctx) =>
// {
//     if (ctx.CompanyInfo == null)
//         return Results.NotFound(new { error = "Company not found" });
//
//     return Results.Json(ctx.CompanyInfo);
// })
// .WithName("GetCompany")
// .WithOpenApi();
//
// // Example 2: Enriched response with additional logic
// app.MapGet("/api/companies/{companyId}/summary", (ICompanyContext ctx) =>
// {
//     if (ctx.CompanyInfo == null)
//         return Results.NotFound();
//
//     var company = ctx.CompanyInfo;
//     var size = company.EmployeeCount switch
//     {
//         < 100 => "small",
//         < 500 => "medium",
//         _ => "large"
//     };
//
//     var yearsInBusiness = DateTime.Now.Year - company.FoundedDate.Year;
//
//     return Results.Json(new
//     {
//         id = company.Id,
//         name = company.Name,
//         industry = company.Industry,
//         size = size,
//         yearsInBusiness = yearsInBusiness,
//         founded = company.FoundedDate.ToShortDateString(),
//         message = "All data from cache (5 min TTL)"
//     });
// })
// .WithName("GetCompanySummary")
// .WithOpenApi();
//
// // Example 3: Multiple properties
// app.MapGet("/api/companies/{companyId}/details", (ICompanyContext ctx) =>
// {
//     if (ctx.CompanyInfo == null)
//         return Results.NotFound();
//
//     return Results.Json(new
//     {
//         company = new
//         {
//             id = ctx.CompanyInfo.Id,
//             name = ctx.CompanyInfo.Name,
//             industry = ctx.CompanyInfo.Industry,
//             employees = ctx.CompanyInfo.EmployeeCount,
//             founded = ctx.CompanyInfo.FoundedDate
//         },
//         cacheInfo = new
//         {
//             cached = true,
//             ttl = "5 minutes (L1), 1 hour (L2)",
//             source = "In-memory cache (fast)"
//         }
//     });
// })
// .WithName("GetCompanyDetails")
// .WithOpenApi();
//
// // Example 4: Access without service wrapper
// app.MapGet("/api/companies/{companyId}/raw", (ITenantContextAccessor accessor) =>
// {
//     var companyId = accessor.GetTenantId();
//     var company = accessor.GetTenantInfo<CompanyInfo>();
//
//     if (company == null)
//         return Results.NotFound();
//
//     return Results.Json(new { companyId, company });
// })
// .WithName("GetCompanyRaw")
// .WithOpenApi();
//
// // Example 5: List all available companies (for testing)
// app.MapGet("/api/companies/list/all", () =>
// {
//     return Results.Json(new
//     {
//         companies = new[] { "acme", "globex", "initech" },
//         message = "Test these in URLs like /api/companies/acme, /api/companies/globex, etc."
//     });
// })
// .WithName("ListCompanies")
// .WithOpenApi();
//
// // Example 6: Refresh cache for a company
// app.MapPost("/api/companies/{companyId}/cache/refresh", async (
//     string companyId,
//     IMultiTierCache cache) =>
// {
//     await cache.RemoveAllTenantAsync(companyId);
//     return Results.Json(new
//     {
//         message = $"Cache refreshed for company {companyId}",
//         willRefetchFrom = "database on next request"
//     });
// })
// .WithName("RefreshCompanyCache")
// .WithOpenApi();
//
// // Example 7: Health check endpoint
// app.MapGet("/health", () =>
// {
//     return Results.Json(new { status = "healthy", timestamp = DateTime.UtcNow });
// })
// .WithName("HealthCheck")
// .WithOpenApi();
//
// // Enable Swagger/OpenAPI documentation
// app.UseSwagger();
// app.UseSwaggerUI();
//
// // ============================================
// // RUN
// // ============================================
//
// app.Run();
//
// /*
//  * GETTING STARTED:
//  * 
//  * 1. Create a new .NET 8 project:
//  *    dotnet new webapi -n MultiTierCacheDemo
//  * 
//  * 2. Add NuGet packages:
//  *    dotnet add package StackExchange.Redis
//  * 
//  * 3. Copy MultiTierCache.cs to your project
//  * 
//  * 4. Update appsettings.json:
//  *    {
//  *      "Redis": {
//  *        "ConnectionString": "localhost:6379"
//  *      }
//  *    }
//  * 
//  * 5. Start Redis:
//  *    docker run -d -p 6379:6379 redis:7-alpine
//  * 
//  * 6. Run the project:
//  *    dotnet run
//  * 
//  * 7. Test endpoints:
//  *    curl http://localhost:5000/api/companies/acme
//  *    curl http://localhost:5000/api/companies/globex/summary
//  *    curl http://localhost:5000/api/companies/initech/details
//  *    curl http://localhost:5000/api/companies/list/all
//  * 
//  * 8. View Swagger UI:
//  *    http://localhost:5000/swagger/index.html
//  * 
//  * PERFORMANCE TEST:
//  * 
//  * First request (database):
//  *    curl -w "\nTime: %{time_total}s\n" http://localhost:5000/api/companies/acme
//  *    → ~110ms (100ms simulation + network)
//  * 
//  * Subsequent requests (L1 cache):
//  *    curl -w "\nTime: %{time_total}s\n" http://localhost:5000/api/companies/acme
//  *    → ~2-5ms (memory lookup)
//  * 
//  * Different company (L1 miss, L2 miss, database):
//  *    curl -w "\nTime: %{time_total}s\n" http://localhost:5000/api/companies/globex
//  *    → ~110ms
//  * 
//  * Same company after Redis timeout (L1 miss, L2 hit):
//  *    Wait 5+ minutes, then:
//  *    curl -w "\nTime: %{time_total}s\n" http://localhost:5000/api/companies/acme
//  *    → ~10-20ms (Redis call is much faster than database)
//  * 
//  * CACHE FLOW DIAGRAM:
//  * 
//  * Request 1: GET /api/companies/acme
//  *   ✗ L1 miss
//  *   ✗ L2 miss
//  *   → Database (100ms)
//  *   → Store in L1 (5 min TTL)
//  *   → Store in L2 (1 hour TTL)
//  *   Total: 110ms
//  * 
//  * Request 2-N within 5 min: GET /api/companies/acme
//  *   ✓ L1 hit
//  *   Total: 2-5ms (25x faster!)
//  * 
//  * Request N+1 after 5 min: GET /api/companies/acme
//  *   ✗ L1 miss (TTL expired)
//  *   ✓ L2 hit (still valid, 1 hour TTL)
//  *   → Populate L1 from L2
//  *   Total: 10-20ms (5x faster than database)
//  * 
//  * TENANT ISOLATION EXAMPLE:
//  * 
//  * Request 1: GET /api/companies/acme → Extracts tenant='acme'
//  * Request 2: GET /api/companies/globex → Extracts tenant='globex'
//  * Request 3: GET /api/companies/acme → Uses ACME's cache (not GLOBEX)
//  * 
//  * Cache keys:
//  *   ACME:   tenant:acme:tenant-info:CompanyInfo
//  *   GLOBEX: tenant:globex:tenant-info:CompanyInfo
//  * 
//  * ✓ No cross-tenant data leakage
//  * ✓ Each tenant has independent cache
//  * ✓ Same URL pattern works for unlimited tenants
//  */
