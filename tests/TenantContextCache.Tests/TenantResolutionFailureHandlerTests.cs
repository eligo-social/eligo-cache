using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace TenantContextCache.Tests;

/// <summary>
/// Covers the tenant-resolution failure handler: the middleware hands each failure — no tenant in
/// the request, no tenant found for the id, or the retrieval throwing — to the configured handler,
/// which returns the HTTP result to reply with. Returning null opts out of intercepting that
/// failure, leaving the pre-existing behaviour intact.
/// </summary>
[TestFixture]
public class TenantResolutionFailureHandlerTests
{
    private sealed class Company
    {
        public string Name { get; set; }
    }

    private sealed class StubTenantInfoProvider : ITenantInfoProvider
    {
        private readonly object _result;
        private readonly Exception _throws;

        private StubTenantInfoProvider(object result, Exception throws)
        {
            _result = result;
            _throws = throws;
        }

        public static StubTenantInfoProvider Returning(object result) => new(result, null);
        public static StubTenantInfoProvider Throwing(Exception ex) => new(null, ex);

        public Type TenantInfoType => typeof(Company);

        public Task<object> GetTenantInfoAsync(string tenantId)
        {
            if (_throws != null)
                throw _throws;

            return Task.FromResult(_result);
        }
    }

    private static ITenantResolver ResolverReturning(string tenantId)
    {
        var mock = new Mock<ITenantResolver>();
        mock.Setup(r => r.ResolveTenant(It.IsAny<HttpContext>())).Returns(tenantId);
        return mock.Object;
    }

