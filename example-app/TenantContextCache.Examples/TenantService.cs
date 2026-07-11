namespace TenantContextCache.Examples;

public interface ITenantService
{
    Task<TenantInfo> GetTenantByIdAsync(string tenantId);
    Task<TenantInfo> GetTenantBySlugAsync(string slug);
}

public class TenantService : ITenantService
{
    public async Task<TenantInfo> GetTenantByIdAsync(string tenantId)
    {
        await Task.Delay(50); // Simulate DB
        
        // In production, query your database
        return tenantId switch
        {
            "1" => new TenantInfo { Id = "1", Name = "ACME Corp", Slug = "acme", CreatedAt = DateTime.UtcNow },
            "2" => new TenantInfo { Id = "2", Name = "Globex", Slug = "globex", CreatedAt = DateTime.UtcNow },
            _ => null
        };
    }

    public async Task<TenantInfo> GetTenantBySlugAsync(string slug)
    {
        await Task.Delay(50); // Simulate DB
        
        return slug.ToLower() switch
        {
            "acme" => new TenantInfo { Id = "1", Name = "ACME Corp", Slug = "acme", CreatedAt = DateTime.UtcNow },
            "globex" => new TenantInfo { Id = "2", Name = "Globex", Slug = "globex", CreatedAt = DateTime.UtcNow },
            _ => null
        };
    }
}