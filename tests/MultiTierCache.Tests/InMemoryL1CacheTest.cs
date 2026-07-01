using FluentAssertions;
using NUnit.Framework;

namespace MultiTierCache.Tests;

[TestFixture]
public class InMemoryL1CacheTest
    {
        private InMemoryL1Cache _cache;

        [SetUp]
        public void Setup()
        {
            _cache = new InMemoryL1Cache();
        }

        [Test]
        public async Task SetAsync_StoresValue()
        {
            // Arrange
            var testValue = "test-data";
            var ttl = TimeSpan.FromMinutes(5);

            // Act
            await _cache.SetAsync("key1", testValue, ttl);
            var result = await _cache.GetAsync<string>("key1");

            // Assert
            result.Should().Be(testValue);
        }

        [Test]
        public async Task GetAsync_ReturnsNull_WhenKeyNotFound()
        {
            // Arrange
            // No setup needed

            // Act
            var result = await _cache.GetAsync<string>("nonexistent-key");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public async Task GetAsync_ReturnsNull_WhenExpired()
        {
            // Arrange
            await _cache.SetAsync("key1", "value", TimeSpan.FromMilliseconds(100));

            // Act
            await Task.Delay(150);
            var result = await _cache.GetAsync<string>("key1");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public async Task RemoveAsync_DeletesValue()
        {
            // Arrange
            await _cache.SetAsync("key1", "value", TimeSpan.FromMinutes(5));

            // Act
            await _cache.RemoveAsync("key1");
            var result = await _cache.GetAsync<string>("key1");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public async Task ExistsAsync_ReturnsFalse_WhenKeyNotFound()
        {
            // Arrange
            // No setup needed

            // Act
            var exists = await _cache.ExistsAsync("nonexistent");

            // Assert
            exists.Should().BeFalse();
        }

        [Test]
        public async Task ExistsAsync_ReturnsTrue_WhenKeyExists()
        {
            // Arrange
            await _cache.SetAsync("key1", "value", TimeSpan.FromMinutes(5));

            // Act
            var exists = await _cache.ExistsAsync("key1");

            // Assert
            exists.Should().BeTrue();
        }

        [Test]
        public async Task ExistsAsync_ReturnsFalse_WhenExpired()
        {
            // Arrange
            await _cache.SetAsync("key1", "value", TimeSpan.FromMilliseconds(100));

            // Act
            await Task.Delay(150);
            var exists = await _cache.ExistsAsync("key1");

            // Assert
            exists.Should().BeFalse();
        }

        [Test]
        public async Task MultipleKeys_Isolated()
        {
            // Arrange
            // No setup needed

            // Act
            await _cache.SetAsync("key1", "value1", TimeSpan.FromMinutes(5));
            await _cache.SetAsync("key2", "value2", TimeSpan.FromMinutes(5));
            var result1 = await _cache.GetAsync<string>("key1");
            var result2 = await _cache.GetAsync<string>("key2");

            // Assert
            result1.Should().Be("value1");
            result2.Should().Be("value2");
        }
    }