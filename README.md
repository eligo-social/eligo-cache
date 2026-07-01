# MultiTierCache

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/MultiTierCache.svg?color=blue)](https://www.nuget.org/packages/MultiTierCache)
[![NuGet Downloads](https://img.shields.io/nuget/dt/MultiTierCache.svg?color=blue)](https://www.nuget.org/packages/MultiTierCache)
[![.NET 8.0+](https://img.shields.io/badge/.NET-8.0+-512bd4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)

A production-ready **multi-tiered caching library** for ASP.NET Core with automatic tenant context injection, supporting multiple URL patterns and distributed cache backends.

## ✨ Features

- **Two-Tier Caching**
  - L1: Fast in-memory cache (5 sec - 30 min TTL)
  - L2: Distributed cache via Redis or Hazelcast (1 hour - 1 day TTL)
  - Automatic L1→L2 fallback on miss

- **Tenant Resolution**
  - Route parameters (numeric IDs, string slugs)
  - HTTP headers
  - Subdomains
  - Custom patterns with cascade logic

- **Automatic Context Injection**
  - Tenant data resolved once per request
  - Available throughout request lifecycle
  - Zero additional database calls
  - Type-safe dependency injection

## 🚀 Quick Start

### 1. Install NuGet Package

```bash
dotnet add package MultiTierCache
```

### 2. Configure in Program.cs

```csharp
using MultiTierCache.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

// Configure cache
builder.Services.AddMultiTierCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))
        .WithRedisL2("localhost:6379");
});

var app = builder.Build();

// Add middleware with tenant data fetch
app.UseMultiTierCache(
    @"/api/tenants/(?<tenant>[^/]+)",
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantService>();
        return await db.GetTenantByIt(tenantId);
    }
);

app.MapGet("/api/tenants/{tenantId}/info", (ITenantContextAccessor ctx) =>
{
    var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
    return Results.Json(tenantInfo);
});

app.Run();
```

### 3. Use in Endpoints

```csharp
app.MapGet("/api/tenants/{tenantId}/users", (ITenantContextService context) =>
{
    // Tenant data pre-loaded and available (no DB call!)
    return Results.Json(new
    {
        tenantId = context.TenantId,
        tenantName = context.TenantInfo.Name,
        region = context.TenantInfo.Region
    });
});
```

## 🔧 Advanced Usage

### Multiple Tenant Resolution Patterns

Support both `/tenants/123` (numeric) and `/Tenants/acme` (slug):

```csharp
app.UseMultiTierCacheWithPatterns(
    patterns =>
    {
        patterns
            .WithNumericTenantId("tenantId")      // /tenants/{tenantId}/**
            .WithTenantSlug("tenantSlug")         // /Tenants/{tenantSlug}/**
            .WithHeader("X-Tenant-Id")            // Fallback to header
            .WithSubdomain();                      // SaaS multi-tenant
    },
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        
        if (int.TryParse(tenantId, out _))
            return await db.GetTenantByIdAsync(tenantId);
        
        return await db.GetTenantBySlugAsync(tenantId);
    }
);
```

### Custom Tenant Resolver

```csharp
public class CustomTenantResolver : ITenantResolver
{
    public string ResolveTenant(HttpContext httpContext)
    {
        // Your custom logic here
        return tenantId;
    }
}

app.UseMultiTierCacheWithResolver(
    new CustomTenantResolver(),
    async (tenantId) => await db.GetTenantAsync(tenantId)
);
```

### Cache Invalidation

```csharp
app.MapPost("/api/tenants/{tenantId}/settings", async (
    string tenantId,
    UpdateRequest request,
    IMultiTierCache cache,
    ITenantDatabase db) =>
{
    // Update database
    await db.UpdateTenantAsync(tenantId, request);
    
    // Invalidate cache
    await cache.RemoveAsync(tenantId, "tenant-info:TenantInfo");
    await cache.RemoveAsync(tenantId, "tenant-settings");
    
    return Results.Ok();
});
```

## 📊 Performance

### Benchmark Results

```
Scenario                  Time    Improvement
─────────────────────────────────────────────
Database only            50ms    baseline
L1 cache hit (memory)    1-2ms   25-50x faster
L2 cache hit (Redis)     5-10ms  5-10x faster
```

### Real-World Impact

For a typical API with 5 endpoints per tenant request:

```
Without cache:   5 DB calls × 50ms = 250ms
With cache:      1 DB call  × 50ms = 50ms (+ 1-2ms for L1 cache)
Improvement:     5x faster, 4 DB calls eliminated per request
```

## 🧪 Testing

### Run All Tests

```bash
dotnet test
```

### Test Coverage

```bash
# With coverage report
dotnet test  --collect:"XPlat Code Coverage"
```

## 📈 Roadmap

- [x] Core multi-tier caching
- [x] Tenant context injection
- [x] Multiple URL patterns
- [x] Tenant Context Resolution from Authentication Token 
- [x] Redis backend
- [x] Hazelcast backend
- [ ] OpenTelemetry metrics
- [ ] Cache preloading strategies
- [ ] GraphQL support
- [ ] gRPC support

## 🤝 Contributing

Contributions are welcome! See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

### Setup Development Environment

```bash
git clone https://github.com/eligo-social/eligo-cache.git
cd eligo-cache
dotnet restore
dotnet build
dotnet test
```

### Areas We Need Help With

- Additional cache backends (Memcached, RavenDB, Custom)
- Performance optimizations
- Documentation improvements
- Example applications
- Language bindings

## 📝 License

This project is licensed under the MIT License - see [LICENSE](./LICENSE) file for details.

## 🙋 Support

- **Documentation:** [/docs](./docs)
- **Issues:** [GitHub Issues](https://github.com/eligo-social/eligo-cache/issues)
- **Discussions:** [GitHub Discussions](https://github.com/eligo-social/eligo-cache/discussions)
- **Email:** support@example.com

## 📦 NuGet

```bash
dotnet add package MultiTierCache
```

Or via NuGet Package Manager:
```
Install-Package MultiTierCache
```

## 🎯 Use Cases

- **Multi-tenant SaaS** — Automatic tenant isolation with shared caching
- **Microservices** — Distributed cache across service instances
- **High-traffic APIs** — Reduce database load with intelligent caching
- **Global applications** — Region-specific tenant resolution
- **Legacy modernization** — Add caching without architecture changes


## ✅ Checklist Before Production

- [ ] Set L1/L2 TTLs based on data freshness requirements
- [ ] Configure Redis persistence
- [ ] Set up monitoring & alerting
- [ ] Load test with realistic tenant count
- [ ] Test cache invalidation flows
- [ ] Verify > 80% L1 cache hit rate
- [ ] Document tenant resolution logic
- [ ] Plan cache warming strategy

## 🙏 Acknowledgments

Built with ❤️ by Eligo eVoting inside the Lumi Lab Day Initiative

## 📞 Contact

- **GitHub:** [@save_veltri](https://github.com/save_veltri)
- **Twitter:** [@save_veltri](https://twitter.com/save_Veltri)
- **LinkedIn:** [saveveltri](https://linkedin.com/in/saveveltri)

---
