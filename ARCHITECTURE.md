# Tenant Context & Cache Resolution Pattern

## Overview

The enhanced library provides a **zero-lookup tenant context** pattern where:

1. **Middleware** extracts the tenant ID from the request URL
2. **Middleware** fetches tenant data from cache (or database on miss)
3. **Tenant data is stored in HttpContext** for use throughout the request
4. **Developers inject `ITenantContextService`** to access tenant data without additional calls

This eliminates repeated database queries and provides a clean, type-safe API for accessing tenant context.

---

## Architecture Flow

```
HTTP Request
    ↓
TenantResolutionMiddleware
    ├─ Extract tenantId from URL via regex
    ├─ Call ITenantInfoProvider.GetTenantInfoAsync(tenantId)
    │   ├─ Check L1 cache (in-memory, 5 min TTL)
    │   ├─ Check L2 cache (Redis, 1 hour TTL)
    │   └─ Call database fetch lambda if both miss
    ├─ Store tenant info in HttpContext.Items
    └─ Continue to endpoint
    ↓
Controller/Endpoint Handler
    ├─ Inject ITenantContextService
    ├─ Access TenantInfo from context (no DB call!)
    └─ Return response

Database is called ONCE per unique tenant per L1 TTL expiry
Not ONCE per endpoint!
```

---

## Configuration

### Step 1: Define Your Tenant Models

```csharp
public class TenantInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Region { get; set; }
    public string Plan { get; set; }
}

public class TenantSettings
{
    public string TenantId { get; set; }
    public int MaxUsers { get; set; }
    public bool EnableFeatureX { get; set; }
}
```

### Step 2: Implement Database Layer

```csharp
public interface ITenantDatabase
{
    Task<TenantInfo> GetTenantAsync(string tenantId);
    Task<TenantSettings> GetTenantSettingsAsync(string tenantId);
}

public class TenantDatabase : ITenantDatabase
{
    private readonly IDbConnection _db;

    public async Task<TenantInfo> GetTenantAsync(string tenantId)
    {
        return await _db.QuerySingleAsync<TenantInfo>(
            "SELECT * FROM Tenants WHERE Id = @Id",
            new { Id = tenantId });
    }

    public async Task<TenantSettings> GetTenantSettingsAsync(string tenantId)
    {
        return await _db.QuerySingleAsync<TenantSettings>(
            "SELECT * FROM TenantSettings WHERE TenantId = @Id",
            new { Id = tenantId });
    }
}
```

### Step 3: Configure Middleware in Program.cs

```csharp
var builder = WebApplicationBuilder.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantDatabase, TenantDatabase>();

// Configure cache with tenant data fetch lambda
builder.Services.AddTenantContextCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))
        .WithCustomL2(_ => new MyDistributedCache("localhost:6379")); // any IDistributedCache (Redis, etc.)
});

var app = builder.Build();

// Register middleware with tenant data fetch
app.UseTenantContextCache(
    @"/api/tenants/(?<tenant>[^/]+)",
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        return await db.GetTenantAsync(tenantId);
    }
);

// Or with multiple data sources:
app.UseTenantContextCacheWithResolvers(
    @"/api/tenants/(?<tenant>[^/]+)",
    // Primary: TenantInfo
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        return await db.GetTenantAsync(tenantId);
    },
    // Secondary: TenantSettings
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        var settings = await db.GetTenantSettingsAsync(tenantId);
        return ("TenantInfo:TenantSettings", (object)settings);
    }
);

app.Run();
```

### Step 4: Use in Endpoints

```csharp
// Option A: Inject ITenantContextAccessor directly
app.MapGet("/api/tenants/{tenantId}/info", (ITenantContextAccessor ctx) =>
{
    var tenantId = ctx.GetTenantId();
    var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
    return Results.Ok(tenantInfo);
});

// Option B: Create a service wrapper (recommended)
public class TenantContextService
{
    private readonly ITenantContextAccessor _ctx;

    public string TenantId => _ctx.GetTenantId();
    public TenantInfo TenantInfo => _ctx.GetTenantInfo<TenantInfo>();
    public TenantSettings Settings => _ctx.GetTenantInfo<TenantSettings>();
}

app.MapGet("/api/tenants/{tenantId}/dashboard", (TenantContextService tenant) =>
{
    return Results.Ok(new
    {
        id = tenant.TenantId,
        name = tenant.TenantInfo.Name,
        region = tenant.TenantInfo.Region,
        maxUsers = tenant.Settings.MaxUsers
    });
});
```

---

## Request Lifecycle Example

### Request 1: First call with tenantId=acme

