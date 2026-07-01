using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace MultiTierCache.Tests;

[TestFixture]
    public class MultiTierCacheTests
    {
        private Mock<ICacheLayer> _l1Mock;
        private Mock<ICacheLayer> _l2Mock;
        private CacheConfiguration _config;
        private MultiTierCache _cache;

        [SetUp]
        public void Setup()
        {
            _l1Mock = new Mock<ICacheLayer>();
            _l2Mock = new Mock<ICacheLayer>();
            _config = new CacheConfiguration();
            _cache = new MultiTierCache(_l1Mock.Object, _l2Mock.Object, _config);
        }

        [Test]
        public async Task GetAsync_ReturnsL1Value_WhenAvailable()
        {
            // Arrange
            var expectedValue = "cached-value";
            _l1Mock.Setup(c => c.GetAsync<string>(It.IsAny<string>()))
                .ReturnsAsync(expectedValue);

            // Act
            var result = await _cache.GetAsync<string>("tenant1", "key1");

            // Assert
            result.Should().Be(expectedValue);
            _l2Mock.Verify(c => c.GetAsync<string>(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task GetAsync_FallsbackToL2_WhenL1Miss()
        {
            // Arrange
            var expectedValue = "l2-value";
            _config.L1TimeToLive = TimeSpan.FromMinutes(5);

            _l1Mock.Setup(c => c.GetAsync<string>(It.IsAny<string>()))
                .ReturnsAsync((string)null);
            _l2Mock.Setup(c => c.GetAsync<string>(It.IsAny<string>()))
                .ReturnsAsync(expectedValue);

            // Act
            var result = await _cache.GetAsync<string>("tenant1", "key1");

            // Assert
            result.Should().Be(expectedValue);
            _l1Mock.Verify(c => c.SetAsync(It.IsAny<string>(), expectedValue, TimeSpan.FromMinutes(5)), Times.Once);
        }

        [Test]
        public async Task SetAsync_StoresInBothLayers()
        {
            // Arrange
            _config.L1TimeToLive = TimeSpan.FromMinutes(5);
            _config.L2TimeToLive = TimeSpan.FromHours(1);
            var testValue = "test-data";

            // Act
            await _cache.SetAsync("tenant1", "key1", testValue);

            // Assert
            _l1Mock.Verify(c => c.SetAsync(
                It.Is<string>(k => k == "tenant:tenant1:key1"),
                testValue,
                TimeSpan.FromMinutes(5)), Times.Once);

            _l2Mock.Verify(c => c.SetAsync(
                It.Is<string>(k => k == "tenant:tenant1:key1"),
                testValue,
                TimeSpan.FromHours(1)), Times.Once);
        }

        [Test]
        public async Task RemoveAsync_RemovesFromBothLayers()
        {
            // Arrange
            // No setup needed

            // Act
            await _cache.RemoveAsync("tenant1", "key1");

            // Assert
            _l1Mock.Verify(c => c.RemoveAsync("tenant:tenant1:key1"), Times.Once);
            _l2Mock.Verify(c => c.RemoveAsync("tenant:tenant1:key1"), Times.Once);
        }

        [Test]
        public async Task RemoveAllTenantAsync_ClearsAllTenantKeys()
        {
            // Arrange
            _config.L1TimeToLive = TimeSpan.FromMinutes(5);
            _config.L2TimeToLive = TimeSpan.FromHours(1);

            // Set multiple keys
            await _cache.SetAsync("tenant1", "key1", "value1");
            await _cache.SetAsync("tenant1", "key2", "value2");

            // Act
            await _cache.RemoveAllTenantAsync("tenant1");

            // Assert
            _l1Mock.Verify(c => c.RemoveAsync(It.IsAny<string>()), Times.Exactly(2));
            _l2Mock.Verify(c => c.RemoveAsync(It.IsAny<string>()), Times.Exactly(2));
        }

        [Test]
        public async Task GetAsync_ReturnsNull_WhenNotInEitherLayer()
        {
            // Arrange
            _l1Mock.Setup(c => c.GetAsync<string>(It.IsAny<string>()))
                .ReturnsAsync((string)null);
            _l2Mock.Setup(c => c.GetAsync<string>(It.IsAny<string>()))
                .ReturnsAsync((string)null);

            // Act
            var result = await _cache.GetAsync<string>("tenant1", "key1");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public async Task GetAsync_WithEmptyTenantId_ReturnsNull()
        {
            // Arrange
            // No setup needed

            // Act
            var result = await _cache.GetAsync<string>(null, "key1");

            // Assert
            result.Should().BeNull();
        }
    }