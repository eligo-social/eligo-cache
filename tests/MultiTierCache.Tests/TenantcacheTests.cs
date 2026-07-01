using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace MultiTierCache.Tests;

[TestFixture]
public class TenantCacheTests
{
    private Mock<IMultiTierCache> _multiCacheMock;
    private TenantCache _tenantCache;

    [SetUp]
    public void Setup()
    {
        _multiCacheMock = new Mock<IMultiTierCache>();
        _tenantCache = new TenantCache(_multiCacheMock.Object, "tenant123");
    }

    [Test]
    public async Task GetAsync_UsesProvidedTenantId()
    {
        // Arrange
        var expectedValue = "test-value";
        _multiCacheMock.Setup(c => c.GetAsync<string>("tenant123", "key1"))
            .ReturnsAsync(expectedValue);

        // Act
        var result = await _tenantCache.GetAsync<string>("key1");

        // Assert
        result.Should().Be(expectedValue);
        _multiCacheMock.Verify(c => c.GetAsync<string>("tenant123", "key1"), Times.Once);
    }

    [Test]
    public async Task SetAsync_UsesProvidedTenantId()
    {
        // Arrange
        // No setup needed

        // Act
        await _tenantCache.SetAsync("key1", "value1");

        // Assert
        _multiCacheMock.Verify(c => c.SetAsync("tenant123", "key1", "value1"), Times.Once);
    }

    [Test]
    public void Constructor_ThrowsOnNullTenantId()
    {
        // Arrange
        var action = new Action(() => new TenantCache(_multiCacheMock.Object, null));

        // Act & Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Test]
    public void TenantId_Property_ReturnsProvidedId()
    {
        // Arrange
        var tenantCache = new TenantCache(_multiCacheMock.Object, "acme");

        // Act
        var tenantId = tenantCache.TenantId;

        // Assert
        tenantId.Should().Be("acme");
    }
}