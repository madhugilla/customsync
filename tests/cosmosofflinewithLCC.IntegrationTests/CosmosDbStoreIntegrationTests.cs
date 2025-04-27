using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace cosmosofflinewithLCC.IntegrationTests
{
    [Collection("SequentialTests")]
    public class CosmosDbStoreIntegrationTests : IDisposable
    {
        private readonly Container _container;
        private readonly CosmosDbStore<Item> _store;
        private readonly CosmosClient _cosmosClient;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "TestDb";
        private readonly string _containerId = "TestContainer";

        public CosmosDbStoreIntegrationTests()
        {
            // Setup using local emulator
            _cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");

            try
            {
                // Delete test container if it exists to ensure clean state with correct partition key
                try
                {
                    Console.WriteLine($"Deleting container {_containerId} in database {_databaseId} if it exists");
                    _cosmosClient.GetDatabase(_databaseId).GetContainer(_containerId).DeleteContainerAsync().GetAwaiter().GetResult();
                    // Wait a moment for the delete to complete
                    System.Threading.Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    // Ignore if the container doesn't exist
                    Console.WriteLine($"Container deletion exception (can be ignored if not exists): {ex.Message}");
                }

                // Create database if not exists
                _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseId).GetAwaiter().GetResult();
                var database = _cosmosClient.GetDatabase(_databaseId);

                Console.WriteLine($"Creating container {_containerId} with partition key path /userId");

                // Create container with userId as the partition key
                database.CreateContainerIfNotExistsAsync(
                    id: _containerId,
                    partitionKeyPath: "/userId",
                    throughput: 400).GetAwaiter().GetResult();

                _container = database.GetContainer(_containerId);
                _store = new CosmosDbStore<Item>(_container);

                Console.WriteLine("CosmosDB test container is ready");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up Cosmos DB: {ex.Message}");
                throw;
            }

            // Clean up any existing data
            Console.WriteLine("Cleaning up any existing test data");
            CleanupTestData().GetAwaiter().GetResult();
        }

        private async Task CleanupTestData()
        {
            try
            {
                // Use a query to find all items (regardless of partition key)
                var query = new QueryDefinition("SELECT c.id, c.userId FROM c");
                var itemsToDelete = new List<(string id, string userId)>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var item in response)
                    {
                        string id = item.id;
                        string userId = item.userId ?? _testUserId; // Default to test user if null
                        itemsToDelete.Add((id, userId));
                    }
                }

                Console.WriteLine($"Found {itemsToDelete.Count} items to delete in cleanup");

                foreach (var (id, userId) in itemsToDelete)
                {
                    try
                    {
                        // Delete using userId as the partition key
                        Console.WriteLine($"Deleting item with id {id} from partition {userId}");
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(userId));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                        Console.WriteLine($"Item not found during deletion: {id} in partition {userId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up test data: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Clean up any test data
            CleanupTestData().GetAwaiter().GetResult();
        }

        [Fact]
        public async Task UpsertAndGetAsync_ShouldStoreAndRetrieveItem()
        {
            // Arrange
            var item = new Item
            {
                Id = Guid.NewGuid().ToString(),
                Content = "Test content",
                LastModified = DateTime.UtcNow,
                UserId = _testUserId,
                Type = "Item"
            };

            // Act - Upsert the item
            Console.WriteLine($"Upserting document with ID: {item.Id} in partition: {item.UserId}");
            await _store.UpsertAsync(item);

            // Retrieve the item
            var result = await _store.GetAsync(item.Id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(item.Id, result.Id);
            Assert.Equal(item.Content, result.Content);
            Assert.Equal(item.UserId, result.UserId);
            Assert.Equal(item.Type, result.Type);
        }

        [Fact]
        public async Task UpsertBulkAsync_ShouldStoreMultipleItems()
        {
            // Arrange
            var items = new List<Item>
            {
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "Item 1",
                    LastModified = DateTime.UtcNow,
                    UserId = _testUserId,
                    Type = "Item"
                },
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "Item 2",
                    LastModified = DateTime.UtcNow,
                    UserId = _testUserId,
                    Type = "Item"
                }
            };

            // Act
            foreach (var item in items)
            {
                Console.WriteLine($"Upserting document with ID: {item.Id} in partition: {item.UserId}");
            }
            await _store.UpsertBulkAsync(items);

            // Assert
            foreach (var expected in items)
            {
                var result = await _store.GetAsync(expected.Id);
                Assert.NotNull(result);
                Assert.Equal(expected.Id, result.Id);
                Assert.Equal(expected.Content, result.Content);
                Assert.Equal(_testUserId, result.UserId);
                Assert.Equal("Item", result.Type);
            }
        }

        [Fact]
        public async Task GetAllAsync_ShouldReturnAllItems()
        {
            // Arrange
            var items = new List<Item>
            {
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "Item 1",
                    LastModified = DateTime.UtcNow,
                    UserId = _testUserId,
                    Type = "Item"
                },
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "Item 2",
                    LastModified = DateTime.UtcNow,
                    UserId = _testUserId,
                    Type = "Item"
                }
            };

            foreach (var item in items)
            {
                Console.WriteLine($"Upserting document with ID: {item.Id} in partition: {item.UserId}");
            }
            await _store.UpsertBulkAsync(items);

            // Act
            var results = await _store.GetAllAsync();

            // Assert
            Assert.NotEmpty(results);
            Assert.Equal(items.Count, results.Count);
            foreach (var expected in items)
            {
                Assert.Contains(results, r => r.Id == expected.Id && r.Content == expected.Content);
            }
        }

        [Fact]
        public async Task GetByUserIdAsync_ShouldReturnUserSpecificItems()
        {
            // Arrange
            var user1Id = "user1";
            var user2Id = "user2";

            var user1Items = new List<Item>
            {
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "User 1 Item 1",
                    LastModified = DateTime.UtcNow,
                    UserId = user1Id,
                    Type = "Item"
                },
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "User 1 Item 2",
                    LastModified = DateTime.UtcNow,
                    UserId = user1Id,
                    Type = "Item"
                }
            };

            var user2Items = new List<Item>
            {
                new Item
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = "User 2 Item 1",
                    LastModified = DateTime.UtcNow,
                    UserId = user2Id,
                    Type = "Item"
                }
            };

            // Add all items
            await _store.UpsertBulkAsync(user1Items);
            await _store.UpsertBulkAsync(user2Items);

            // Act
            var user1Results = await _store.GetByUserIdAsync(user1Id);
            var user2Results = await _store.GetByUserIdAsync(user2Id);

            // Assert
            Assert.Equal(user1Items.Count, user1Results.Count);
            Assert.Equal(user2Items.Count, user2Results.Count);

            foreach (var expected in user1Items)
            {
                Assert.Contains(user1Results, r => r.Id == expected.Id && r.UserId == user1Id);
            }

            foreach (var expected in user2Items)
            {
                Assert.Contains(user2Results, r => r.Id == expected.Id && r.UserId == user2Id);
            }

            // Verify that user1's items don't appear in user2's results and vice versa
            Assert.DoesNotContain(user1Results, r => r.UserId == user2Id);
            Assert.DoesNotContain(user2Results, r => r.UserId == user1Id);
        }

        [Fact]
        public async Task GetByUserIdAsync_ShouldReturnEmptyList_WhenUserHasNoItems()
        {
            // Arrange - No items for "nonexistent-user"

            // Act
            var results = await _store.GetByUserIdAsync("nonexistent-user");

            // Assert
            Assert.Empty(results);
        }
    }
}