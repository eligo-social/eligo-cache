# 🎓 .NET Core Examples Guide

## 📌 Available Examples

You have **4 complete .NET Core examples** showing different usage patterns:

---

## 1️⃣ **MINIMAL_EXAMPLE.cs** (Simplest)

**Use this if:** You want the absolute simplest working example

**What it shows:**
- Basic setup in Program.cs
- Single tenant route pattern
- One endpoint accessing tenant context
- Minimal dependencies

**Copy & Paste Ready:**
```csharp
// Just copy the entire file into your Program.cs
// It's a complete, working ASP.NET Core app!
```

---

## 2️⃣ **MULTIPLE_PATTERNS_EXAMPLE.cs** (Advanced)

**Use this if:** You want to support multiple URL patterns

**What it shows:**
- Multiple tenant resolution patterns:
  - Numeric ID (`/tenants/123`)
  - String slug (`/Tenants/acme`)
  - Header-based (`X-Tenant-Id`)
- Pattern cascade logic
- Normalized tenant IDs
- Multiple endpoints

**Key features:**
- Support different URL formats
- Fallback from header if URL pattern fails
- Automatic tenant data injection

---

## 3️⃣ **Program.cs.example** (Basic Setup)

**Use this if:** You want a clean Program.cs template

**What it shows:**
- Service registration
- Middleware setup
- Single regex pattern
- Basic endpoint
- Configuration from appsettings

**Quick start:**
```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddMultiTierCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))
        .WithRedisL2("localhost:6379");
});

app.UseMultiTierCache(@"/api/tenants/(?<tenant>[^/]+)",
    async (tenantId) => await db.GetTenantAsync(tenantId));
```

---

## 4️⃣ **Program.cs.enhanced.example** (Production Ready)

**Use this if:** You want a full-featured production setup

**What it shows:**
- Multiple middleware registrations
- Multiple pattern resolvers
- Additional data resolvers
- Logging configuration
- Environment-specific setup
- Graceful error handling
- Health checks

**Production features:**
- Error handling middleware
- Logging to console & file
- Multiple tenant data types
- Cache invalidation endpoints
- Health check endpoints

---

## 🚀 **HOW TO USE THESE EXAMPLES**

### **Option 1: Copy the Whole Program.cs**

```bash
# Simplest approach
1. Open MINIMAL_EXAMPLE.cs
2. Copy entire content
3. Paste into your Program.cs
4. Customize for your database
5. Run: dotnet run
```

### **Option 2: Copy Relevant Sections**

```bash
1. Open Program.cs.example or Program.cs.enhanced.example
2. Copy the setup sections you need
3. Integrate into your existing Program.cs
4. Adjust dependencies as needed
```

### **Option 3: Use as Reference**

```bash
1. Keep examples open as reference
2. Build your Program.cs from scratch
3. Check examples when unsure about syntax
```

---

## 📖 **EXAMPLE PATTERNS**

### **Pattern 1: Regex (Simplest)**

```csharp
app.UseMultiTierCache(
    @"/api/tenants/(?<tenant>[^/]+)",
    async (tenantId) => await db.GetTenantAsync(tenantId)
);

// Matches: /api/tenants/acme, /api/tenants/123, etc.
```

### **Pattern 2: Route Parameters**

```csharp
app.UseMultiTierCacheWithPatterns(
    patterns =>
    {
        patterns.WithNumericTenantId("tenantId");
        patterns.WithTenantSlug("tenantSlug");
    },
    async (tenantId) => await db.GetTenantAsync(tenantId)
);

// Matches: /tenants/123 and /Tenants/acme
```

### **Pattern 3: Headers**

```csharp
app.UseMultiTierCacheWithPatterns(
    patterns =>
    {
        patterns.WithHeader("X-Tenant-Id");
    },
    async (tenantId) => await db.GetTenantAsync(tenantId)
);

// Matches header: X-Tenant-Id: acme
```

### **Pattern 4: Subdomains**

```csharp
app.UseMultiTierCacheWithPatterns(
    patterns =>
    {
        patterns.WithSubdomain();
    },
    async (tenantId) => await db.GetTenantAsync(tenantId)
);

// Matches: acme.example.com, client.example.com, etc.
```

---

## 💻 **ENDPOINT EXAMPLES**

### **Example 1: Get Tenant Info**

