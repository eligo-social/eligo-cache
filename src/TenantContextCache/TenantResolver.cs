using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TenantContextCache;

    /// <summary>
    /// Opt-in marker: only endpoints annotated with this attribute participate in tenant
    /// resolution. The middleware reads the tenant from the route value named by
    /// <see cref="RouteParameter"/>, so there is no URL-shape guessing and no risk of an
    /// unrelated path (e.g. <c>/admin/tenants/list</c>) being mistaken for a tenant route.
    /// Apply it to a controller, an action, or a minimal-API endpoint's metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class TenantContextAttribute : Attribute
    {
        /// <summary>Route parameter that carries the tenant, e.g. "tenantId" for "/api/tenants/{tenantId}".</summary>
        public string RouteParameter { get; }

        public TenantContextAttribute(string routeParameter = "tenant")
        {
            if (string.IsNullOrWhiteSpace(routeParameter))
                throw new ArgumentException("Route parameter name must not be null or empty.", nameof(routeParameter));
            RouteParameter = routeParameter;
        }
    }

    /// <summary>
    /// Converts ASP.NET-style route templates such as "/api/tenants/{tenantId:int}"
    /// into a regular expression with a named "tenant" capture group, suitable for
    /// <see cref="RegexTenantResolver"/>.
    ///
    /// Supported inline constraints: int, long, guid, bool, alpha, decimal/double/float,
    /// and numeric range constraints (min/max/range). Catch-all parameters ({*rest})
    /// map to ".+". Unknown or unsupported constraints (including regex(...)) fall back
    /// to the default segment pattern "[^/]+".
    /// </summary>
    public static class RouteTemplateConverter
    {
        /// <summary>Name of the capture group produced for the tenant parameter.</summary>
        public const string TenantGroupName = "tenant";

        private static readonly Regex PlaceholderRegex =
            new(@"\{(?<name>[^{}:]+)(?::(?<constraint>[^{}]+))?\}", RegexOptions.Compiled);

        /// <summary>
        /// Translate a route template into a regex pattern. The tenant parameter is
        /// captured in a group named "tenant". When <paramref name="tenantParameterName"/>
        /// is supplied, the matching placeholder is captured; otherwise the first
        /// placeholder is used.
        /// </summary>
        public static string ToRegexPattern(string template, string tenantParameterName = null)
        {
            if (string.IsNullOrWhiteSpace(template))
                throw new ArgumentException("Route template must not be null or empty.", nameof(template));

            var placeholders = PlaceholderRegex.Matches(template);
            if (placeholders.Count == 0)
                throw new ArgumentException(
                    $"Route template '{template}' does not contain any '{{parameter}}' placeholders.",
                    nameof(template));

            var tenantIndex = ResolveTenantIndex(placeholders, tenantParameterName);

            var sb = new StringBuilder();
            var cursor = 0;
            for (var i = 0; i < placeholders.Count; i++)
            {
                var placeholder = placeholders[i];

                // Escape the literal text preceding this placeholder.
                sb.Append(Regex.Escape(template.Substring(cursor, placeholder.Index - cursor)));

                var name = placeholder.Groups["name"].Value;
                var isCatchAll = name.StartsWith("*");
                var constraint = placeholder.Groups["constraint"].Success
                    ? placeholder.Groups["constraint"].Value
                    : null;
                var body = PatternFor(constraint, isCatchAll);

                sb.Append(i == tenantIndex ? $"(?<{TenantGroupName}>{body})" : $"(?:{body})");

                cursor = placeholder.Index + placeholder.Length;
            }

            // Escape any trailing literal text.
            sb.Append(Regex.Escape(template.Substring(cursor)));
            return sb.ToString();
        }

        private static int ResolveTenantIndex(MatchCollection placeholders, string tenantParameterName)
        {
            if (string.IsNullOrEmpty(tenantParameterName))
                return 0;

            for (var i = 0; i < placeholders.Count; i++)
            {
                var rawName = placeholders[i].Groups["name"].Value.TrimStart('*');
                if (string.Equals(rawName, tenantParameterName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            throw new ArgumentException(
                $"Route template does not contain a parameter named '{tenantParameterName}'.",
                nameof(tenantParameterName));
        }

        private static string PatternFor(string constraint, bool isCatchAll)
        {
            if (isCatchAll)
                return ".+";

            // Constraints can be chained, e.g. "int:min(1)" - the first token drives the shape.
            var token = constraint?.Split(':')[0].Trim().ToLowerInvariant();

            // Strip any parenthesised arguments, e.g. "length(5)" -> "length".
            var paren = token?.IndexOf('(') ?? -1;
            if (paren >= 0)
                token = token.Substring(0, paren);

            return token switch
            {
                "int" or "long" or "min" or "max" or "range" => @"\d+",
                "guid" => @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                "bool" => @"(?:true|false)",
                "alpha" => @"[a-zA-Z]+",
                "decimal" or "double" or "float" => @"[-+]?[0-9]*\.?[0-9]+",
                _ => @"[^/]+"
            };
        }
    }

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
    /// Annotation-gated tenant resolver. Resolves a tenant only when the matched endpoint is
    /// annotated with <see cref="TenantContextAttribute"/>, and takes the value from the route
    /// parameter that attribute names. Endpoints that don't opt in resolve to <c>null</c>, so
    /// unrelated paths never acquire tenant context.
    /// <para>
    /// Requires the resolution middleware to run after <c>UseRouting()</c> (so an endpoint and
    /// its route values are available); before routing, <c>GetEndpoint()</c>
    /// is null and this resolver returns <c>null</c>.
    /// </para>
    /// </summary>
    public class EndpointTenantResolver : ITenantResolver
    {
        public string ResolveTenant(HttpContext httpContext)
        {
            var marker = httpContext.GetEndpoint()?.Metadata.GetMetadata<TenantContextAttribute>();
            if (marker == null)
                return null;

            return httpContext.Request.RouteValues.TryGetValue(marker.RouteParameter, out var value)
                ? value?.ToString()
                : null;
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