    /// <summary>
    /// A real HttpContext, so results can actually be executed against a response. Executing an
    /// IResult resolves services (a logger factory, at least) from RequestServices, which a real
    /// request always has.
    /// </summary>
    private static DefaultHttpContext CreateContext()
    {
        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static TenantResolutionMiddleware Middleware(
        RequestDelegate next,
        string resolvedTenantId,
        Func<TenantResolutionFailure, IResult> handler)
    {
        var options = new TenantResolutionOptions();
        if (handler != null)
            options.FailureHandler = failure => Task.FromResult(handler(failure));

        return new TenantResolutionMiddleware(next, ResolverReturning(resolvedTenantId), options);
    }

    [Test]
    public async Task TenantNotFound_HandlerResult_ShortCircuitsThePipeline()
    {
        var context = CreateContext();
        var nextCalled = false;
        var middleware = Middleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            "acme",
            failure => Results.Text($"unknown tenant {failure.TenantId}", statusCode: 404));

        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(null));

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
        ReadBody(context).Should().Be("unknown tenant acme");
    }

    [Test]
    public async Task TenantNotFound_ReceivesReasonAndTenantId_WithNoException()
    {
        TenantResolutionFailure captured = null;
        var middleware = Middleware(_ => Task.CompletedTask, "acme", failure =>
        {
            captured = failure;
            return Results.StatusCode(404);
        });

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(null));

        captured.Reason.Should().Be(TenantResolutionFailureReason.TenantNotFound);
        captured.TenantId.Should().Be("acme");
        captured.Exception.Should().BeNull();
        captured.HttpContext.Should().BeSameAs(context);
    }

    [Test]
    public async Task TenantNotFound_HandlerReturningNull_LetsTheRequestContinue()
    {
        var context = CreateContext();
        var nextCalled = false;
        var middleware = Middleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            "acme",
            _ => null);

        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(null));

        // Unchanged behaviour: the tenant id is still attached, just no tenant info.
        nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be("acme");
        context.Items.Should().NotContainKey("TenantInfo:Company");
    }

    [Test]
    public async Task TenantNotResolved_ReceivesNullTenantId()
    {
        TenantResolutionFailure captured = null;
        var middleware = Middleware(_ => Task.CompletedTask, null, failure =>
        {
            captured = failure;
            return Results.StatusCode(400);
        });

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(new Company()));

        captured.Reason.Should().Be(TenantResolutionFailureReason.TenantNotResolved);
        captured.TenantId.Should().BeNull();
        context.Response.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task TenantNotResolved_HandlerReturningNull_LetsUnscopedRequestsThrough()
    {
        // The common case: a handler that only intercepts TenantNotFound must not break every
        // endpoint that simply isn't tenant-scoped.
        var context = CreateContext();
        var nextCalled = false;
        var middleware = Middleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            null,
            failure => failure.Reason == TenantResolutionFailureReason.TenantNotFound
                ? Results.StatusCode(404)
                : null);

        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(new Company()));

        nextCalled.Should().BeTrue();
        context.Items.Should().NotContainKey("TenantId");
    }

    [Test]
    public async Task TenantRetrievalFailed_HandlerReceivesTheException_AndCanReply()
    {
        var boom = new InvalidOperationException("database is down");
        TenantResolutionFailure captured = null;
        var nextCalled = false;
        var middleware = Middleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            "acme",
            failure =>
            {
                captured = failure;
                return Results.Text("try later", statusCode: 503);
            });

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Throwing(boom));

        captured.Reason.Should().Be(TenantResolutionFailureReason.TenantRetrievalFailed);
        captured.TenantId.Should().Be("acme");
        captured.Exception.Should().BeSameAs(boom);
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(503);
    }

    [Test]
    public async Task TenantRetrievalFailed_WithNoHandler_Propagates()
    {
        var boom = new InvalidOperationException("database is down");
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, ResolverReturning("acme"));

        var act = async () => await middleware.InvokeAsync(CreateContext(), StubTenantInfoProvider.Throwing(boom));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Test]
    public async Task TenantRetrievalFailed_HandlerReturningNull_RethrowsForOuterMiddleware()
    {
        var boom = new InvalidOperationException("database is down");
        var middleware = Middleware(_ => Task.CompletedTask, "acme", _ => null);

        var act = async () => await middleware.InvokeAsync(CreateContext(), StubTenantInfoProvider.Throwing(boom));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Test]
    public async Task SuccessfulResolution_NeverCallsTheHandler()
    {
        var handlerCalled = false;
        var middleware = Middleware(_ => Task.CompletedTask, "acme", _ =>
        {
            handlerCalled = true;
            return Results.StatusCode(500);
        });

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(new Company { Name = "Acme" }));

        handlerCalled.Should().BeFalse();
        context.Items.Should().ContainKey("TenantInfo:Company");
    }

    [Test]
    public async Task AsyncHandler_IsAwaited()
    {
        var options = new TenantResolutionOptions
        {
            FailureHandler = async failure =>
            {
                await Task.Yield();
                return Results.Text($"async {failure.Reason}", statusCode: 404);
            }
        };
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask, ResolverReturning("acme"), options);

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(null));

        context.Response.StatusCode.Should().Be(404);
        ReadBody(context).Should().Be("async TenantNotFound");
    }

    [Test]
    public async Task RealPipeline_ResolvesTheHandlerFromDi_AndShortCircuits()
    {
        // The middleware takes its options as a DI-resolved constructor parameter, which
        // UseMiddleware fills in — a wiring that only fails at request time, so exercise the
        // actual pipeline rather than constructing the middleware directly.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<Company>(_ => Task.FromResult<Company>(null)) // unknown tenant
            .WithCustomL2(new InProcessDistributedCache())
            .WithTenantResolutionFailureHandler(failure =>
                Results.Text($"no tenant: {failure.Reason}", statusCode: 404)));

        using var root = services.BuildServiceProvider();
        var app = new ApplicationBuilder(root);
        app.UseTenantContextCacheWithResolver(ResolverReturning("acme"));

        var nextCalled = false;
        app.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var pipeline = app.Build();

        using var scope = root.CreateScope();
        var context = CreateContext();
        context.RequestServices = scope.ServiceProvider;

        await pipeline(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
        ReadBody(context).Should().Be("no tenant: TenantNotFound");
    }

    [Test]
    public void WithTenantResolutionFailureHandler_IsRegisteredForTheMiddleware()
    {
        Func<TenantResolutionFailure, IResult> handler = _ => Results.StatusCode(404);

        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<Company>(_ => Task.FromResult<Company>(null))
            .WithCustomL2(new InProcessDistributedCache())
            .WithTenantResolutionFailureHandler(handler));

        var options = services.BuildServiceProvider().GetRequiredService<TenantResolutionOptions>();

        options.FailureHandler.Should().NotBeNull();
    }

    [Test]
    public void WithTenantResolutionFailureHandler_DefaultsToNoHandler()
    {
        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<Company>(_ => Task.FromResult<Company>(null))
            .WithCustomL2(new InProcessDistributedCache()));

        var options = services.BuildServiceProvider().GetRequiredService<TenantResolutionOptions>();

        options.FailureHandler.Should().BeNull();
    }

    [Test]
    public async Task ObserveOnlyHandler_SeesEveryFailure_WithoutChangingBehaviour()
    {
        // The documented "log it and carry on" shape. The cast picks the synchronous overload;
        // a lambda whose only return is a bare null cannot choose between the two.
        var seen = new List<TenantResolutionFailureReason>();

        var services = new ServiceCollection();
        services.AddTenantContextCache(c => c
            .WithTenantDataFetch<Company>(_ => Task.FromResult<Company>(null))
            .WithCustomL2(new InProcessDistributedCache())
            .WithTenantResolutionFailureHandler(failure =>
            {
                seen.Add(failure.Reason);
                return (IResult)null;
            }));

        var options = services.BuildServiceProvider().GetRequiredService<TenantResolutionOptions>();

        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; }, ResolverReturning("acme"), options);

        var context = CreateContext();
        await middleware.InvokeAsync(context, StubTenantInfoProvider.Returning(null));

        seen.Should().Equal(TenantResolutionFailureReason.TenantNotFound);
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
    }

    [Test]
    public void WithTenantResolutionFailureHandler_RejectsNull()
    {
        var services = new ServiceCollection();

        var nullSync = () => services.AddTenantContextCache(c => c
            .WithTenantResolutionFailureHandler((Func<TenantResolutionFailure, IResult>)null));
        var nullAsync = () => services.AddTenantContextCache(c => c
            .WithTenantResolutionFailureHandler((Func<TenantResolutionFailure, Task<IResult>>)null));

        nullSync.Should().Throw<ArgumentNullException>();
        nullAsync.Should().Throw<ArgumentNullException>();
    }
}
