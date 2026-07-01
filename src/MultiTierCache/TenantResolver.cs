using System.Text.RegularExpressions;

namespace MultiTierCache;

/// <summary>
    /// Tenant extraction from HTTP context
    /// </summary>
    public interface ITenantResolver
    {
        string ResolveTenant(HttpContext httpContext);
    }

    /// <summary>
    /// Composable tenant resolver - supports multiple patterns with fallback
    /// </summary>
    public interface IComposableTenantResolver : ITenantResolver
    {
        IComposableTenantResolver AddResolver(ITenantResolver resolver);
    }

    /// <summary>
    /// Implementation of composable resolver
    /// </summary>
    public class CompositeTenantResolver : IComposableTenantResolver
    {
        private readonly List<ITenantResolver> _resolvers = new();

        public string ResolveTenant(HttpContext httpContext)
        {
            foreach (var resolver in _resolvers)
            {
                var tenantId = resolver.ResolveTenant(httpContext);
                if (!string.IsNullOrEmpty(tenantId))
                    return tenantId;
            }
            return null;
        }

        public IComposableTenantResolver AddResolver(ITenantResolver resolver)
        {
            _resolvers.Add(resolver);
            return this;
        }
    }

    /// <summary>
    /// Default regex-based tenant resolver
    /// </summary>
    public class RegexTenantResolver : ITenantResolver
    {
        private readonly Regex _tenantRegex;

        public RegexTenantResolver(string tenantPattern)
        {
            _tenantRegex = new Regex(tenantPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        public string ResolveTenant(HttpContext httpContext)
        {
            var path = httpContext.Request.Path.Value ?? string.Empty;
            var match = _tenantRegex.Match(path);
            return match.Success ? match.Groups["tenant"].Value : null;
        }
    }

    /// <summary>
    /// Route parameter-based tenant resolver
    /// Extracts tenant from ASP.NET route values
    /// </summary>
    public class RouteParameterTenantResolver : ITenantResolver
    {
        private readonly string _parameterName;
        private readonly string _routePattern;

        public RouteParameterTenantResolver(string parameterName = "tenantId", string routePattern = null)
        {
            _parameterName = parameterName;
            _routePattern = routePattern;
        }

        public string ResolveTenant(HttpContext httpContext)
        {
            // Try to get from route values
            if (httpContext.GetRouteData().Values.TryGetValue(_parameterName, out var value))
            {
                return value?.ToString();
            }
            return null;
        }
    }

    /// <summary>
    /// Header-based tenant resolver
    /// </summary>
    public class HeaderTenantResolver : ITenantResolver
    {
        private readonly string _headerName;

        public HeaderTenantResolver(string headerName = "X-Tenant-Id")
        {
            _headerName = headerName;
        }

        public string ResolveTenant(HttpContext httpContext)
        {
            if (httpContext.Request.Headers.TryGetValue(_headerName, out var tenantId))
            {
                return tenantId.ToString();
            }
            return null;
        }
    }

    /// <summary>
    /// Subdomain-based tenant resolver
    /// </summary>
    public class SubdomainTenantResolver : ITenantResolver
    {
        private readonly string _domain;

        public SubdomainTenantResolver(string domain)
        {
            _domain = domain;
        }

        public string ResolveTenant(HttpContext httpContext)
        {
            var host = httpContext.Request.Host.Host;
            var parts = host.Split('.');
            
            if (parts.Length < 2)
                return null;

            var subdomain = parts[0];
            
            // Filter out common subdomains
            if (subdomain == "www" || subdomain == "api" || subdomain == "admin")
                return null;

            return subdomain;
        }
    }

    /// <summary>
    /// Multi-pattern route resolver - handles /tenants/{tenantId}/** and /Tenants/{tenantSlug}/**
    /// </summary>
    public class MultiPatternRouteResolver : IComposableTenantResolver
    {
        private readonly List<ITenantResolver> _resolvers = new();

        public MultiPatternRouteResolver()
        {
        }

        /// <summary>
        /// Add numeric tenant ID pattern: /tenants/{tenantId}/**
        /// </summary>
        public MultiPatternRouteResolver WithNumericTenantId(string parameterName = "tenantId")
        {
            _resolvers.Add(new RouteParameterTenantResolver(parameterName));
            return this;
        }

        /// <summary>
        /// Add string tenant slug pattern: /Tenants/{tenantSlug}/**
        /// </summary>
        public MultiPatternRouteResolver WithTenantSlug(string parameterName = "tenantSlug")
        {
            _resolvers.Add(new RouteParameterTenantResolver(parameterName));
            return this;
        }

        /// <summary>
        /// Add regex pattern: /api/tenants/{tenant}/...
        /// </summary>
        public MultiPatternRouteResolver WithRegexPattern(string pattern)
        {
            _resolvers.Add(new RegexTenantResolver(pattern));
            return this;
        }

        /// <summary>
        /// Add header-based resolution
        /// </summary>
        public MultiPatternRouteResolver WithHeader(string headerName = "X-Tenant-Id")
        {
            _resolvers.Add(new HeaderTenantResolver(headerName));
            return this;
        }

        /// <summary>
        /// Add subdomain-based resolution
        /// </summary>
        public MultiPatternRouteResolver WithSubdomain(string domain = "")
        {
            _resolvers.Add(new SubdomainTenantResolver(domain));
            return this;
        }

        /// <summary>
        /// Add custom resolver
        /// </summary>
        public MultiPatternRouteResolver WithCustomResolver(ITenantResolver resolver)
        {
            _resolvers.Add(resolver);
            return this;
        }

        public string ResolveTenant(HttpContext httpContext)
        {
            foreach (var resolver in _resolvers)
            {
                var tenantId = resolver.ResolveTenant(httpContext);
                if (!string.IsNullOrEmpty(tenantId))
                    return tenantId;
            }
            return null;
        }

        public IComposableTenantResolver AddResolver(ITenantResolver resolver)
        {
            _resolvers.Add(resolver);
            return this;
        }
    }