```
GET /api/tenants/acme/info

Middleware:
  1. Extract tenantId = "acme" from URL
  2. Call TenantInfoProvider.GetTenantInfoAsync("acme")
     - L1 cache miss
     - L2 cache miss
     - Call database lambda → SELECT * FROM Tenants WHERE Id='acme'
     - Store in L1 (5 min TTL)
     - Store in L2 (1 hour TTL)
  3. Store TenantInfo in HttpContext.Items["TenantInfo:TenantInfo"]

Endpoint:
  1. Inject TenantContextService
  2. Call ctx.TenantInfo
  3. Returns cached object (no additional calls)

Response time: ~50ms (database call)
```

### Request 2: Same tenant 30 seconds later

```
GET /api/tenants/acme/info

Middleware:
  1. Extract tenantId = "acme" from URL
  2. Call TenantInfoProvider.GetTenantInfoAsync("acme")
     - L1 cache HIT (in-memory)
     - Return cached object immediately
  3. Store in HttpContext.Items

Endpoint:
  1. Inject TenantContextService
  2. Call ctx.TenantInfo
  3. Returns cached object

Response time: ~1-2ms (no network/database call!)
Speedup: 25-50x faster
```

### Request 3: Different tenant, globex

```
GET /api/tenants/globex/info

Middleware:
  1. Extract tenantId = "globex" from URL
  2. Call TenantInfoProvider.GetTenantInfoAsync("globex")
     - L1 cache miss (different tenant)
     - L2 cache miss (first time)
     - Call database → SELECT * FROM Tenants WHERE Id='globex'
     - Store in L1 and L2
  3. Store in HttpContext.Items

Endpoint:
  1. Inject TenantContextService (different context per request!)
  2. Call ctx.TenantInfo
  3. Returns globex data

Response time: ~50ms
Tenant isolation: Guaranteed by key scoping
```

### Request 4: acme tenant, 1 hour later

```
GET /api/tenants/acme/info

Middleware:
  1. Extract tenantId = "acme"
  2. Call TenantInfoProvider.GetTenantInfoAsync("acme")
     - L1 cache miss (TTL expired)
     - L2 cache HIT (1 hour TTL, still valid)
     - Return cached object from Redis
     - Populate L1 for next 5 minutes
  3. Store in HttpContext.Items

Response time: ~5-10ms (Redis network call, faster than database)
```

---

## Common Patterns

### Pattern 1: Access Tenant in Multiple Endpoints

```csharp
// Create a service to access tenant context
public interface ITenantContext
{
    string TenantId { get; }
    TenantInfo Info { get; }
}

public class TenantContext : ITenantContext
{
    private readonly ITenantContextAccessor _accessor;

    public TenantContext(ITenantContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public string TenantId => _accessor.GetTenantId();
    public TenantInfo Info => _accessor.GetTenantInfo<TenantInfo>();
}

// Register in DI
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Use in endpoints
app.MapGet("/api/tenants/{tenantId}/users", (ITenantContext tenant) =>
{
    // tenant.TenantId and tenant.Info available
    return Results.Ok(new { tenantId = tenant.TenantId });
});

app.MapGet("/api/tenants/{tenantId}/settings", (ITenantContext tenant) =>
{
    // Same tenant context, no additional lookup
    return Results.Ok(new { tenantId = tenant.TenantId });
});
```

### Pattern 2: Conditional Logic Based on Tenant Plan

```csharp
app.MapPost("/api/tenants/{tenantId}/export", async (ITenantContext tenant) =>
{
    // Check tenant plan without additional database call
    if (tenant.Info.Plan != "Enterprise")
        return Results.BadRequest("Export requires Enterprise plan");

    // Proceed with export
    return Results.Ok();
});
```

### Pattern 3: Enriching Responses with Tenant Data

```csharp
app.MapGet("/api/tenants/{tenantId}/users", async (
    ITenantContext tenant,
    IUserService userService) =>
{
    var users = await userService.GetAllAsync(tenant.TenantId);

    return Results.Ok(new
    {
        tenant = new
        {
            id = tenant.TenantId,
            name = tenant.Info.Name,
            region = tenant.Info.Region
        },
        users = users,
        totalAllowed = tenant.Info.Plan == "Enterprise" ? int.MaxValue : 100
    });
});
```

### Pattern 4: Logging with Tenant Context

```csharp
public class RequestLoggingMiddleware
{
    public async Task InvokeAsync(HttpContext context, ILogger<RequestLoggingMiddleware> logger)
    {
        var tenantId = context.Items["TenantId"] as string;
        using (logger.BeginScope(new { TenantId = tenantId }))
        {
            // All logs from this request include TenantId
            logger.LogInformation("Processing request for tenant {TenantId}", tenantId);
            // ...
        }
    }
}
```

