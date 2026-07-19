# TenantContextCache

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/TenantContextCache.svg?color=blue)](https://www.nuget.org/packages/TenantContextCache)
[![NuGet Downloads](https://img.shields.io/nuget/dt/TenantContextCache.svg?color=blue)](https://www.nuget.org/packages/TenantContextCache)
[![.NET 8.0+](https://img.shields.io/badge/.NET-8.0+-512bd4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)

A production-ready **multi-tiered caching library** for ASP.NET Core with automatic tenant context injection, supporting multiple URL patterns and distributed cache backends.

The two-tier caching engine is powered by [**FusionCache**](https://github.com/ZiggyCreatures/FusionCache); this library adds a tenant-aware API, tenant resolution middleware, and per-tenant bulk invalidation on top of it.

## ✨ Features

- **Two-Tier Caching (powered by FusionCache)**
  - L1: Fast in-memory cache (5 sec - 30 min TTL)
  - L2: Distributed cache via any `IDistributedCache` backend you supply — Redis, SQL Server, etc. (1 hour - 1 day TTL)
  - Automatic L1→L2 fallback on miss, cache stampede protection, and fail-safe

- **Tenant Resolution**
  - Opt-in per endpoint via a `[TenantContext]` annotation (recommended — no URL-shape guessing)
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

The package is published to **GitHub Packages** as `tenant-context-cache`:

```bash
dotnet add package tenant-context-cache
```

> This requires a one-time feed setup so NuGet can authenticate to GitHub Packages. See [📦 Installing from GitHub Packages](#-installing-from-github-packages) below.

### 2. Configure in Program.cs

```csharp
using TenantContextCache;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<ITenantService, TenantService>();

// Configure cache. Two things are required:
//   - WithTenantDataFetch<T>: the source of tenant data. On each request the middleware calls
//     it (cache-first) and injects the result into the request context. This is the library's
//     primary job, so it must be configured. The (sp, tenantId) overload hands the fetch the
//     request-scoped IServiceProvider, so it can resolve scoped services (a repository,
//     DbContext, …) safely; a plain tenantId => … overload exists for dependency-free fetches.
//   - WithCustomL2: the L2 distributed backend, as any IDistributedCache (Redis, SQL Server, …).
builder.Services.AddTenantContextCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))
        .WithTenantDataFetch<TenantInfo>((sp, tenantId) =>
            sp.GetRequiredService<ITenantService>().GetTenantByIdAsync(tenantId))
        .WithCustomL2(_ => new MyDistributedCache("localhost:6379")); // any IDistributedCache (Redis, etc.)
});

var app = builder.Build();

app.UseRouting();

// Tenant resolution is opt-in per endpoint: only endpoints annotated with
// [TenantContext("<routeParam>")] participate, and the tenant is read from that route value.
// Register it AFTER UseRouting() so the matched endpoint and its route values are available.
// The tenant data fetch was configured above.
app.UseTenantContextCache();

app.MapGet("/api/tenants/{tenantId}/info", (ITenantContextAccessor ctx) =>
{
    // Tenant data was already resolved, fetched (cache-first) and injected by the middleware.
    var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
    return Results.Json(tenantInfo);
})
.WithMetadata(new TenantContextAttribute("tenantId")); // opt in; tenant comes from {tenantId}

app.Run();
```

### 3. Use in Endpoints

Annotate every endpoint that needs tenant context with `[TenantContext("<routeParam>")]`,
naming the route parameter that holds the tenant. Endpoints without the annotation are left
untouched — this is what makes resolution opt-in and prevents an unrelated path (e.g.
`/admin/tenants/list`) from ever being mistaken for a tenant route.

```csharp
app.MapGet("/api/tenants/{tenantId}/users", (ITenantContextAccessor context) =>
{
    // Tenant data pre-loaded and available (no DB call!)
    var tenant = context.GetTenantInfo<TenantInfo>();
    return Results.Json(new
    {
        tenantId = context.GetTenantId(),
        tenantName = tenant.Name,
        region = tenant.Region
    });
})
.WithMetadata(new TenantContextAttribute("tenantId"));
```

On MVC/API controllers, apply the attribute to the action or the controller instead:

```csharp
[ApiController]
[Route("api/tenants/{tenantId}")]
[TenantContext("tenantId")]   // applies to every action in the controller
public class TenantController : ControllerBase { /* ... */ }
```

## 🔧 Advanced Usage

### Annotation-based Resolution (recommended)

The no-argument `app.UseTenantContextCache()` resolves the tenant **only for endpoints that
opt in** with `[TenantContext("<routeParam>")]`, reading the value straight from the named route
parameter. There is no URL-shape guessing, so an unrelated path can never be mistaken for a
tenant route.

```csharp
app.UseRouting();
app.UseTenantContextCache();   // must be AFTER UseRouting()
app.UseEndpoints(/* ... */);

app.MapGet("/api/tenants/{tenantId}/info", /* ... */)
   .WithMetadata(new TenantContextAttribute("tenantId"));
```

Two rules to keep in mind:

- **Order matters.** Register the middleware *after* `UseRouting()` (and before your endpoints).
  Before routing, the matched endpoint and its route values don't exist yet, so nothing resolves.
- **Opt-in only.** Endpoints without `[TenantContext]` never acquire tenant context, and the
  middleware runs its resolution only for requests that match an annotated endpoint (404s and
  static files are skipped).

The attribute's argument is the route-parameter name; it defaults to `"tenant"` when omitted
(`[TenantContext]` ≙ `[TenantContext("tenant")]`). On controllers, place it on the action or the
controller class to cover every action.

### URL-shape Matching (alternative)

> ⚠️ These overloads match by **URL shape** and can false-match any path containing the pattern
> (e.g. `/admin/tenants/list` would resolve `list` as a tenant). Prefer the annotation-based
> default above unless you specifically need path-shape matching.

### Route Template Syntax

`UseTenantContextCache(string)` takes a **regular expression** with a named
`tenant` capture group:

```csharp
app.UseTenantContextCache(@"/api/tenants/(?<tenant>[^/]+)");
```

If you prefer ASP.NET-style route templates, use `UseTenantContextCacheWithTemplate(...)`.
It translates the template into the equivalent regex for you, so
`{tenantId:int}` only matches numeric tenants:

```csharp
app.UseTenantContextCacheWithTemplate("/api/tenants/{tenantId:int}");
// equivalent to: @"/api/tenants/(?<tenant>\d+)"
```

Supported inline constraints:

| Template                         | Matches                     | Equivalent regex |
|----------------------------------|-----------------------------|------------------|
| `{tenantId}`                     | any single path segment     | `[^/]+`          |
| `{tenantId:int}` / `{id:long}`   | digits only                 | `\d+`            |
| `{id:guid}`                      | a GUID                      | GUID pattern     |
| `{id:alpha}`                     | letters only                | `[a-zA-Z]+`      |
| `{id:bool}`                      | `true` / `false`            | `(?:true\|false)`|
| `{id:decimal}` / `:double`/`:float` | a decimal number         | `[-+]?[0-9]*\.?[0-9]+` |
| `{*rest}`                        | catch-all (rest of path)    | `.+`             |

Notes:

- The **first** placeholder is captured as the tenant by default. With multiple
  placeholders, name the tenant parameter explicitly:
  ```csharp
  app.UseTenantContextCacheWithTemplate(
      "/api/{version}/tenants/{tenantId:int}",
      tenantParameterName: "tenantId");
  ```
- Chained constraints such as `{id:int:min(1)}` use the first token (`int`) for matching.
- Unknown/unsupported constraints (including `regex(...)`) fall back to `[^/]+`.
- The tenant data fetch is configured once via `WithTenantDataFetch<T>` at registration; the
  `Use...` overloads only pick how the tenant id is resolved.

### Multiple Tenant Resolution Patterns

Support both `/tenants/123` (numeric) and `/Tenants/acme` (slug):

```csharp
app.UseTenantContextCacheWithPatterns(patterns =>
{
    patterns
        .WithNumericTenantId("tenantId")      // /tenants/{tenantId}/**
        .WithTenantSlug("tenantSlug")         // /Tenants/{tenantSlug}/**
        .WithHeader("X-Tenant-Id")            // Fallback to header
        .WithSubdomain();                      // SaaS multi-tenant
});
```

The tenant data source is the `WithTenantDataFetch<T>` you configured at registration — it can
branch on the id shape itself, e.g. `int.TryParse(id, …) ? GetByIdAsync(id) : GetBySlugAsync(id)`.

`MultiPatternRouteResolver` tries each registered source **in order** and returns the
first non-empty tenant id (empty results are treated as "no match" and fall through).
Available sources:

| Builder method | Resolves from | Notes |
|----------------|---------------|-------|
| `WithRegexPattern(pattern)` | request path via regex | pattern needs a named `tenant` group |
| `WithNumericTenantId(name)` | route value `{name}` | reads matched route data |
| `WithTenantSlug(name)` | route value `{name}` | reads matched route data |
| `WithHeader(headerName)` | request header | defaults to `X-Tenant-Id` |
| `WithSubdomain()` | host subdomain | ignores `www`, `api`, `admin` |
| `WithCustomResolver(resolver)` | your `ITenantResolver` | any custom logic |

Example — path first, then header, then subdomain:

```csharp
app.UseTenantContextCacheWithPatterns(patterns =>
{
    patterns
        .WithRegexPattern(@"/api/tenants/(?<tenant>[^/]+)") // /api/tenants/acme/...
        .WithHeader("X-Tenant-Id")                          // fallback to header
        .WithSubdomain();                                   // fallback to acme.example.com
});
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

app.UseTenantContextCacheWithResolver(new CustomTenantResolver());
```

### L2 Cache Backend

The L2 (distributed) layer is always supplied by you as an `IDistributedCache`.
The library itself ships no distributed-cache dependency, so you bring the backend
that fits your stack. Because FusionCache uses `IDistributedCache` as its L2
abstraction, the only requirement is a standard `IDistributedCache` implementation —
many already exist (Redis, SQL Server, NCache, etc.) and you can also write your own.
The [example app](example-app/TenantContextCache.Examples) includes a small
Redis-backed `IDistributedCache` (`RedisDistributedCache`) built on StackExchange.Redis:

```csharp
public class MyCustomL2Cache : IDistributedCache
{
    public byte[] Get(string key) { /* ... */ }
    public Task<byte[]> GetAsync(string key, CancellationToken token = default) { /* ... */ }
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { /* ... */ }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { /* ... */ }
    public void Refresh(string key) { /* ... */ }
    public Task RefreshAsync(string key, CancellationToken token = default) { /* ... */ }
    public void Remove(string key) { /* ... */ }
    public Task RemoveAsync(string key, CancellationToken token = default) { /* ... */ }
}
```

Register it with one of the `WithCustomL2` overloads:

```csharp
builder.Services.AddTenantContextCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))

        // 1. Resolve from DI by type (the type is registered for you)
        .WithCustomL2<MyCustomL2Cache>();

        // 2. Or pass a ready-made instance
        // .WithCustomL2(new MyCustomL2Cache("connection-string"));

        // 3. Or use a factory with access to the service provider
        // .WithCustomL2(sp => new MyCustomL2Cache(sp.GetRequiredService<IFoo>()));
});
```

L1 always stays in-memory (FusionCache's memory layer); only the L2 layer is
replaced, and the automatic L1→L2 fallback and per-tenant invalidation continue
to work unchanged. FusionCache handles serialization and key management for the
distributed layer.

### Cache Invalidation

```csharp
app.MapPost("/api/tenants/{tenantId}/settings", async (
    string tenantId,
    UpdateRequest request,
    ITenantContextCache cache,
    ITenantDatabase db) =>
{
    // Update database
    await db.UpdateTenantAsync(tenantId, request);
    
    // Invalidate individual entries
    await cache.RemoveAsync(tenantId, "tenant-info:TenantInfo");
    await cache.RemoveAsync(tenantId, "tenant-settings");
    
    return Results.Ok();
});
```

To clear **everything** for a tenant in one call, use `RemoveAllTenantAsync`. Each
entry is written with a per-tenant tag, so this maps to a single FusionCache
`RemoveByTagAsync` — it evicts across both L1 and L2:

```csharp
await cache.RemoveAllTenantAsync(tenantId);
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
- [x] FusionCache-backed two-tier engine
- [x] Bring-your-own L2 backend (any `IDistributedCache`, e.g. Redis)
- [x] Per-tenant bulk invalidation via FusionCache tagging
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

## 📦 Installing from GitHub Packages

This library is published to [GitHub Packages](https://github.com/eligo-social/eligo-cache/packages) under the id **`tenant-context-cache`**. Because GitHub Packages feeds are authenticated, consumers need a one-time setup.

### 1. Create a Personal Access Token (PAT)

Create a **classic** PAT with the `read:packages` scope at
[github.com/settings/tokens](https://github.com/settings/tokens).

### 2. Add the GitHub Packages feed

Add a `nuget.config` next to your solution (do **not** commit the token — reference it via an environment variable):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github" value="https://nuget.pkg.github.com/eligo-social/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </github>
  </packageSourceCredentials>
</configuration>
```

Then export the token before restoring:

```bash
export GITHUB_PACKAGES_TOKEN=ghp_your_token_here
```

Alternatively, register the source from the CLI instead of a `nuget.config`:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/eligo-social/index.json" \
  --name github \
  --username YOUR_GITHUB_USERNAME \
  --password $GITHUB_PACKAGES_TOKEN \
  --store-password-in-clear-text
```

### 3. Install the package

```bash
dotnet add package tenant-context-cache
```

Or via the NuGet Package Manager console:

```
Install-Package tenant-context-cache
```

### CI/CD

In GitHub Actions inside the same organization, use the built-in `GITHUB_TOKEN` instead of a PAT:

```yaml
- run: dotnet nuget add source "https://nuget.pkg.github.com/eligo-social/index.json" \
    --name github --username eligo-social \
    --password ${{ secrets.GITHUB_TOKEN }} --store-password-in-clear-text
- run: dotnet restore
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
