using FluentAssertions;
using Moq;
using NUnit.Framework;
using Moq;
using FluentAssertions;

namespace MultiTierCache.Tests;

[TestFixture]
    public class CacheIntegrationTests
    {
        [Test]
        public async Task FullWorkflow_L1Miss_PopulatesFromL2()
        {
            // Arrange
            var l1 = new InMemoryL1Cache();
            var l2 = new InMemoryL1Cache();
            var config = new CacheConfiguration
            {
                L1TimeToLive = TimeSpan.FromMinutes(5),
                L2TimeToLive = TimeSpan.FromHours(1)
            };

            var cache = new MultiTierCache(l1, l2, config);
            var testData = new { id = 1, name = "John" };

            // Pre-populate L2
            await l2.SetAsync("tenant:acme:user-123", testData, TimeSpan.FromHours(1));

            // Act - First call should populate L1 from L2
            var result = await cache.GetAsync<dynamic>("acme", "user-123");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.id, Is.EqualTo(1));
            
            // Verify L1 now has the value
            var l1Direct = await l1.GetAsync<dynamic>("tenant:acme:user-123");
            Assert.That(l1Direct, Is.Not.Null);
        }

        [Test]
        public async Task FullWorkflow_MultiTenantIsolation()
        {
            // Arrange
            var l1 = new InMemoryL1Cache();
            var l2 = new InMemoryL1Cache();
            var config = new CacheConfiguration
            {
                L1TimeToLive = TimeSpan.FromMinutes(5),
                L2TimeToLive = TimeSpan.FromHours(1)
            };

            var cache = new MultiTierCache(l1, l2, config);

            // Act
            await cache.SetAsync("tenant-a", "config", new { theme = "dark" });
            await cache.SetAsync("tenant-b", "config", new { theme = "light" });

            var resultA = await cache.GetAsync<dynamic>("tenant-a", "config");
            var resultB = await cache.GetAsync<dynamic>("tenant-b", "config");

            Assert.That(resultA, Is.Not.Null);
            Assert.That(resultB, Is.Not.Null);
            // Assert
            Assert.That(resultA.theme, Is.EqualTo("dark"));
            Assert.That(resultB.theme, Is.EqualTo("light"));
        }

        [Test]
        public async Task FullWorkflow_CacheKeyIsolation()
        {
            // Arrange
            var l1 = new InMemoryL1Cache();
            var config = new CacheConfiguration();
            var cache = new MultiTierCache(l1, new Mock<ICacheLayer>().Object, config);

            // Act
            await cache.SetAsync("tenant1", "shared-key", "tenant1-value");
            await cache.SetAsync("tenant2", "shared-key", "tenant2-value");

            var result1 = await cache.GetAsync<string>("tenant1", "shared-key");
            var result2 = await cache.GetAsync<string>("tenant2", "shared-key");

            // Assert
            result1.Should().Be("tenant1-value");
            result2.Should().Be("tenant2-value");
        }
    }