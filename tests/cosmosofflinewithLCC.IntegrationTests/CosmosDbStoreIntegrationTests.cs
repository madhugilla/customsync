using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;

namespace cosmosofflinewithLCC.IntegrationTests
{
    [Collection("SequentialTests")]
    public class CosmosDbStoreIntegrationTests : IDisposable
    {
        private readonly CosmosDbStore<Item> _store;
        private readonly TestCosmosClientFactory _clientFactory;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "SyncTestDB"; // Match Azure Function configuration
        private readonly string _containerId = "SyncTestContainer"; // Match Azure Function configuration
        private readonly string _azureFunctionUrl = "http://localhost:7071/api/GetCosmosToken";
        private readonly string _cosmosEndpoint = "https://localhost:8081"; // Cosmos DB emulator endpoint

        public CosmosDbStoreIntegrationTests()
        {
            Console.WriteLine("Setting up CosmosDbStore integration tests with HTTP token provider...");

            try
            {
                // Create the HTTP token-based client factory
                _clientFactory = new TestCosmosClientFactory(_azureFunctionUrl, _testUserId, _cosmosEndpoint);

                // Create the store using the token-based factory
                _store = new CosmosDbStore<Item>(_clientFactory, _databaseId, _containerId);

                Console.WriteLine("HTTP token-based CosmosDB store is ready");
                Console.WriteLine($"Using Azure Function: {_azureFunctionUrl}");
                Console.WriteLine($"Using Cosmos endpoint: {_cosmosEndpoint}");
                Console.WriteLine($"Target database: {_databaseId}, container: {_containerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up HTTP token-based Cosmos DB: {ex.Message}");
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
                // Get a container instance through the client factory
                var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);

                // Use a query to find all items (regardless of partition key)
                var query = new QueryDefinition("SELECT c.id, c.partitionKey FROM c");
                var itemsToDelete = new List<(string id, string partitionKey)>();

                using var iterator = container.GetItemQueryIterator<dynamic>(query);
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
                        // Get a fresh container for each delete operation (fresh token)
                        var deleteContainer = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
                        Console.WriteLine($"Deleting item with id {id} from partition {partitionKey}");
                        await deleteContainer.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
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
                // Don't fail the test setup if cleanup fails
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
            var orderClientFactory = new TestCosmosClientFactory(_azureFunctionUrl, _testUserId, _cosmosEndpoint);
            var orderStore = new CosmosDbStore<Order>(orderClientFactory, _databaseId, _containerId);

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
    }
}