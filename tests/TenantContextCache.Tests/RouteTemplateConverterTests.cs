using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace TenantContextCache.Tests;

[TestFixture]
public class RouteTemplateConverterTests
{
    private static System.Text.RegularExpressions.Match MatchPath(string template, string path, string tenantParameterName = null)
    {
        var pattern = RouteTemplateConverter.ToRegexPattern(template, tenantParameterName);
        return Regex.Match(path, pattern, RegexOptions.IgnoreCase);
    }

    [Test]
    public void IntConstraint_MatchesDigitsOnly()
    {
        var match = MatchPath("/api/tenants/{tenantId:int}", "/api/tenants/123/users");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("123");
    }

    [Test]
    public void IntConstraint_DoesNotMatchNonNumericTenant()
    {
        var match = MatchPath("/api/tenants/{tenantId:int}", "/api/tenants/acme/users");

        match.Success.Should().BeFalse();
    }

    [Test]
    public void NoConstraint_MatchesAnySingleSegment()
    {
        var match = MatchPath("/api/tenants/{tenantId}", "/api/tenants/acme-corp/data");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("acme-corp");
    }

    [Test]
    public void GuidConstraint_MatchesGuid()
    {
        var id = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
        var match = MatchPath("/api/tenants/{tenantId:guid}", $"/api/tenants/{id}/info");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be(id);
    }

    [Test]
    public void AlphaConstraint_MatchesLettersOnly()
    {
        // Pattern is unanchored (matching RegexTenantResolver), so a trailing "123"
        // simply stops the capture at the last letter.
        MatchPath("/t/{id:alpha}", "/t/acme").Groups["tenant"].Value.Should().Be("acme");
        MatchPath("/t/{id:alpha}", "/t/acme123").Groups["tenant"].Value.Should().Be("acme");
        MatchPath("/t/{id:alpha}", "/t/123").Success.Should().BeFalse();
    }

    [Test]
    public void ChainedConstraint_UsesFirstTokenShape()
    {
        // "int:min(1)" should still be treated as an integer segment.
        var match = MatchPath("/api/tenants/{tenantId:int:min(1)}", "/api/tenants/42");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("42");
    }

    [Test]
    public void CatchAll_MatchesRemainingPath()
    {
        var match = MatchPath("/files/{*path}", "/files/a/b/c.txt");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("a/b/c.txt");
    }

    [Test]
    public void MultiplePlaceholders_CapturesNamedTenantParameter()
    {
        var match = MatchPath(
            "/api/{version}/tenants/{tenantId:int}",
            "/api/v1/tenants/99",
            tenantParameterName: "tenantId");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("99");
    }

    [Test]
    public void MultiplePlaceholders_DefaultsToFirstPlaceholder()
    {
        var match = MatchPath("/api/{version}/tenants/{tenantId:int}", "/api/v1/tenants/99");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("v1");
    }

    [Test]
    public void UnknownConstraint_FallsBackToDefaultSegment()
    {
        var match = MatchPath("/t/{id:datetime}", "/t/anything");

        match.Success.Should().BeTrue();
        match.Groups["tenant"].Value.Should().Be("anything");
    }

    [Test]
    public void LiteralCharacters_AreEscaped()
    {
        // The '.' in the literal must be treated literally, not as "any char".
        var pattern = RouteTemplateConverter.ToRegexPattern("/api.v1/tenants/{tenantId}");

        Regex.IsMatch("/apiXv1/tenants/acme", pattern).Should().BeFalse();
        Regex.IsMatch("/api.v1/tenants/acme", pattern).Should().BeTrue();
    }

    [Test]
    public void NullOrEmptyTemplate_Throws()
    {
        Action act = () => RouteTemplateConverter.ToRegexPattern("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void TemplateWithoutPlaceholders_Throws()
    {
        Action act = () => RouteTemplateConverter.ToRegexPattern("/api/tenants");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void UnknownTenantParameterName_Throws()
    {
        Action act = () => RouteTemplateConverter.ToRegexPattern(
            "/api/tenants/{tenantId:int}", tenantParameterName: "missing");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ConvertedPattern_WorksWithRegexTenantResolver()
    {
        // End-to-end: the produced pattern resolves through RegexTenantResolver.
        var pattern = RouteTemplateConverter.ToRegexPattern("/api/tenants/{tenantId:int}");
        var resolver = new RegexTenantResolver(pattern);

        var context = new Mock<HttpContext>();
        var request = new Mock<HttpRequest>();
        request.Setup(r => r.Path).Returns("/api/tenants/777/settings");
        context.Setup(c => c.Request).Returns(request.Object);

        resolver.ResolveTenant(context.Object).Should().Be("777");
    }
}
