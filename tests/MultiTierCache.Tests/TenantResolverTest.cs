using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace MultiTierCache.Tests;

[TestFixture]
    public class RegexTenantResolverTests
    {
        [TestCase("/api/tenants/acme/users", "acme")]
        [TestCase("/api/tenants/company-123/settings", "company-123")]
        [TestCase("/api/tenants/tenant_with_underscore/data", "tenant_with_underscore")]
        public void ResolveTenant_ExtractsTenantFromUrl(string path, string expectedTenant)
        {
            // Arrange
            var resolver = new RegexTenantResolver(@"/api/tenants/(?<tenant>[^/]+)");
            var context = new Mock<HttpContext>();
            var request = new Mock<HttpRequest>();
            
            request.Setup(r => r.Path).Returns(path);
            context.Setup(c => c.Request).Returns(request.Object);

            // Act
            var result = resolver.ResolveTenant(context.Object);

            // Assert
            result.Should().Be(expectedTenant);
        }

        [Test]
        public void ResolveTenant_ReturnsNull_WhenNoMatch()
        {
            // Arrange
            var resolver = new RegexTenantResolver(@"/api/tenants/(?<tenant>[^/]+)");
            var context = new Mock<HttpContext>();
            var request = new Mock<HttpRequest>();
            
            request.Setup(r => r.Path).Returns("/api/users");
            context.Setup(c => c.Request).Returns(request.Object);

            // Act
            var result = resolver.ResolveTenant(context.Object);

            // Assert
            result.Should().BeNull();
        }

        [TestCase("/api/v1/tenants/acme", "acme")]
        [TestCase("/api/v2/tenants/globex", "globex")]
        public void ResolveTenant_CaseInsensitive(string path, string expectedTenant)
        {
            // Arrange
            var resolver = new RegexTenantResolver(@"/api/v\d+/tenants/(?<tenant>[^/]+)");
            var context = new Mock<HttpContext>();
            var request = new Mock<HttpRequest>();
            
            request.Setup(r => r.Path).Returns(path);
            context.Setup(c => c.Request).Returns(request.Object);

            // Act
            var result = resolver.ResolveTenant(context.Object);

            // Assert
            result.Should().Be(expectedTenant);
        }
    }