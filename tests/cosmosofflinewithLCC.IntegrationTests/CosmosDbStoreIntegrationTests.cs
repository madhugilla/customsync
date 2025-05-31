using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;

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
                var database = _cosmosClient.GetDatabase(_databaseId); Console.WriteLine($"Creating container {_containerId} with partition key path /partitionKey");

                // Create container with composite partition key (userId:docType)
                database.CreateContainerIfNotExistsAsync(
                    id: _containerId,
                    partitionKeyPath: "/partitionKey",
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
                var query = new QueryDefinition("SELECT c.id, c.partitionKey FROM c");
                var itemsToDelete = new List<(string id, string partitionKey)>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var item in response)
                    {
                        string id = item.id;
                        string partitionKey = item.partitionKey;

                        if (string.IsNullOrEmpty(partitionKey))
                        {
                            // Fall back to old format if needed
                            if (item.userId != null)
                            {
                                string userId = item.userId;
                                partitionKey = $"{userId}:Item"; // Default type to Item if not specified
                            }
                            else
                            {
                                partitionKey = $"{_testUserId}:Item"; // Default fallback
                            }
                        }

                        itemsToDelete.Add((id, partitionKey));
                    }
                }

                Console.WriteLine($"Found {itemsToDelete.Count} items to delete in cleanup");

                foreach (var (id, partitionKey) in itemsToDelete)
                {
                    try
                    {
                        // Delete using the composite partition key
                        Console.WriteLine($"Deleting item with id {id} from partition {partitionKey}");
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                        Console.WriteLine($"Item not found during deletion: {id} in partition {partitionKey}");
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
                ID = Guid.NewGuid().ToString(),
                Content = "Test Item",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Item"
            };

            // Act
            Console.WriteLine($"Upserting document with ID: {item.ID} in partition: {item.OIID}");
            await _store.UpsertAsync(item);

            // Assert
            var result = await _store.GetAsync(item.ID, item.OIID);
            Assert.NotNull(result);
            Assert.Equal(item.ID, result.ID);
            Assert.Equal(item.Content, result.Content);
            Assert.Equal(_testUserId, result.OIID);
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
                    ID = Guid.NewGuid().ToString(),
                    Content = "Item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = _testUserId,
                    Type = "Item"
                },
                new Item
                {
                    ID = Guid.NewGuid().ToString(),
                    Content = "Item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = _testUserId,
                    Type = "Item"
                }
            };

            // Act
            foreach (var item in items)
            {
                Console.WriteLine($"Upserting document with ID: {item.ID} in partition: {item.OIID}");
            }
            await _store.UpsertBulkAsync(items);            // Assert
            foreach (var expected in items)
            {
                var result = await _store.GetAsync(expected.ID, expected.OIID);
                Assert.NotNull(result);
                Assert.Equal(expected.ID, result.ID);
                Assert.Equal(expected.Content, result.Content);
                Assert.Equal(_testUserId, result.OIID);
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
                    ID = Guid.NewGuid().ToString(),
                    Content = "Item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = _testUserId,
                    Type = "Item"
                },
                new Item
                {
                    ID = Guid.NewGuid().ToString(),
                    Content = "Item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = _testUserId,
                    Type = "Item"
                }
            };

            foreach (var item in items)
            {
                Console.WriteLine($"Upserting document with ID: {item.ID} in partition: {item.OIID}");
            }
            await _store.UpsertBulkAsync(items);

            // Act
            var results = await _store.GetAllAsync();

            // Assert
            Assert.NotEmpty(results);
            Assert.Equal(items.Count, results.Count);
            foreach (var expected in items)
            {
                Assert.Contains(results, r => r.ID == expected.ID && r.Content == expected.Content);
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
                    ID = Guid.NewGuid().ToString(),
                    Content = "User 1 Item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = user1Id,
                    Type = "Item"
                },
                new Item
                {
                    ID = Guid.NewGuid().ToString(),
                    Content = "User 1 Item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = user1Id,
                    Type = "Item"
                }
            };

            var user2Items = new List<Item>
            {
                new Item
                {
                    ID = Guid.NewGuid().ToString(),
                    Content = "User 2 Item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = user2Id,
                    Type = "Item"
                },
                new Item
                {
                    ID = Guid.NewGuid().ToString(),
                    Content = "User 2 Item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = user2Id,
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
                Assert.Contains(user1Results, r => r.ID == expected.ID && r.OIID == user1Id);
            }

            foreach (var expected in user2Items)
            {
                Assert.Contains(user2Results, r => r.ID == expected.ID && r.OIID == user2Id);
            }

            // Verify that user1's items don't appear in user2's results and vice versa
            Assert.DoesNotContain(user1Results, r => r.OIID == user2Id);
            Assert.DoesNotContain(user2Results, r => r.OIID == user1Id);
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

        [Fact]
        public async Task GetAsync_WithCompositePartitionKey_ShouldRetrieveItem()
        {
            // Arrange
            var item = new Item
            {
                ID = Guid.NewGuid().ToString(),
                Content = "Composite partition key test",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Item"
            };

            // Act - Upsert the item
            Console.WriteLine($"Upserting document with ID: {item.ID} with composite partition key: {_testUserId}:Item");
            await _store.UpsertAsync(item);

            // Retrieve the item using the user ID (which should work with the composite partition key)
            var result = await _store.GetAsync(item.ID, item.OIID);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(item.ID, result.ID);
            Assert.Equal(item.Content, result.Content);
            Assert.Equal(item.OIID, result.OIID);
        }

        [Fact]
        public async Task MultipleDocumentTypes_ShouldBeStoredInSameContainer()
        {
            // Arrange - create an Order class in the same container
            var container = _container;
            var orderStore = new CosmosDbStore<Order>(container);

            var item = new Item
            {
                ID = Guid.NewGuid().ToString(),
                Content = "Item content",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Item"
            };

            var order = new Order
            {
                ID = Guid.NewGuid().ToString(),
                Description = "Test Order",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Order"
            };

            // Act - store both types
            await _store.UpsertAsync(item);
            await orderStore.UpsertAsync(order);

            // Assert
            var retrievedItem = await _store.GetAsync(item.ID, _testUserId);
            var retrievedOrder = await orderStore.GetAsync(order.ID, _testUserId);

            Assert.NotNull(retrievedItem);
            Assert.Equal("Item", retrievedItem.Type);
            Assert.Equal(item.Content, retrievedItem.Content);

            Assert.NotNull(retrievedOrder);
            Assert.Equal("Order", retrievedOrder.Type);
            Assert.Equal(order.Description, retrievedOrder.Description);
        }

        [Fact]
        public async Task HandleMissingTypeProperty_ShouldDefaultToClassName()
        {
            // Arrange
            var item = new Item
            {
                ID = Guid.NewGuid().ToString(),
                Content = "Item without explicit type",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = null! // This will cause the store to fall back to the class name
            };

            // Act
            Console.WriteLine($"Upserting document with ID: {item.ID} with composite partition key: {_testUserId}:Item");
            await _store.UpsertAsync(item);

            // Assert
            var result = await _store.GetAsync(item.ID, item.OIID);
            Assert.NotNull(result);
            Assert.Equal(item.ID, result.ID);
            Assert.Equal(item.Content, result.Content);
            Assert.Equal(_testUserId, result.OIID);
        }

        [Fact]
        public async Task GetByUserIdAsync_WithExcludeIds_ShouldExcludeSpecifiedItems()
        {
            // Arrange
            var userId = "user-exclude-test";
            var items = new List<Item>
            {
                new Item
                {
                    ID = "item1",
                    Content = "Item 1 content",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "item2",
                    Content = "Item 2 content",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "item3",
                    Content = "Item 3 content",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "item4",
                    Content = "Item 4 content",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                }
            };

            // Add all items
            await _store.UpsertBulkAsync(items);

            // Act - exclude items 1 and 3
            var excludeIds = new HashSet<string> { "item1", "item3" };
            var results = await _store.GetByUserIdAsync(userId, excludeIds);

            // Assert
            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.ID == "item2");
            Assert.Contains(results, r => r.ID == "item4");
            Assert.DoesNotContain(results, r => r.ID == "item1");
            Assert.DoesNotContain(results, r => r.ID == "item3");

            // Verify all returned items belong to the correct user
            Assert.All(results, r => Assert.Equal(userId, r.OIID));
        }

        [Fact]
        public async Task GetByUserIdAsync_WithEmptyExcludeIds_ShouldReturnAllUserItems()
        {
            // Arrange
            var userId = "user-empty-exclude";
            var items = new List<Item>
            {
                new Item
                {
                    ID = "empty1",
                    Content = "Empty test 1",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "empty2",
                    Content = "Empty test 2",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                }
            };

            await _store.UpsertBulkAsync(items);

            // Act - test with empty exclude set
            var emptyExcludeIds = new HashSet<string>();
            var resultsEmpty = await _store.GetByUserIdAsync(userId, emptyExcludeIds);

            // Act - test with null exclude set
            var resultsNull = await _store.GetByUserIdAsync(userId, null);

            // Assert - both should return all items
            Assert.Equal(2, resultsEmpty.Count);
            Assert.Equal(2, resultsNull.Count);
            Assert.Contains(resultsEmpty, r => r.ID == "empty1");
            Assert.Contains(resultsEmpty, r => r.ID == "empty2");
            Assert.Contains(resultsNull, r => r.ID == "empty1");
            Assert.Contains(resultsNull, r => r.ID == "empty2");
        }

        [Fact]
        public async Task GetByUserIdAsync_WithNonExistentExcludeIds_ShouldReturnAllUserItems()
        {
            // Arrange
            var userId = "user-nonexistent-exclude";
            var items = new List<Item>
            {
                new Item
                {
                    ID = "real1",
                    Content = "Real item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "real2",
                    Content = "Real item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                }
            };

            await _store.UpsertBulkAsync(items);

            // Act - exclude non-existent items
            var excludeIds = new HashSet<string> { "nonexistent1", "nonexistent2" };
            var results = await _store.GetByUserIdAsync(userId, excludeIds);

            // Assert - should return all items since excluded IDs don't exist
            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.ID == "real1");
            Assert.Contains(results, r => r.ID == "real2");
        }

        [Fact]
        public async Task GetByUserIdAsync_WithLargeExcludeIdSet_ShouldHandleEfficiently()
        {
            // Arrange
            var userId = "user-large-exclude";
            var totalItems = 10;
            var excludeCount = 7;

            var items = new List<Item>();
            var excludeIds = new HashSet<string>();

            // Create items and exclude IDs
            for (int i = 1; i <= totalItems; i++)
            {
                var item = new Item
                {
                    ID = $"large{i}",
                    Content = $"Large test item {i}",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                };
                items.Add(item);

                // Exclude the first 7 items
                if (i <= excludeCount)
                {
                    excludeIds.Add(item.ID);
                }
            }

            await _store.UpsertBulkAsync(items);

            // Act
            var results = await _store.GetByUserIdAsync(userId, excludeIds);

            // Assert - should return only the non-excluded items (3 items)
            Assert.Equal(totalItems - excludeCount, results.Count);

            // Verify excluded items are not present
            foreach (var excludeId in excludeIds)
            {
                Assert.DoesNotContain(results, r => r.ID == excludeId);
            }

            // Verify non-excluded items are present
            for (int i = excludeCount + 1; i <= totalItems; i++)
            {
                Assert.Contains(results, r => r.ID == $"large{i}");
            }
        }

        [Fact]
        public async Task GetByUserIdAsync_WithExcludeIds_ShouldRespectUserFiltering()
        {
            // Arrange
            var user1 = "user1-exclude-test";
            var user2 = "user2-exclude-test";

            var user1Items = new List<Item>
            {
                new Item { ID = "u1item1", Content = "User 1 Item 1", LastModified = DateTime.UtcNow, OIID = user1, Type = "Item" },
                new Item { ID = "u1item2", Content = "User 1 Item 2", LastModified = DateTime.UtcNow, OIID = user1, Type = "Item" }
            };

            var user2Items = new List<Item>
            {
                new Item { ID = "u2item1", Content = "User 2 Item 1", LastModified = DateTime.UtcNow, OIID = user2, Type = "Item" },
                new Item { ID = "u2item2", Content = "User 2 Item 2", LastModified = DateTime.UtcNow, OIID = user2, Type = "Item" }
            };

            await _store.UpsertBulkAsync(user1Items);
            await _store.UpsertBulkAsync(user2Items);

            // Act - exclude user1's first item, but query should only affect user1's results
            var excludeIds = new HashSet<string> { "u1item1", "u2item1" }; // Include both users' items in exclude
            var user1Results = await _store.GetByUserIdAsync(user1, excludeIds);
            var user2Results = await _store.GetByUserIdAsync(user2, excludeIds);

            // Assert
            // User1 should have only u1item2 (u1item1 excluded)
            Assert.Single(user1Results);
            Assert.Contains(user1Results, r => r.ID == "u1item2");
            Assert.DoesNotContain(user1Results, r => r.ID == "u1item1");

            // User2 should have only u2item2 (u2item1 excluded) 
            Assert.Single(user2Results);
            Assert.Contains(user2Results, r => r.ID == "u2item2");
            Assert.DoesNotContain(user2Results, r => r.ID == "u2item1");

            // No cross-user contamination
            Assert.All(user1Results, r => Assert.Equal(user1, r.OIID));
            Assert.All(user2Results, r => Assert.Equal(user2, r.OIID));
        }
    }
}