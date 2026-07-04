using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Moq;
using NUnit.Framework;

namespace MultiTierCache.Tests;

[TestFixture]
public class MultiPatternRouteResolverTests
{
    /// <summary>Simple stub resolver that always returns a fixed value.</summary>
    private class StubResolver : ITenantResolver
    {
        private readonly string _value;
        public int CallCount { get; private set; }

        public StubResolver(string value) => _value = value;

        public string ResolveTenant(HttpContext httpContext)
        {
            CallCount++;
            return _value;
        }
    }

    private static HttpContext ContextWith(string path = "/", string host = "localhost",
        string headerName = null, string headerValue = null)
    {
        var context = new Mock<HttpContext>();
        var request = new Mock<HttpRequest>();

        request.Setup(r => r.Path).Returns(path);
        request.Setup(r => r.Host).Returns(new HostString(host));

        var headers = new Mock<IHeaderDictionary>();
        var values = headerValue != null ? new StringValues(headerValue) : StringValues.Empty;
        headers.Setup(h => h.TryGetValue(headerName ?? "X-Tenant-Id", out values))
            .Returns(headerValue != null);
        request.Setup(r => r.Headers).Returns(headers.Object);

        context.Setup(c => c.Request).Returns(request.Object);
        return context.Object;
    }

    [Test]
    public void ResolveTenant_ReturnsFirstNonEmptyMatch_InOrder()
    {
        var first = new StubResolver(null);
        var second = new StubResolver("acme");
        var third = new StubResolver("globex");
        var resolver = new MultiPatternRouteResolver()
            .WithCustomResolver(first)
            .WithCustomResolver(second)
            .WithCustomResolver(third);

        var result = resolver.ResolveTenant(ContextWith());

        result.Should().Be("acme");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1);
        third.CallCount.Should().Be(0); // short-circuits after the first match
    }

    [Test]
    public void ResolveTenant_ReturnsNull_WhenNoResolverMatches()
    {
        var resolver = new MultiPatternRouteResolver()
            .WithCustomResolver(new StubResolver(null))
            .WithCustomResolver(new StubResolver(""));

        resolver.ResolveTenant(ContextWith()).Should().BeNull();
    }

    [Test]
    public void ResolveTenant_ReturnsNull_WhenNoResolversRegistered()
    {
        new MultiPatternRouteResolver().ResolveTenant(ContextWith()).Should().BeNull();
    }

    [Test]
    public void ResolveTenant_TreatsEmptyStringAsNoMatch()
    {
        var resolver = new MultiPatternRouteResolver()
            .WithCustomResolver(new StubResolver(""))
            .WithCustomResolver(new StubResolver("fallback"));

        resolver.ResolveTenant(ContextWith()).Should().Be("fallback");
    }

    [Test]
    public void AddResolver_AppendsResolver_AndParticipatesInFallback()
    {
        var resolver = new MultiPatternRouteResolver()
            .WithCustomResolver(new StubResolver(null));
        resolver.AddResolver(new StubResolver("added"));

        resolver.ResolveTenant(ContextWith()).Should().Be("added");
    }

    [Test]
    public void AddResolver_ReturnsComposableResolver_ForChaining()
    {
        var resolver = new MultiPatternRouteResolver();

        var returned = resolver.AddResolver(new StubResolver("x"));

        returned.Should().BeAssignableTo<IComposableTenantResolver>();
    }

    [Test]
    public void BuilderMethods_ReturnSameInstance_ForFluentChaining()
    {
        var resolver = new MultiPatternRouteResolver();

        resolver.WithHeader().Should().BeSameAs(resolver);
        resolver.WithSubdomain().Should().BeSameAs(resolver);
        resolver.WithRegexPattern("(?<tenant>x)").Should().BeSameAs(resolver);
        resolver.WithNumericTenantId().Should().BeSameAs(resolver);
        resolver.WithTenantSlug().Should().BeSameAs(resolver);
    }

    [Test]
    public void WithRegexPattern_ResolvesTenantFromPath()
    {
        var resolver = new MultiPatternRouteResolver()
            .WithRegexPattern(@"/api/tenants/(?<tenant>[^/]+)");

        resolver.ResolveTenant(ContextWith(path: "/api/tenants/acme/users")).Should().Be("acme");
    }

    [Test]
    public void WithHeader_ResolvesTenantFromHeader()
    {
        var resolver = new MultiPatternRouteResolver().WithHeader("X-Tenant-Id");
        var context = ContextWith(headerName: "X-Tenant-Id", headerValue: "tenant-42");

        resolver.ResolveTenant(context).Should().Be("tenant-42");
    }

    [Test]
    public void WithSubdomain_ResolvesTenantFromHost()
    {
        var resolver = new MultiPatternRouteResolver().WithSubdomain();

        resolver.ResolveTenant(ContextWith(host: "acme.example.com")).Should().Be("acme");
    }

    [TestCase("www.example.com")]
    [TestCase("api.example.com")]
    [TestCase("admin.example.com")]
    public void WithSubdomain_IgnoresReservedSubdomains(string host)
    {
        var resolver = new MultiPatternRouteResolver().WithSubdomain();

        resolver.ResolveTenant(ContextWith(host: host)).Should().BeNull();
    }

    [Test]
    public void FallbackChain_PathThenHeaderThenSubdomain()
    {
        // No path match, no header -> falls through to subdomain.
        var resolver = new MultiPatternRouteResolver()
            .WithRegexPattern(@"/api/tenants/(?<tenant>[^/]+)")
            .WithHeader("X-Tenant-Id")
            .WithSubdomain();

        var context = ContextWith(path: "/health", host: "globex.example.com");

        resolver.ResolveTenant(context).Should().Be("globex");
    }
}
