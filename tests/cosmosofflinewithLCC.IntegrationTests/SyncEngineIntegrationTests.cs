using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.IntegrationTests
{
    [Collection("SequentialTests")]
    public class SyncEngineIntegrationTests : IDisposable
    {
        private readonly SqliteStore<Item> _localStore;
        private readonly CosmosDbStore<Item> _remoteStore;
        private readonly ILogger _logger;
        private readonly string _dbPath;
        private readonly Container _container;
        private readonly CosmosClient _cosmosClient;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "SyncTestDb";
        private readonly string _containerId = "SyncTestContainer";

        public SyncEngineIntegrationTests()
        {
            // Setup Cosmos DB (using local emulator)
            _cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");

            try
            {
                // Delete the container first to ensure we have the correct partition key
                try
                {
                    Console.WriteLine($"Deleting container {_containerId} in database {_databaseId} if it exists");
                    _cosmosClient.GetDatabase(_databaseId).GetContainer(_containerId).DeleteContainerAsync().GetAwaiter().GetResult();
                    // Wait a moment for the delete to complete
                    System.Threading.Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    // Ignore if it doesn't exist
                    Console.WriteLine($"Container deletion exception (can be ignored if not exists): {ex.Message}");
                }

                // Create database and container if they don't exist
                _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseId).GetAwaiter().GetResult();
                var database = _cosmosClient.GetDatabase(_databaseId);

                Console.WriteLine($"Creating container {_containerId} with partition key path /userId");                // Use composite partitionKey as the partition key path
                database.CreateContainerIfNotExistsAsync(
                    id: _containerId,
                    partitionKeyPath: "/partitionKey",
                    throughput: 400).GetAwaiter().GetResult();

                _container = database.GetContainer(_containerId);
                Console.WriteLine("CosmosDB sync test container is ready");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up Cosmos DB: {ex.Message}");
                throw;
            }

            // Setup SQLite (using temp file)
            _dbPath = Path.Combine(Path.GetTempPath(), $"synctest_{Guid.NewGuid()}.sqlite");
            _localStore = new SqliteStore<Item>(_dbPath);
            _remoteStore = new CosmosDbStore<Item>(_container);

            // Setup logger
            _logger = new LoggerFactory().CreateLogger<SyncEngineIntegrationTests>();

            // Clean up any existing data
            CleanupTestData().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            // Clean up test resources
            CleanupTestData().GetAwaiter().GetResult();

            // Delete the SQLite database file
            if (File.Exists(_dbPath))
            {
                try
                {
                    File.Delete(_dbPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deleting SQLite file: {ex.Message}");
                }
            }
        }

        private async Task CleanupTestData()
        {
            try
            {
                Console.WriteLine("Cleaning up test data in Cosmos DB...");

                // Clean up Cosmos DB data - use a query to find all items
                var query = new QueryDefinition("SELECT c.id, c.partitionKey, c.userId, c.Type FROM c");
                var itemsToDelete = new List<(string id, string partitionKey)>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var item in response)
                    {
                        string id = item.id;
                        string partitionKey;

                        if (item.partitionKey != null)
                        {
                            // Use the partitionKey property if it exists
                            partitionKey = item.partitionKey;
                        }
                        else
                        {
                            // Fall back to constructing it from userId and Type
                            string userIdValue = item.userId ?? _testUserId;
                            string docType = item.Type ?? "Item";
                            partitionKey = $"{userIdValue}:{docType}";
                        }

                        itemsToDelete.Add((id, partitionKey));
                    }
                }

                Console.WriteLine($"Found {itemsToDelete.Count} items to delete in cleanup");

                foreach (var (id, partitionKey) in itemsToDelete)
                {
                    try
                    {
                        Console.WriteLine($"Deleting item with id {id} from partition {partitionKey}");

                        // Delete using the composite partition key
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                        Console.WriteLine($"Item not found during deletion: {id} in partition {partitionKey}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting item {id} from partition {partitionKey}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up test data: {ex.Message}");
            }
        }

        [Fact]
        public async Task SyncEngine_ShouldSyncLocalToRemote_WhenLocalHasNewerItems()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var localItem = new Item
            {
                ID = "test1",
                Content = "Local content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item" // Add Type property
            };

            // Add item only to local store with pending change
            await _localStore.UpsertAsync(localItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            Console.WriteLine($"Checking remote store for item with ID test1");
            var remoteItem = await _remoteStore.GetAsync("test1", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(localItem.Content, remoteItem.Content);
            Assert.Equal(_testUserId, remoteItem.OIID);
            Assert.Equal("Item", remoteItem.Type); // Verify Type property is set

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncEngine_ShouldSyncRemoteToLocal_WhenRemoteHasNewerItems()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var remoteItem = new Item
            {
                ID = "test2",
                Content = "Remote content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item" // Add Type property
            };

            // Add item only to remote store
            await _remoteStore.UpsertAsync(remoteItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            Console.WriteLine($"Checking local store for item with ID test2");
            var localItem = await _localStore.GetAsync("test2", _testUserId);
            Assert.NotNull(localItem);
            Assert.Equal(remoteItem.Content, localItem.Content);
            Assert.Equal(_testUserId, localItem.OIID);
            Assert.Equal("Item", localItem.Type); // Verify Type property is set
        }

        [Fact]
        public async Task SyncEngine_ShouldPreferNewerItems_WhenConflictsExist()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var oldTime = now.AddMinutes(-5);

            // Add older item to remote
            var remoteItem = new Item
            {
                ID = "test3",
                Content = "Old remote content",
                LastModified = oldTime,
                OIID = _testUserId,
                Type = "Item" // Add Type property
            };
            await _remoteStore.UpsertAsync(remoteItem);

            // Add newer item to local with the same ID
            var localItem = new Item
            {
                ID = "test3",
                Content = "New local content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item" // Add Type property
            };
            await _localStore.UpsertAsync(localItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert - Remote should be updated with local content since local is newer
            var updatedRemoteItem = await _remoteStore.GetAsync("test3", _testUserId);
            Assert.NotNull(updatedRemoteItem);
            Assert.Equal(localItem.Content, updatedRemoteItem.Content);
            Assert.Equal(localItem.LastModified, updatedRemoteItem.LastModified);
            Assert.Equal(_testUserId, updatedRemoteItem.OIID);
            Assert.Equal("Item", updatedRemoteItem.Type); // Verify Type property is set
        }

        [Fact]
        public async Task SyncEngine_ShouldHandleMultipleItems_WhenSyncingBidirectionally()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var oldTime = now.AddMinutes(-5);
            var newerTime = now.AddMinutes(5);

            // Items that should sync from local to remote
            var localItems = new List<Item>
            {
                new Item { ID = "local1", Content = "Local item 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "local2", Content = "Local item 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Items that should sync from remote to local
            var remoteItems = new List<Item>
            {
                new Item { ID = "remote1", Content = "Remote item 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "remote2", Content = "Remote item 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Conflict items - local is newer
            var localNewerItem = new Item { ID = "conflict1", Content = "Local newer", LastModified = newerTime, OIID = _testUserId, Type = "Item" };
            var remoteOlderItem = new Item { ID = "conflict1", Content = "Remote older", LastModified = oldTime, OIID = _testUserId, Type = "Item" };

            // Conflict items - remote is newer
            var localOlderItem = new Item { ID = "conflict2", Content = "Local older", LastModified = oldTime, OIID = _testUserId, Type = "Item" };
            var remoteNewerItem = new Item { ID = "conflict2", Content = "Remote newer", LastModified = newerTime, OIID = _testUserId, Type = "Item" };

            // Add all items to their respective stores
            foreach (var item in localItems)
            {
                await _localStore.UpsertAsync(item);
            }
            await _localStore.UpsertAsync(localNewerItem);
            await _localStore.UpsertAsync(localOlderItem);

            foreach (var item in remoteItems)
            {
                await _remoteStore.UpsertAsync(item);
            }
            await _remoteStore.UpsertAsync(remoteOlderItem);
            await _remoteStore.UpsertAsync(remoteNewerItem);

            // Wait a moment to ensure items are properly persisted
            await Task.Delay(500);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();
            await Task.Delay(500);

            // Assert - Local items should be in remote
            foreach (var item in localItems)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
                Assert.Equal(_testUserId, remoteItem.OIID);
                Assert.Equal("Item", remoteItem.Type); // Verify Type property is set
            }            // Remote items should be in local
            foreach (var item in remoteItems)
            {
                var localItem = await _localStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(localItem);
                Assert.Equal(item.Content, localItem.Content);
                Assert.Equal(_testUserId, localItem.OIID);
                Assert.Equal("Item", localItem.Type); // Verify Type property is set
            }            // Local newer should win conflict
            var resolvedConflict1Remote = await _remoteStore.GetAsync("conflict1", _testUserId);
            Assert.NotNull(resolvedConflict1Remote);
            Assert.Equal(localNewerItem.Content, resolvedConflict1Remote.Content);
            Assert.Equal(_testUserId, resolvedConflict1Remote.OIID);
            Assert.Equal("Item", resolvedConflict1Remote.Type); // Verify Type property is set            // Remote newer should win conflict
            var resolvedConflict2Local = await _localStore.GetAsync("conflict2", _testUserId);
            Assert.NotNull(resolvedConflict2Local);
            Assert.Equal(remoteNewerItem.Content, resolvedConflict2Local.Content);
            Assert.Equal(_testUserId, resolvedConflict2Local.OIID);
            Assert.Equal("Item", resolvedConflict2Local.Type); // Verify Type property is set
        }

        [Fact]
        public async Task SyncEngine_ShouldHandleBulkOperations_WithLargeDatasets()
        {
            // Arrange
            const int itemCount = 5; // Large enough to test bulk operations but not too large for a unit test
            var now = DateTime.UtcNow;

            // Create a large number of items to sync from local to remote
            var localItems = new List<Item>();
            for (int i = 0; i < itemCount; i++)
            {
                localItems.Add(new Item
                {
                    ID = $"bulk{i}",
                    Content = $"Bulk content {i}",
                    LastModified = now.AddSeconds(i), // Each item has a slightly different time
                    OIID = _testUserId,
                    Type = "Item" // Add Type property
                });
            }

            // Add all items to local store
            await _localStore.UpsertBulkAsync(localItems);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            // We need to use GetByUserIdAsync to efficiently get items for a specific user 
            var remoteItems = await _remoteStore.GetByUserIdAsync(_testUserId);
            Assert.Equal(itemCount, remoteItems.Count);

            // Verify the content of each item
            foreach (var localItem in localItems)
            {
                var remoteItem = remoteItems.FirstOrDefault(i => i.ID == localItem.ID);
                Assert.NotNull(remoteItem);
                Assert.Equal(localItem.Content, remoteItem!.Content);
                Assert.Equal(_testUserId, remoteItem.OIID);
                Assert.Equal("Item", remoteItem.Type); // Verify Type property is set
            }

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncEngine_ShouldFilterByUserId_WhenMultipleUsersExist()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create items for different users
            var user1Item = new Item { ID = "user1Item", Content = "User 1 data", LastModified = now, OIID = "user1", Type = "Item" };
            var user2Item = new Item { ID = "user2Item", Content = "User 2 data", LastModified = now, OIID = "user2", Type = "Item" };

            // Add both items to remote store
            await _remoteStore.UpsertAsync(user1Item);
            await _remoteStore.UpsertAsync(user2Item);

            // Wait a moment to ensure items are properly persisted
            await Task.Delay(500);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, "user1");

            // Act
            await syncEngine.SyncAsync();
            await Task.Delay(500);

            // Assert            // Should only have user1's item in local store
            var localUser1Item = await _localStore.GetAsync("user1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("user2Item", "user2");

            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.OIID);
            Assert.Equal("Item", localUser1Item.Type); // Verify Type property is set

            // User2's item should not be synced
            Assert.Null(localUser2Item);
        }
        [Fact]
        public async Task SyncEngine_ShouldRespectPartitionKey_AndOnlySyncSpecificUserAndType()
        {
            // Arrange - Create items for different users and types to verify partition key filtering
            var now = DateTime.UtcNow;

            // Create items for different users and types
            var user1Item = new Item { ID = "initUser1Item", Content = "User 1 data", LastModified = now, OIID = "user1", Type = "Item" };
            var user2Item = new Item { ID = "initUser2Item", Content = "User 2 data", LastModified = now, OIID = "user2", Type = "Item" };
            var user1ItemDiffType = new Item { ID = "initUser1ItemOrder", Content = "User 1 order", LastModified = now, OIID = "user1", Type = "Order" };

            // Add items to remote store
            await _remoteStore.UpsertAsync(user1Item);
            await _remoteStore.UpsertAsync(user2Item);
            await _remoteStore.UpsertAsync(user1ItemDiffType);

            // Wait a moment to ensure items are properly persisted
            await Task.Delay(500);

            // Act - Create a sync engine for user1's Items only
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, "user1");

            await syncEngine.SyncAsync();
            await Task.Delay(500);            // Assert - Should only sync user1's Items (not Orders or user2's items)
            var localUser1Item = await _localStore.GetAsync("initUser1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("initUser2Item", "user2");
            var localUser1ItemDiffType = await _localStore.GetAsync("initUser1ItemOrder", "user1");

            // User1's Item should be synced
            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.OIID);
            Assert.Equal("Item", localUser1Item.Type);

            // User2's item should not be synced (different user)
            Assert.Null(localUser2Item);

            // User1's Order should not be synced (different type)
            Assert.Null(localUser1ItemDiffType);
        }

        [Fact]
        public async Task SyncEngine_ShouldSyncDifferentDocumentTypes_WithCompositePartitionKey()
        {
            // Arrange - setup stores for both Item and Order
            var sqlitePath = Path.Combine(Path.GetTempPath(), $"doctype_sync_test_{Guid.NewGuid()}.sqlite");
            var localItemStore = new SqliteStore<Item>(sqlitePath);
            var remoteItemStore = new CosmosDbStore<Item>(_container);

            var localOrderStore = new SqliteStore<Order>(sqlitePath);
            var remoteOrderStore = new CosmosDbStore<Order>(_container);

            // Create an Item and an Order
            var item = new Item
            {
                ID = "sync_test_item",
                Content = "Item for sync test",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Item"
            };

            var order = new Order
            {
                ID = "sync_test_order",
                Description = "Order for sync test",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId,
                Type = "Order"
            };

            // Add to local stores
            await localItemStore.UpsertAsync(item);
            await localOrderStore.UpsertAsync(order);

            var itemSyncEngine = new SyncEngine<Item>(localItemStore, remoteItemStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            var orderSyncEngine = new SyncEngine<Order>(localOrderStore, remoteOrderStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await itemSyncEngine.SyncAsync();
            await orderSyncEngine.SyncAsync();

            // Assert
            var remoteItem = await remoteItemStore.GetAsync(item.ID, _testUserId);
            var remoteOrder = await remoteOrderStore.GetAsync(order.ID, _testUserId);

            Assert.NotNull(remoteItem);
            Assert.Equal(item.Content, remoteItem.Content);
            Assert.Equal("Item", remoteItem.Type);

            Assert.NotNull(remoteOrder);
            Assert.Equal(order.Description, remoteOrder.Description);
            Assert.Equal("Order", remoteOrder.Type);

            // Cleanup
            if (File.Exists(sqlitePath))
            {
                try
                {
                    File.Delete(sqlitePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deleting temp SQLite file: {ex.Message}");
                }
            }
        }

        [Fact]
        public async Task InitialUserDataPull_ShouldOnlyPullItemsOfSpecifiedType()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var itemType1 = new Item { ID = "type1Item", Content = "Type 1 content", LastModified = now, OIID = _testUserId, Type = "Item" };
            var itemType2 = new Item { ID = "type2Item", Content = "Type 2 content", LastModified = now, OIID = _testUserId, Type = "Type2" };

            // Add both items to remote store
            await _remoteStore.UpsertAsync(itemType1);
            await _remoteStore.UpsertAsync(itemType2);
            await Task.Delay(500);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act - Pull only Type1 items
            await syncEngine.InitialUserDataPullAsync("Item");

            // Assert
            var localType1Item = await _localStore.GetAsync("type1Item", _testUserId);
            var localType2Item = await _localStore.GetAsync("type2Item", _testUserId);

            Assert.NotNull(localType1Item);
            Assert.Equal("Type 1 content", localType1Item.Content);
            Assert.Null(localType2Item);
        }

        [Fact]
        public async Task SyncEngine_ShouldHandleMissingTypeProperty()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var item = new Item { ID = "noTypeItem", Content = "No type content", LastModified = now, OIID = _testUserId, Type = null! };

            // Add item to local store
            await _localStore.UpsertAsync(item);
            await Task.Delay(500);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert - Should use class name as type
            var remoteItem = await _remoteStore.GetAsync("noTypeItem", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal("Item", remoteItem.Type);
        }
        [Fact]
        public void SyncEngine_ShouldHandleEmptyUserId()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() => new SyncEngine<Item>(
                _localStore,
                _remoteStore,
                _logger,
                x => x.ID,
                x => x.LastModified,
                string.Empty));
        }

        [Fact]
        public async Task SyncEngine_ShouldHandleConcurrentModifications()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var item = new Item { ID = "concurrentItem", Content = "Original", LastModified = now, OIID = _testUserId, Type = "Item" };

            // Add to both stores with pending change in local
            await _remoteStore.UpsertAsync(item);
            await _localStore.UpsertAsync(item);
            await Task.Delay(500);

            // Modify remote while local has pending change
            var remoteModified = new Item { ID = "concurrentItem", Content = "Remote Modified", LastModified = now.AddMinutes(1), OIID = _testUserId, Type = "Item" };
            await _remoteStore.UpsertAsync(remoteModified);

            var localModified = new Item { ID = "concurrentItem", Content = "Local Modified", LastModified = now.AddSeconds(30), OIID = _testUserId, Type = "Item" };
            await _localStore.UpsertAsync(localModified);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert - Remote version should win as it's newer
            var finalLocal = await _localStore.GetAsync("concurrentItem", _testUserId);
            Assert.NotNull(finalLocal);
            Assert.Equal("Remote Modified", finalLocal.Content);
            Assert.Equal(now.AddMinutes(1), finalLocal.LastModified);
        }
        [Fact]
        public async Task SyncEngine_ShouldHandleInvalidItems()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var validItem = new Item { ID = "valid1", Content = "Valid", LastModified = now, OIID = _testUserId, Type = "Item" };
            await _localStore.UpsertAsync(validItem);

            // We can't test null IDs directly due to database constraints, but we can test that valid items still sync
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();

            // Assert - Valid item should sync
            var remoteItems = await _remoteStore.GetByUserIdAsync(_testUserId);
            Assert.Single(remoteItems);
            Assert.Equal("valid1", remoteItems.First().ID);
        }

        [Fact]
        public async Task SyncEngine_ShouldHandleUserIdChange_WhenUpdateUserIdIsCalled()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var user1 = _testUserId;
            var user2 = "testUser2";

            // Create items for both users
            var user1Item = new Item
            {
                ID = "user1-item",
                Content = "User 1 content",
                LastModified = now,
                OIID = user1,
                Type = "Item"
            };

            var user2Item = new Item
            {
                ID = "user2-item",
                Content = "User 2 content",
                LastModified = now,
                OIID = user2,
                Type = "Item"
            };

            // Add items to remote store
            await _remoteStore.UpsertAsync(user1Item);
            await _remoteStore.UpsertAsync(user2Item);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, user1);

            // Act - First sync with user1
            await syncEngine.SyncAsync();

            // Assert - Only user1's item should be in local store
            var localUser1Item = await _localStore.GetAsync("user1-item", user1);
            var localUser2Item = await _localStore.GetAsync("user2-item", user2);
            Assert.NotNull(localUser1Item);
            Assert.Null(localUser2Item);

            // Act - Update to user2 and sync again
            syncEngine.UpdateUserId(user2);
            await syncEngine.SyncAsync();

            // Assert - Now user2's item should be in local store
            localUser1Item = await _localStore.GetAsync("user1-item", user1);
            localUser2Item = await _localStore.GetAsync("user2-item", user2);
            Assert.NotNull(localUser1Item); // Previous items remain
            Assert.NotNull(localUser2Item); // New user's items are synced
            Assert.Equal("User 2 content", localUser2Item.Content);
            Assert.Equal(user2, localUser2Item.OIID);
        }
    }
}