```csharp
app.MapGet("/api/tenants/{tenantId}/info", 
    (ITenantContextAccessor ctx) =>
    {
        var tenantId = ctx.GetTenantId();
        var info = ctx.GetTenantInfo<TenantInfo>();
        
        return Results.Json(new { id = tenantId, name = info.Name });
    });

// Request:  GET /api/tenants/acme/info
// Response: { "id": "acme", "name": "Acme Corp" }
```

### **Example 2: Get with Service Injection**

```csharp
app.MapGet("/dashboard", 
    (ITenantContextService ctx) =>
    {
        return Results.Json(new
        {
            tenantId = ctx.TenantId,
            tenantName = ctx.TenantInfo.Name,
            plan = ctx.TenantInfo.Plan
        });
    });
```

### **Example 3: Multiple Data Types**

```csharp
app.MapGet("/api/tenants/{tenantId}/full", 
    (ITenantContextAccessor ctx) =>
    {
        var tenantId = ctx.GetTenantId();
        var tenantInfo = ctx.GetTenantInfo<TenantInfo>();
        var settings = ctx.GetTenantInfo<TenantSettings>();
        
        return Results.Json(new
        {
            tenant = tenantInfo,
            settings = settings
        });
    });
```

### **Example 4: Cache Invalidation**

```csharp
app.MapPost("/api/tenants/{tenantId}/refresh",
    async (string tenantId, IMultiTierCache cache) =>
    {
        // Clear all tenant data from cache
        await cache.RemoveAllTenantAsync(tenantId);
        return Results.Ok(new { message = "Tenant cache cleared" });
    });
```

---

## 🗂️ **FILE ORGANIZATION**

After downloading examples, organize like this:

```
MyProject/
├── Program.cs                 ← Copy from one of the examples
├── Models/
│   ├── TenantInfo.cs         ← Tenant data model
│   └── TenantSettings.cs     ← Settings model
├── Database/
│   └── TenantDatabase.cs     ← DB access layer
└── MultiTierCache/
    └── MultiTierCache.cs     ← Library code
```

---

## 🔧 **CUSTOMIZATION CHECKLIST**

### **1. Database Model**

```csharp
public class TenantInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Plan { get; set; }
    public string Region { get; set; }
}
```

### **2. Database Access**

```csharp
public interface ITenantDatabase
{
    Task<TenantInfo> GetTenantAsync(string tenantId);
}

public class TenantDatabase : ITenantDatabase
{
    private readonly IDbConnection _connection;
    
    public async Task<TenantInfo> GetTenantAsync(string tenantId)
    {
        // Your database query here
        return await _connection.QuerySingleOrDefaultAsync<TenantInfo>(
            "SELECT * FROM Tenants WHERE Id = @id",
            new { id = tenantId }
        );
    }
}
```

### **3. Configure in Program.cs**

```csharp
// Register database
builder.Services.AddScoped<ITenantDatabase, TenantDatabase>();

// Configure cache
builder.Services.AddHttpContextAccessor();
builder.Services.AddMultiTierCache(cache =>
{
    cache
        .WithL1TimeToLive(TimeSpan.FromMinutes(5))
        .WithL2TimeToLive(TimeSpan.FromHours(1))
        .WithRedisL2(config["Redis:ConnectionString"]);
});

var app = builder.Build();

// Add middleware
app.UseMultiTierCache(
    @"/api/tenants/(?<tenant>[^/]+)",
    async (tenantId) =>
    {
        var db = app.Services.GetRequiredService<ITenantDatabase>();
        return await db.GetTenantAsync(tenantId);
    }
);
```

---

## 🧪 **TESTING THE EXAMPLES**

### **1. Run Locally**

```bash
# Restore NuGet packages
dotnet restore

# Build
dotnet build

# Run
dotnet run

# API should be at: https://localhost:7000
```

### **2. Test Endpoints**

```bash
# Get tenant info
curl https://localhost:7000/api/tenants/acme/info

# Expected response:
# { "id": "acme", "name": "Acme Corp" }
```

### **3. With Docker Compose**

```bash
# Start Redis and other services
docker-compose up -d

# Run your app
dotnet run

# Services available:
# API:              http://localhost:5000
# Redis:            localhost:6379
# Redis Commander:  http://localhost:8081
```

---

## 📊 **EXAMPLE FEATURES**