### Pattern 5: Caching Additional Tenant Data

```csharp
// Load settings in middleware if needed
app.UseTenantContextCacheWithResolvers(
    @"/api/tenants/(?<tenant>[^/]+)",
    // Primary fetch
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        return await db.GetTenantAsync(tenantId);
    },
    // Additional resolvers for related data
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        var settings = await db.GetTenantSettingsAsync(tenantId);
        return ("TenantInfo:TenantSettings", (object)settings);
    },
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        var features = await db.GetTenantFeaturesAsync(tenantId);
        return ("TenantInfo:Features", (object)features);
    }
);

// Access in endpoints
app.MapGet("/api/info", (ITenantContextAccessor ctx) =>
{
    var info = ctx.GetTenantInfo<TenantInfo>();
    var settings = ctx.GetTenantInfo<TenantSettings>();
    var features = ctx.GetTenantInfo<Features>();

    return Results.Ok(new { info, settings, features });
});
```

---

## Cache Invalidation

### Explicit Invalidation

```csharp
app.MapPost("/api/tenants/{tenantId}/settings", async (
    string tenantId,
    UpdateSettingsRequest request,
    ITenantDatabase db,
    ITenantContextCache cache) =>
{
    // Update in database
    await db.UpdateTenantSettingsAsync(tenantId, request);

    // Invalidate cache
    await cache.RemoveAsync(tenantId, "tenant-info:TenantInfo");
    await cache.RemoveAsync(tenantId, "tenant-info:TenantSettings");

    return Results.Ok();
});
```

### Bulk Invalidation

```csharp
app.MapPost("/api/tenants/{tenantId}/cache/purge", async (
    string tenantId,
    ITenantContextCache cache) =>
{
    // Clear ALL cache for this tenant
    await cache.RemoveAllTenantAsync(tenantId);
    return Results.Ok(new { message = "Cache cleared" });
});
```

---

## Performance Comparison

### Without Tenant Context Caching

```
Request to /api/tenants/acme/users
├─ Get tenant ID from URL
├─ Call GET /tenants/acme (database)
├─ Call GET /tenants/acme/settings (database)
├─ Call GET /tenants/acme/users (database)
└─ Response: ~150ms

5 concurrent requests × 3 database calls = 15 database queries
```

### With Tenant Context Caching

```
Request to /api/tenants/acme/users
├─ Middleware resolves TenantInfo from cache (L1 hit)
├─ Call GET /tenants/acme/users (database)
└─ Response: ~50ms

5 concurrent requests × 1 database call = 1 database query
(tenant info cached for 5 minutes)

Improvement:
- 3x faster response times
- 15x fewer database queries
- Reduced database load
```

---

## Troubleshooting

### Tenant Info is null in endpoint

**Cause**: Middleware didn't extract tenant or failed to fetch from database

**Solution**:
```csharp
// Check regex pattern matches your URL
app.UseTenantContextCache(@"/api/tenants/(?<tenant>[^/]+)");

// Test with exact URL:
// GET /api/tenants/acme/users → Should extract "acme"

// Enable logging to see middleware behavior
builder.Services.AddLogging(c => c.AddConsole());
```

### Cache hit rate is low

**Cause**: L1 TTL is too short or endpoints are used infrequently

**Solution**:
```csharp
// Increase L1 TTL
cache.WithL1TimeToLive(TimeSpan.FromMinutes(15));

// Implement cache warming
public class CacheWarmingService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tenantIds = new[] { "acme", "globex", "initech" };
        foreach (var id in tenantIds)
        {
            // Pre-populate cache on startup
        }
    }
}
```

### Different tenant data mixed up

**Cause**: Cache keys not properly scoped by tenant

**Solution**:
- Library automatically scopes keys as `tenant:{tenantId}:{key}`
- Verify each request has correct tenant ID in context
- Check tenant ID extraction regex is correct

---

## Best Practices

1. **Keep L1 TTL reasonable** (5-30 minutes)
   - Too short: Frequent database calls
   - Too long: Stale data in memory

2. **Use L2 for cross-instance sharing** (1-8 hours)
   - Different servers see same cached data
   - Handles instance restarts

3. **Implement cache invalidation hooks**
   - Clear cache when tenant data changes
   - Use event bus or webhooks for updates

4. **Monitor cache hit rates**
   - Aim for >80% L1 hit rate
   - Adjust TTLs if too low

5. **Test tenant isolation**
   - Verify tenant A can't access tenant B's cache
   - Use integration tests with multiple tenants

6. **Handle cache failures gracefully**
   - If L2 (Redis) is down, L1 still works
   - If both fail, fall back to database

7. **Profile database queries**
   - Measure before/after cache implementation
   - Verify expected reduction in queries
