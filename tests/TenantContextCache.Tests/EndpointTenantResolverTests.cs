using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class EndpointTenantResolverTests
{
    private static DefaultHttpContext ContextWith(TenantContextAttribute marker, params (string key, string value)[] routeValues)
    {
        var context = new DefaultHttpContext();

        if (marker != null)
        {
            var endpoint = new Endpoint(
                requestDelegate: null,
                metadata: new EndpointMetadataCollection(marker),
                displayName: "test");
            context.SetEndpoint(endpoint);
        }

        foreach (var (key, value) in routeValues)
            context.Request.RouteValues[key] = value;

        return context;
    }

    [Test]
    public void ResolveTenant_ReturnsNull_WhenNoEndpoint()
    {
        var resolver = new EndpointTenantResolver();

        resolver.ResolveTenant(new DefaultHttpContext()).Should().BeNull();
    }

    [Test]
    public void ResolveTenant_ReturnsNull_WhenEndpointNotAnnotated()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "test"));
        context.Request.RouteValues["tenant"] = "acme"; // present, but endpoint didn't opt in

        new EndpointTenantResolver().ResolveTenant(context).Should().BeNull();
    }

    [Test]
    public void ResolveTenant_ReturnsRouteValue_WhenAnnotated()
    {
        var context = ContextWith(new TenantContextAttribute("tenantId"), ("tenantId", "acme"));

        new EndpointTenantResolver().ResolveTenant(context).Should().Be("acme");
    }

    [Test]
    public void ResolveTenant_UsesNamedRouteParameter()
    {
        // Route exposes several values; only the one the attribute names is used.
        var context = ContextWith(
            new TenantContextAttribute("org"),
            ("tenantId", "wrong"),
            ("org", "contoso"));

        new EndpointTenantResolver().ResolveTenant(context).Should().Be("contoso");
    }

    [Test]
    public void ResolveTenant_ReturnsNull_WhenAnnotatedButRouteValueMissing()
    {
        var context = ContextWith(new TenantContextAttribute("tenantId")); // no route values

        new EndpointTenantResolver().ResolveTenant(context).Should().BeNull();
    }

    [Test]
    public void Attribute_DefaultsToTenant_AndRejectsBlankName()
    {
        new TenantContextAttribute().RouteParameter.Should().Be("tenant");

        var act = () => new TenantContextAttribute("  ");
        act.Should().Throw<ArgumentException>();
    }
}