| Feature | Minimal | Multiple Patterns | Enhanced |
|---------|---------|-------------------|----------|
| Single pattern | ✅ | ❌ | ❌ |
| Multiple patterns | ❌ | ✅ | ✅ |
| Header support | ❌ | ✅ | ✅ |
| Subdomain support | ❌ | ✅ | ✅ |
| Multiple resolvers | ❌ | ✅ | ✅ |
| Error handling | Basic | Basic | Advanced |
| Logging | None | None | Full |
| Health checks | ❌ | ❌ | ✅ |
| Multiple data types | ❌ | ✅ | ✅ |

---

## 🎯 **WHICH EXAMPLE TO USE?**

### **Use MINIMAL_EXAMPLE if:**
- You want the simplest possible setup
- Single tenant URL pattern is enough
- Quick proof of concept
- Learning the basics

### **Use MULTIPLE_PATTERNS_EXAMPLE if:**
- You support multiple URL formats
- You need pattern fallback logic
- You want advanced features
- Supporting multiple clients

### **Use Program.cs.example if:**
- You have existing ASP.NET Core app
- You want clean, modular setup
- You prefer DI and services
- Production-ready

### **Use Program.cs.enhanced.example if:**
- You need production features
- You want logging and monitoring
- You need error handling
- You want health checks

---

## 🔌 **INTEGRATING WITH YOUR PROJECT**

### **Step 1: Copy MultiTierCache.cs**

```bash
cp MultiTierCache.cs src/YourProject/
```

### **Step 2: Choose an Example**

```bash
# Pick the example that matches your needs
cp MINIMAL_EXAMPLE.cs Program.cs
# OR
cp Program.cs.example Program.cs
# OR
cp Program.cs.enhanced.example Program.cs
```

### **Step 3: Customize for Your Database**

```csharp
// Update the fetch lambda
async (tenantId) =>
{
    var db = app.Services.GetRequiredService<ITenantDatabase>();
    return await db.GetTenantAsync(tenantId);  // Your DB call
}
```

### **Step 4: Update appsettings.json**

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  "Cache": {
    "L1Ttl": "00:05:00",
    "L2Ttl": "01:00:00"
  }
}
```

### **Step 5: Run**

```bash
dotnet run
```

---

## 🚀 **QUICK START (5 MINUTES)**

1. **Download** examples
2. **Copy** MINIMAL_EXAMPLE.cs to Program.cs
3. **Add** MultiTierCache.cs to project
4. **Create** TenantInfo model
5. **Create** TenantDatabase class
6. **Run** `dotnet run`
7. **Test** endpoints

---

## 📝 **EXAMPLE MODELS TO CREATE**

### **TenantInfo.cs**

```csharp
public class TenantInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Plan { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
}
```

### **TenantDatabase.cs**

```csharp
public interface ITenantDatabase
{
    Task<TenantInfo> GetTenantAsync(string tenantId);
}

public class TenantDatabase : ITenantDatabase
{
    private readonly IDbConnection _connection;
    
    public TenantDatabase(IConfiguration config)
    {
        _connection = new SqlConnection(config.GetConnectionString("Default"));
    }
    
    public async Task<TenantInfo> GetTenantAsync(string tenantId)
    {
        const string sql = "SELECT Id, Name, Plan, CreatedAt, IsActive FROM Tenants WHERE Id = @id";
        return await _connection.QuerySingleOrDefaultAsync<TenantInfo>(sql, new { id = tenantId });
    }
}
```

---

## ✅ **EXAMPLES CHECKLIST**

- [x] MINIMAL_EXAMPLE.cs — Simplest working example
- [x] MULTIPLE_PATTERNS_EXAMPLE.cs — Advanced patterns
- [x] Program.cs.example — Basic template
- [x] Program.cs.enhanced.example — Production setup

All examples are:
- ✅ Copy-paste ready
- ✅ Fully working
- ✅ Well-commented
- ✅ Production-ready
- ✅ Async/await throughout
- ✅ DI configured

---

## 🎓 **LEARNING PATH**

1. **Read** this guide (5 min)
2. **Study** MINIMAL_EXAMPLE.cs (10 min)
3. **Run** locally (5 min)
4. **Test** endpoints (5 min)
5. **Review** MULTIPLE_PATTERNS_EXAMPLE.cs (10 min)
6. **Integrate** into your project (15 min)

**Total: ~50 minutes to production!**

---

## 🎉 **YOU'RE READY!**

All examples are presented above. Download them and get started! 🚀
