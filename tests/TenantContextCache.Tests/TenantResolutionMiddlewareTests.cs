using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class TenantResolutionMiddlewareTests
{
    private static Mock<HttpContext> CreateContext(out IDictionary<object, object> items)
    {
        items = new Dictionary<object, object>();
        var ctx = new Mock<HttpContext>();
        ctx.Setup(c => c.Items).Returns(items);
        return ctx;
    }

    private static ITenantResolver ResolverReturning(string tenantId)
    {
        var mock = new Mock<ITenantResolver>();
        mock.Setup(r => r.ResolveTenant(It.IsAny<HttpContext>())).Returns(tenantId);
        return mock.Object;
    }

    private static List<Func<string, Task<(string key, object value)>>> DataResolvers(
        params Func<string, Task<(string key, object value)>>[] resolvers) => new(resolvers);

    [Test]
    public async Task InvokeAsync_SetsTenantId_WhenResolved()
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantResolutionMiddleware(next, ResolverReturning("acme"), new NullTenantInfoProvider());

        await middleware.InvokeAsync(ctx.Object);

        items.Should().ContainKey("TenantId");
        items["TenantId"].Should().Be("acme");
        nextCalled.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task InvokeAsync_DoesNotSetTenantId_WhenNotResolved(string tenantId)
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantResolutionMiddleware(next, ResolverReturning(tenantId), new NullTenantInfoProvider());

        await middleware.InvokeAsync(ctx.Object);

        items.Should().NotContainKey("TenantId");
        nextCalled.Should().BeTrue(); // pipeline continues even without a tenant
    }

    [Test]
    public async Task InvokeAsync_RunsDataResolvers_AndPopulatesItems()
    {
        var ctx = CreateContext(out var items);
        var company = new { Name = "Acme" };
        var receivedTenantId = (string)null;
        var resolvers = DataResolvers(tenantId =>
        {
            receivedTenantId = tenantId;
            return Task.FromResult<(string, object)>(("Company", company));
        });
        var middleware = new TenantResolutionMiddleware(
            _ => Task.CompletedTask, ResolverReturning("acme"), new NullTenantInfoProvider(), resolvers);

        await middleware.InvokeAsync(ctx.Object);

        receivedTenantId.Should().Be("acme");
        items.Should().ContainKey("Company");
        items["Company"].Should().BeSameAs(company);
    }

    [Test]
    public async Task InvokeAsync_DoesNotRunDataResolvers_WhenTenantNotResolved()
    {
        var ctx = CreateContext(out var items);
        var resolverRan = false;
        var resolvers = DataResolvers(_ =>
        {
            resolverRan = true;
            return Task.FromResult<(string, object)>(("Company", new object()));
        });
        var middleware = new TenantResolutionMiddleware(
            _ => Task.CompletedTask, ResolverReturning(null), new NullTenantInfoProvider(), resolvers);

        await middleware.InvokeAsync(ctx.Object);

        resolverRan.Should().BeFalse();
    }

    [TestCase(null, "value")]
    [TestCase("key", null)]
    [TestCase(null, null)]
    public async Task InvokeAsync_SkipsDataResolverResult_WhenKeyOrValueIsNull(string key, string value)
    {
        var ctx = CreateContext(out var items);
        var resolvers = DataResolvers(_ => Task.FromResult<(string, object)>((key, value)));
        var middleware = new TenantResolutionMiddleware(
            _ => Task.CompletedTask, ResolverReturning("acme"), new NullTenantInfoProvider(), resolvers);

        await middleware.InvokeAsync(ctx.Object);

        // Only the TenantId entry should be present.
        items.Keys.Should().BeEquivalentTo(new object[] { "TenantId" });
    }

    [Test]
    public async Task InvokeAsync_SwallowsResolverException_AndContinuesPipeline()
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        var secondResolverRan = false;
        var resolvers = DataResolvers(
            _ => throw new InvalidOperationException("boom"),
            _ =>
            {
                secondResolverRan = true;
                return Task.FromResult<(string, object)>(("Ok", "value"));
            });
        var middleware = new TenantResolutionMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            ResolverReturning("acme"), new NullTenantInfoProvider(), resolvers);

        // Act - should not throw despite the failing resolver
        await middleware.InvokeAsync(ctx.Object);

        // Assert - failure is isolated: later resolvers and the pipeline still run
        secondResolverRan.Should().BeTrue();
        items.Should().ContainKey("Ok");
        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task InvokeAsync_WithNullDataResolvers_UsesEmptyListAndRunsPipeline()
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            ResolverReturning("acme"), new NullTenantInfoProvider(), tenantDataResolvers: null);

        await middleware.InvokeAsync(ctx.Object);

        items["TenantId"].Should().Be("acme");
        nextCalled.Should().BeTrue();
    }
}
