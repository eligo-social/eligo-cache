using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class TenantResolutionMiddlewareTests
{
    private sealed class Company
    {
        public string Name { get; set; }
    }

    /// <summary>Records what it was asked for and returns a preconfigured result.</summary>
    private sealed class StubTenantInfoProvider : ITenantInfoProvider
    {
        private readonly object _result;

        public StubTenantInfoProvider(object result) => _result = result;

        public bool Called { get; private set; }
        public string ReceivedTenantId { get; private set; }

        public Type TenantInfoType => typeof(Company);

        public Task<object> GetTenantInfoAsync(string tenantId)
        {
            Called = true;
            ReceivedTenantId = tenantId;
            return Task.FromResult(_result);
        }
    }

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

    [Test]
    public async Task InvokeAsync_SetsTenantId_WhenResolved()
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantResolutionMiddleware(next, ResolverReturning("acme"));

        await middleware.InvokeAsync(ctx.Object, new StubTenantInfoProvider(null));

        items.Should().ContainKey("TenantId");
        items["TenantId"].Should().Be("acme");
        nextCalled.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task InvokeAsync_DoesNotSetTenantId_OrFetch_WhenNotResolved(string tenantId)
    {
        var ctx = CreateContext(out var items);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var provider = new StubTenantInfoProvider(new Company());
        var middleware = new TenantResolutionMiddleware(next, ResolverReturning(tenantId));

        await middleware.InvokeAsync(ctx.Object, provider);

        items.Should().NotContainKey("TenantId");
        provider.Called.Should().BeFalse(); // no tenant -> no fetch
        nextCalled.Should().BeTrue(); // pipeline continues even without a tenant
    }

    [Test]
    public async Task InvokeAsync_InjectsTenantInfo_UnderTypeKey_WhenProviderReturnsData()
    {
        var ctx = CreateContext(out var items);
        var company = new Company { Name = "Acme" };
        var provider = new StubTenantInfoProvider(company);
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, ResolverReturning("acme"));

        await middleware.InvokeAsync(ctx.Object, provider);

        provider.ReceivedTenantId.Should().Be("acme");
        items.Should().ContainKey("TenantInfo:Company");
        items["TenantInfo:Company"].Should().BeSameAs(company);
    }

    [Test]
    public async Task InvokeAsync_DoesNotInjectTenantInfo_WhenProviderReturnsNull()
    {
        var ctx = CreateContext(out var items);
        var provider = new StubTenantInfoProvider(null);
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, ResolverReturning("acme"));

        await middleware.InvokeAsync(ctx.Object, provider);

        provider.Called.Should().BeTrue();
        items.Keys.Should().BeEquivalentTo(new object[] { "TenantId" });
    }

    [Test]
    public async Task InvokeAsync_CallsNext_AfterInjectingTenantInfo()
    {
        var ctx = CreateContext(out _);
        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; }, ResolverReturning("acme"));

        await middleware.InvokeAsync(ctx.Object, new StubTenantInfoProvider(new Company()));

        nextCalled.Should().BeTrue();
    }
}
