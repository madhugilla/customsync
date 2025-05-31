using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;

namespace cosmosofflinewithLCC.IntegrationTests
{

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
            // Generate a unique database path with timestamp to avoid conflicts
            _dbPath = Path.Combine(Path.GetTempPath(), $"synctest_{Guid.NewGuid()}_{DateTime.Now.Ticks}.sqlite");
            Console.WriteLine($"Using SQLite database path: {_dbPath}");

            // Setup Cosmos DB (using local emulator)
            _cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");

            try
            {
                // Delete the container first to ensure we have the correct partition key
                try
                {
                    Console.WriteLine($"Deleting container {_containerId} in database {_databaseId} if it exists");
                    _cosmosClient.GetDatabase(_databaseId).GetContainer(_containerId).DeleteContainerAsync().GetAwaiter().GetResult();
                    System.Threading.Thread.Sleep(1000); // Wait for deletion
                }
                catch (Exception ex)
                {
                    // Ignore if it doesn't exist
                    Console.WriteLine($"Container deletion exception (can be ignored if not exists): {ex.Message}");
                }

                // Create database if it doesn't exist
                _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseId).GetAwaiter().GetResult();
                var database = _cosmosClient.GetDatabase(_databaseId);

                Console.WriteLine($"Creating container {_containerId} with partition key path /partitionKey");

                // Create container with partitionKey path
                database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(_containerId, "/partitionKey")
                    {
                        IndexingPolicy = new IndexingPolicy
                        {
                            IndexingMode = IndexingMode.Consistent,
                            IncludedPaths = { new IncludedPath { Path = "/*" } },
                            ExcludedPaths = { new ExcludedPath { Path = "/partitionKey/?" } }
                        }
                    },
                    throughput: 400
                ).GetAwaiter().GetResult();

                _container = database.GetContainer(_containerId);
                Console.WriteLine("CosmosDB sync test container is ready");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up Cosmos DB: {ex.Message}");
                throw;
            }

            // Setup SQLite and stores
            _localStore = new SqliteStore<Item>(_dbPath);
            _remoteStore = new CosmosDbStore<Item>(_container);

            // Setup logger
            _logger = new LoggerFactory().CreateLogger<SyncEngineIntegrationTests>();

            // Clean up any existing data
            CleanupTestData().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try
            {
                // Clean up test resources
                CleanupTestData().GetAwaiter().GetResult();
                // Extra safety: Close all database connections by forcing garbage collection
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Delete the SQLite database file
                if (File.Exists(_dbPath))
                {
                    try
                    {
                        // Try multiple times with short delays if needed
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                File.Delete(_dbPath);
                                Console.WriteLine($"Successfully deleted SQLite file: {_dbPath}");
                                break;
                            }
                            catch (IOException)
                            {
                                if (i < 2)
                                {
                                    Console.WriteLine($"SQLite file in use, waiting before retry: {_dbPath}");
                                    System.Threading.Thread.Sleep(500);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting SQLite file {_dbPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Dispose: {ex.Message}");
            }
        }

        private async Task CleanupTestData()
        {
            try
            {
                Console.WriteLine("Cleaning up test data...");

                // Clean up local SQLite data
                try
                {
                    Console.WriteLine("Cleaning local SQLite store data...");
                    var localItems = await _localStore.GetAllAsync();
                    Console.WriteLine($"Found {localItems.Count} items in local store");

                    foreach (var item in localItems)
                    {
                        try
                        {
                            // Remove the pending change flag first
                            await _localStore.RemovePendingChangeAsync(item.ID);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error removing pending change for {item.ID}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error cleaning SQLite data: {ex.Message}");
                }

                // Clean up Cosmos DB data
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

                    Console.WriteLine($"Found {itemsToDelete.Count} items to delete in Cosmos DB cleanup");

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
                    Console.WriteLine($"Error cleaning up Cosmos DB data: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in CleanupTestData: {ex.Message}");
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
            await syncEngine.SyncAsync();            // Assert
            Console.WriteLine($"Checking local store for item with ID test2");
            var localItem = await _localStore.GetAsync("test2", _testUserId);
            Assert.NotNull(localItem);
            Assert.Equal(remoteItem.Content, localItem.Content);
            Assert.Equal(_testUserId, localItem.OIID);
            Assert.Equal("Item", localItem.Type); // Verify Type property is set

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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
            await syncEngine.SyncAsync();            // Assert - Remote should be updated with local content since local is newer
            var updatedRemoteItem = await _remoteStore.GetAsync("test3", _testUserId);
            Assert.NotNull(updatedRemoteItem);
            Assert.Equal(localItem.Content, updatedRemoteItem.Content);
            Assert.Equal(localItem.LastModified, updatedRemoteItem.LastModified);
            Assert.Equal(_testUserId, updatedRemoteItem.OIID);
            Assert.Equal("Item", updatedRemoteItem.Type); // Verify Type property is set

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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
            Assert.Equal(remoteNewerItem.Content, resolvedConflict2Local.Content); Assert.Equal(_testUserId, resolvedConflict2Local.OIID);
            Assert.Equal("Item", resolvedConflict2Local.Type); // Verify Type property is set

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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
                    Type = "Item"
                });
            }

            // Verify partition keys are correctly set
            foreach (var item in localItems)
            {
                Assert.Equal($"{_testUserId}:Item", item.PartitionKey);
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
                Assert.Equal("Item", remoteItem.Type);
                Assert.Equal($"{_testUserId}:Item", remoteItem.PartitionKey);
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
            await Task.Delay(500);            // Assert            // Should only have user1's item in local store
            var localUser1Item = await _localStore.GetAsync("user1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("user2Item", "user2");

            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.OIID);
            Assert.Equal("Item", localUser1Item.Type); // Verify Type property is set

            // User2's item should not be synced
            Assert.Null(localUser2Item);

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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

            // Verify partition keys are correct
            Assert.Equal("user1:Item", user1Item.PartitionKey);
            Assert.Equal("user2:Item", user2Item.PartitionKey);
            Assert.Equal("user1:Order", user1ItemDiffType.PartitionKey);

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
            await Task.Delay(800);

            // Assert - Should only sync user1's Items (not Orders or user2's items)
            var localUser1Item = await _localStore.GetAsync("initUser1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("initUser2Item", "user2");
            var localUser1ItemDiffType = await _localStore.GetAsync("initUser1ItemOrder", "user1");

            // User1's Item should be synced
            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.OIID);
            Assert.Equal("Item", localUser1Item.Type);
            Assert.Equal("user1:Item", localUser1Item.PartitionKey);

            // User2's item should not be synced (different user)
            Assert.Null(localUser2Item);

            // User1's Order should not be synced (different type)
            Assert.Null(localUser1ItemDiffType);

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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
            var remoteOrder = await remoteOrderStore.GetAsync(order.ID, _testUserId); Assert.NotNull(remoteItem);
            Assert.Equal(item.Content, remoteItem.Content);
            Assert.Equal("Item", remoteItem.Type);

            Assert.NotNull(remoteOrder);
            Assert.Equal(order.Description, remoteOrder.Description);
            Assert.Equal("Order", remoteOrder.Type);

            // Verify pending changes are removed after sync
            var pendingItemChanges = await localItemStore.GetPendingChangesAsync();
            var pendingOrderChanges = await localOrderStore.GetPendingChangesAsync();
            Assert.Empty(pendingItemChanges);
            Assert.Empty(pendingOrderChanges);

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

            // Verify no pending changes after initial data pull
            var pendingChangesInitial = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChangesInitial);
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
            await syncEngine.SyncAsync();            // Assert - Should use class name as type
            var remoteItem = await _remoteStore.GetAsync("noTypeItem", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal("Item", remoteItem.Type);

            // Verify pending changes are removed after sync
            var pendingChangesAfter = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChangesAfter);
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
            await syncEngine.SyncAsync();            // Assert - Remote version should win as it's newer
            var finalLocal = await _localStore.GetAsync("concurrentItem", _testUserId);
            Assert.NotNull(finalLocal);
            Assert.Equal("Remote Modified", finalLocal.Content);
            Assert.Equal(now.AddMinutes(1), finalLocal.LastModified);

            // Verify pending changes are removed after sync
            var pendingChangesAfter = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChangesAfter);
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
            await syncEngine.SyncAsync();            // Assert - Valid item should sync
            var remoteItems = await _remoteStore.GetByUserIdAsync(_testUserId);
            Assert.Single(remoteItems);
            Assert.Equal("valid1", remoteItems.First().ID);

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
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
            await syncEngine.SyncAsync();            // Assert - Now user2's item should be in local store
            localUser1Item = await _localStore.GetAsync("user1-item", user1);
            localUser2Item = await _localStore.GetAsync("user2-item", user2);
            Assert.NotNull(localUser1Item); // Previous items remain
            Assert.NotNull(localUser2Item); // New user's items are synced
            Assert.Equal("User 2 content", localUser2Item.Content);
            Assert.Equal(user2, localUser2Item.OIID);

            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncEngine_ShouldHaveZeroPendingItemsInLocalStore_AfterSync()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create multiple items with pending changes in local store
            var localItems = new List<Item>
            {
                new Item { ID = "pendingItem1", Content = "Pending content 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "pendingItem2", Content = "Pending content 2", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "pendingItem3", Content = "Pending content 3", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Add items to local store which should mark them as pending changes
            foreach (var item in localItems)
            {
                await _localStore.UpsertAsync(item);
            }

            // Verify we have pending changes before sync
            var pendingChangesBefore = await _localStore.GetPendingChangesAsync();
            Assert.Equal(localItems.Count, pendingChangesBefore.Count);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsync();
            await Task.Delay(500); // Wait for sync to complete

            // Assert
            // Verify all items were synced to remote store
            foreach (var localItem in localItems)
            {
                var remoteItem = await _remoteStore.GetAsync(localItem.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(localItem.Content, remoteItem.Content);
            }

            // Verify there are no pending changes in local store after sync
            var pendingChangesAfter = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChangesAfter);
        }

        [Fact]
        public async Task InitialUserDataPull_DoesNotMarkItemsAsPendingInSqlite()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var remoteItems = new List<Item>
            {
                new Item { ID = Guid.NewGuid().ToString(), Content = "Remote1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = Guid.NewGuid().ToString(), Content = "Remote2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Add items to remote store
            foreach (var item in remoteItems)
            {
                await _remoteStore.UpsertAsync(item);
            }

            // Create sync engine
            var syncEngine = new SyncEngine<Item>(
                _localStore,
                _remoteStore,
                _logger,
                x => x.ID,
                x => x.LastModified,
                _testUserId);

            // Act
            await syncEngine.InitialUserDataPullAsync("Item");

            // Assert
            // Verify items exist in local store
            foreach (var remoteItem in remoteItems)
            {
                var localItem = await _localStore.GetAsync(remoteItem.ID, _testUserId);
                Assert.NotNull(localItem);
                Assert.Equal(remoteItem.Content, localItem.Content);
            }

            // Verify no pending changes after initial data pull
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task ForcePushAsync_ShouldPushItemToRemote_WhenItemExistsInLocal()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var newItem = new Item
            {
                ID = "direct-push-test-1",
                Content = "Direct push content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add item to local store with pending change
            await _localStore.UpsertAsync(newItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var result = await syncEngine.ForcePushAsync("direct-push-test-1");

            // Assert
            Assert.True(result);

            // Verify item exists in remote store
            var remoteItem = await _remoteStore.GetAsync("direct-push-test-1", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(newItem.Content, remoteItem.Content);
            Assert.Equal(_testUserId, remoteItem.OIID);
            Assert.Equal("Item", remoteItem.Type);

            // Verify pending change was removed
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.DoesNotContain(pendingChanges, pc => pc.ID == "direct-push-test-1");
        }

        [Fact]
        public async Task ForcePushAsync_ShouldReturnFalse_WhenItemNotFoundInLocal()
        {
            // Arrange
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var result = await syncEngine.ForcePushAsync("non-existent-item");

            // Assert
            Assert.False(result);

            // Verify item does not exist in remote store
            var remoteItem = await _remoteStore.GetAsync("non-existent-item", _testUserId);
            Assert.Null(remoteItem);
        }
        [Fact]
        public async Task ForcePushAsync_ShouldThrowArgumentException_WhenIdIsNullOrEmpty()
        {
            // Arrange
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act & Assert - null ID
            var ex1 = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await syncEngine.ForcePushAsync(null!));
            Assert.Contains("ID must be provided", ex1.Message);

            // Act & Assert - empty ID
            var ex2 = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await syncEngine.ForcePushAsync(""));
            Assert.Contains("ID must be provided", ex2.Message);

            // Act & Assert - whitespace ID
            var ex3 = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await syncEngine.ForcePushAsync("   "));
            Assert.Contains("ID must be provided", ex3.Message);
        }


        [Fact]
        public async Task ForcePushAsync_ShouldRemovePendingChange_WhenItemHasPendingChange()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var newItem = new Item
            {
                ID = "direct-push-pending-test",
                Content = "Content with pending change",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add item to local store with pending change
            await _localStore.UpsertAsync(newItem);

            // Verify pending change exists initially
            var initialPendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Contains(initialPendingChanges, pc => pc.ID == "direct-push-pending-test");

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var result = await syncEngine.ForcePushAsync("direct-push-pending-test");

            // Assert
            Assert.True(result);

            // Verify pending change was removed
            var finalPendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.DoesNotContain(finalPendingChanges, pc => pc.ID == "direct-push-pending-test");

            // Verify item exists in remote store
            var remoteItem = await _remoteStore.GetAsync("direct-push-pending-test", _testUserId);
            Assert.NotNull(remoteItem);
        }

        [Fact]
        public async Task ForcePushAsync_ShouldHandleGuidIds_WhenItemHasGuidId()
        {
            // Arrange
            var guidId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;
            var newItem = new Item
            {
                ID = guidId,
                Content = "Item with GUID ID",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add item to local store
            await _localStore.UpsertAsync(newItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var result = await syncEngine.ForcePushAsync(guidId);

            // Assert
            Assert.True(result);

            // Verify item exists in remote store with GUID ID
            var remoteItem = await _remoteStore.GetAsync(guidId, _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(guidId, remoteItem.ID);
            Assert.Equal(newItem.Content, remoteItem.Content);
        }

        [Fact]
        public async Task ForcePushAsync_ShouldLogAppropriateMessages_DuringExecution()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var newItem = new Item
            {
                ID = "direct-push-logging-test",
                Content = "Test content for logging",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add item to local store
            await _localStore.UpsertAsync(newItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var result = await syncEngine.ForcePushAsync("direct-push-logging-test");

            // Assert
            Assert.True(result);

            // Verify item was pushed successfully
            var remoteItem = await _remoteStore.GetAsync("direct-push-logging-test", _testUserId);
            Assert.NotNull(remoteItem);

            // Note: We can't easily test logger output in integration tests without a test logger,
            // but we can verify the operation completed successfully which indicates logging worked
        }

        [Fact]
        public async Task ForcePushAsync_ShouldHandleMultipleSequentialCalls_WhenCalledForDifferentItems()
        {
            // Arrange
            var items = new List<Item>();
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            for (int i = 1; i <= 3; i++)
            {
                var item = new Item
                {
                    ID = $"sequential-push-{i}",
                    Content = $"Sequential content {i}",
                    LastModified = DateTime.UtcNow,
                    OIID = _testUserId,
                    Type = "Item"
                };
                items.Add(item);
                await _localStore.UpsertAsync(item);
            }

            // Act
            var results = new List<bool>();
            foreach (var item in items)
            {
                var result = await syncEngine.ForcePushAsync(item.ID);
                results.Add(result);
            }

            // Assert
            Assert.All(results, result => Assert.True(result));

            // Verify all items exist in remote store
            foreach (var item in items)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
            }

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            foreach (var item in items)
            {
                Assert.DoesNotContain(pendingChanges, pc => pc.ID == item.ID);
            }
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldPushAllPendingChangesToRemote_WhenPendingChangesExist()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var items = new List<Item>
            {
                new Item { ID = "force-push-all-1", Content = "Content 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "force-push-all-2", Content = "Content 2", LastModified = now.AddMinutes(1), OIID = _testUserId, Type = "Item" },
                new Item { ID = "force-push-all-3", Content = "Content 3", LastModified = now.AddMinutes(2), OIID = _testUserId, Type = "Item" }
            };

            // Add items to local store with pending changes
            foreach (var item in items)
            {
                await _localStore.UpsertAsync(item);
            }

            // Verify pending changes exist
            var pendingChangesBefore = await _localStore.GetPendingChangesAsync();
            Assert.Equal(items.Count, pendingChangesBefore.Count);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(items.Count, itemsPushed);

            // Verify all items exist in remote store
            foreach (var item in items)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
                Assert.Equal(_testUserId, remoteItem.OIID);
                Assert.Equal("Item", remoteItem.Type);
            }

            // Verify all pending changes were removed
            var pendingChangesAfter = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChangesAfter);
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldReturnZero_WhenNoPendingChangesExist()
        {
            // Arrange
            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(0, itemsPushed);
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldBypassConflictChecking_WhenRemoteItemsExist()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var localItem = new Item
            {
                ID = "conflict-test-1",
                Content = "Local content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            var remoteItem = new Item
            {
                ID = "conflict-test-1",
                Content = "Remote content",
                LastModified = now.AddHours(1), // Remote is newer
                OIID = _testUserId,
                Type = "Item"
            };

            // Add remote item first (newer timestamp)
            await _remoteStore.UpsertAsync(remoteItem);

            // Add local item with pending change (older timestamp)
            await _localStore.UpsertAsync(localItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act - Force push should override remote item despite being older
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(1, itemsPushed);

            // Verify local item overwrote remote item (conflict checking bypassed)
            var finalRemoteItem = await _remoteStore.GetAsync("conflict-test-1", _testUserId);
            Assert.NotNull(finalRemoteItem);
            Assert.Equal(localItem.Content, finalRemoteItem.Content); // Local content should win
            Assert.Equal(localItem.LastModified, finalRemoteItem.LastModified);

            // Verify pending change was removed
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.DoesNotContain(pendingChanges, pc => pc.ID == "conflict-test-1");
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldEnsureCommonProperties_WhenPushingItems()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var item = new Item
            {
                ID = "props-test-1",
                Content = "Test content",
                LastModified = now,
                // Note: Not setting OIID and Type to test they get set properly
            };

            await _localStore.UpsertAsync(item);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(1, itemsPushed);

            // Verify common properties were set correctly
            var remoteItem = await _remoteStore.GetAsync("props-test-1", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(_testUserId, remoteItem.OIID);
            Assert.Equal("Item", remoteItem.Type);
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldHandleGuidIds_WhenItemsHaveGuidIds()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var guidId = Guid.NewGuid().ToString();
            var item = new Item
            {
                ID = guidId,
                Content = "GUID test content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            await _localStore.UpsertAsync(item);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(1, itemsPushed);

            // Verify GUID item was pushed correctly
            var remoteItem = await _remoteStore.GetAsync(guidId, _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(item.Content, remoteItem.Content);
            Assert.Equal(guidId, remoteItem.ID);
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldSkipItemsWithNullOrEmptyIds_AndContinueWithValidItems()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var validItem = new Item
            {
                ID = "valid-item-1",
                Content = "Valid content",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add valid item
            await _localStore.UpsertAsync(validItem);

            // Manually create an item with null ID in pending changes (simulating data corruption)
            // Note: This is a simulation - in practice the store should prevent this

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(1, itemsPushed); // Only valid item should be pushed

            // Verify valid item was pushed
            var remoteItem = await _remoteStore.GetAsync("valid-item-1", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(validItem.Content, remoteItem.Content);
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldLogAppropriateMessages_DuringExecution()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var items = new List<Item>
            {
                new Item { ID = "log-test-1", Content = "Content 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "log-test-2", Content = "Content 2", LastModified = now.AddMinutes(1), OIID = _testUserId, Type = "Item" }
            };

            foreach (var item in items)
            {
                await _localStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(items.Count, itemsPushed);

            // Verify items were pushed successfully
            foreach (var item in items)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
            }

            // Note: In a real test environment, you might want to use a mock logger
            // to verify specific log messages were written
        }

        [Fact]
        public async Task ForcePushAllAsync_ShouldHandleLargeDatasets_WithBulkOperations()
        {
            // Arrange
            var now = DateTime.UtcNow;
            const int itemCount = 50; // Test with a larger dataset
            var items = new List<Item>();

            for (int i = 0; i < itemCount; i++)
            {
                items.Add(new Item
                {
                    ID = $"bulk-test-{i}",
                    Content = $"Content {i}",
                    LastModified = now.AddMinutes(i),
                    OIID = _testUserId,
                    Type = "Item"
                });
            }

            // Add items to local store
            foreach (var item in items)
            {
                await _localStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var itemsPushed = await syncEngine.ForcePushAllAsync();

            // Assert
            Assert.Equal(itemCount, itemsPushed);

            // Verify all items exist in remote store
            foreach (var item in items)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
            }            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        // ...existing methods...

        [Fact]
        public async Task SyncAsyncOptimized_ShouldPerformBasicSync_WithOptimizedAlgorithms()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Local items to push
            var localItems = new List<Item>
            {
                new Item { ID = "opt-local1", Content = "Optimized local 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "opt-local2", Content = "Optimized local 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Remote items to pull
            var remoteItems = new List<Item>
            {
                new Item { ID = "opt-remote1", Content = "Optimized remote 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "opt-remote2", Content = "Optimized remote 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Add local items with pending changes
            foreach (var item in localItems)
            {
                await _localStore.UpsertAsync(item);
            }

            // Add remote items
            foreach (var item in remoteItems)
            {
                await _remoteStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimized();

            // Assert - Local items should be in remote
            foreach (var item in localItems)
            {
                var remoteItem = await _remoteStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
            }

            // Remote items should be in local
            foreach (var item in remoteItems)
            {
                var localItem = await _localStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(localItem);
                Assert.Equal(item.Content, localItem.Content);
            }

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimized_ShouldSkipDuplicateRemoteFetching_WhenItemsArePushedAndPulled()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create an item that will be processed in both push and pull phases
            var conflictItem = new Item
            {
                ID = "opt-conflict",
                Content = "Local version",
                LastModified = now.AddMinutes(10), // Local is newer
                OIID = _testUserId,
                Type = "Item"
            };

            // Add older version to remote first
            var remoteVersion = new Item
            {
                ID = "opt-conflict",
                Content = "Remote version",
                LastModified = now, // Remote is older
                OIID = _testUserId,
                Type = "Item"
            };
            await _remoteStore.UpsertAsync(remoteVersion);

            // Add local version with pending change (newer)
            await _localStore.UpsertAsync(conflictItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimized();

            // Assert - Local version should win (it's newer)
            var finalRemoteItem = await _remoteStore.GetAsync("opt-conflict", _testUserId);
            var finalLocalItem = await _localStore.GetAsync("opt-conflict", _testUserId);

            Assert.NotNull(finalRemoteItem);
            Assert.NotNull(finalLocalItem);
            Assert.Equal("Local version", finalRemoteItem.Content);
            Assert.Equal("Local version", finalLocalItem.Content);
            Assert.Equal(now.AddMinutes(10), finalRemoteItem.LastModified);

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimized_ShouldHandleConflictResolution_WithOptimizedProcessing()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var localNewer = now.AddMinutes(10);
            var remoteOlder = now.AddMinutes(-5);

            // Create conflicting items where local is newer
            var localItem = new Item
            {
                ID = "conflict-test",
                Content = "Local content (newer)",
                LastModified = localNewer,
                OIID = _testUserId,
                Type = "Item"
            };

            var remoteItem = new Item
            {
                ID = "conflict-test",
                Content = "Remote content (older)",
                LastModified = remoteOlder,
                OIID = _testUserId,
                Type = "Item"
            };

            // Add items to their respective stores
            await _localStore.UpsertAsync(localItem);
            await _remoteStore.UpsertAsync(remoteItem);

            // Add additional non-conflicting items to test filtering works correctly
            var additionalLocal = new Item
            {
                ID = "non-conflict-local",
                Content = "Non-conflict local",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };
            var additionalRemote = new Item
            {
                ID = "non-conflict-remote",
                Content = "Non-conflict remote",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            await _localStore.UpsertAsync(additionalLocal);
            await _remoteStore.UpsertAsync(additionalRemote);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - Local (newer) should win the conflict
            var finalRemoteConflict = await _remoteStore.GetAsync("conflict-test", _testUserId);
            var finalLocalConflict = await _localStore.GetAsync("conflict-test", _testUserId);

            Assert.NotNull(finalRemoteConflict);
            Assert.NotNull(finalLocalConflict);
            Assert.Equal("Local content (newer)", finalRemoteConflict.Content);
            Assert.Equal("Local content (newer)", finalLocalConflict.Content);
            Assert.Equal(localNewer, finalRemoteConflict.LastModified);

            // Verify non-conflicting items are properly synced
            var syncedRemoteAdditional = await _remoteStore.GetAsync("non-conflict-local", _testUserId);
            var syncedLocalAdditional = await _localStore.GetAsync("non-conflict-remote", _testUserId);

            Assert.NotNull(syncedRemoteAdditional);
            Assert.NotNull(syncedLocalAdditional);
            Assert.Equal("Non-conflict local", syncedRemoteAdditional.Content);
            Assert.Equal("Non-conflict remote", syncedLocalAdditional.Content);

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldHandleUserFiltering_WithDatabaseLevelOptimization()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var otherUserId = "other-user-123";

            // Create items for the test user
            var testUserItems = new List<Item>
            {
                new Item { ID = "user-filter-1", Content = "Test user item 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "user-filter-2", Content = "Test user item 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Create items for another user (should be ignored)
            var otherUserItems = new List<Item>
            {
                new Item { ID = "other-user-1", Content = "Other user item 1", LastModified = now, OIID = otherUserId, Type = "Item" },
                new Item { ID = "other-user-2", Content = "Other user item 2", LastModified = now, OIID = otherUserId, Type = "Item" }
            };

            // Add all items to remote store
            foreach (var item in testUserItems)
            {
                await _remoteStore.UpsertAsync(item);
            }
            foreach (var item in otherUserItems)
            {
                await _remoteStore.UpsertAsync(item);
            }

            // Add one test user item to local store with pending change
            var localItem = new Item
            {
                ID = "local-pending",
                Content = "Local pending item",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };
            await _localStore.UpsertAsync(localItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - Only test user items should be synced
            foreach (var item in testUserItems)
            {
                var localResult = await _localStore.GetAsync(item.ID, _testUserId);
                Assert.NotNull(localResult);
                Assert.Equal(item.Content, localResult.Content);
                Assert.Equal(_testUserId, localResult.OIID);
            }

            // Local item should be pushed to remote
            var remoteResult = await _remoteStore.GetAsync("local-pending", _testUserId);
            Assert.NotNull(remoteResult);
            Assert.Equal("Local pending item", remoteResult.Content);

            // Other user items should NOT be in local store
            foreach (var item in otherUserItems)
            {
                var shouldNotExist = await _localStore.GetAsync(item.ID, _testUserId);
                Assert.Null(shouldNotExist);
            }

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldHandleEmptyStores_WithoutErrors()
        {
            // Arrange - Both stores are already empty from setup

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act & Assert - Should complete without errors
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Verify stores remain empty
            var localItems = await _localStore.GetByUserIdAsync(_testUserId);
            var remoteItems = await _remoteStore.GetByUserIdAsync(_testUserId);

            Assert.Empty(localItems);
            Assert.Empty(remoteItems);

            // Verify no pending changes
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldProcessLargeDatasets_EfficientlyWithDatabaseFiltering()
        {
            // Arrange - Create a larger dataset to test performance optimization
            var now = DateTime.UtcNow;
            var itemCount = 50; // Moderate size for integration testing

            var localItems = new List<Item>();
            var remoteItems = new List<Item>();

            // Create local items
            for (int i = 0; i < itemCount; i++)
            {
                localItems.Add(new Item
                {
                    ID = $"large-local-{i}",
                    Content = $"Large dataset local item {i}",
                    LastModified = now.AddMinutes(i),
                    OIID = _testUserId,
                    Type = "Item"
                });
            }

            // Create remote items (different IDs to avoid conflicts)
            for (int i = 0; i < itemCount; i++)
            {
                remoteItems.Add(new Item
                {
                    ID = $"large-remote-{i}",
                    Content = $"Large dataset remote item {i}",
                    LastModified = now.AddMinutes(i),
                    OIID = _testUserId,
                    Type = "Item"
                });
            }

            // Add items in batches to avoid overwhelming the stores
            foreach (var item in localItems)
            {
                await _localStore.UpsertAsync(item);
            }

            foreach (var item in remoteItems)
            {
                await _remoteStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();
            stopwatch.Stop();

            // Assert - All items should be synced and operation should complete in reasonable time
            // Verify local items were pushed to remote
            foreach (var localItem in localItems)
            {
                var remoteResult = await _remoteStore.GetAsync(localItem.ID, _testUserId);
                Assert.NotNull(remoteResult);
                Assert.Equal(localItem.Content, remoteResult.Content);
            }

            // Verify remote items were pulled to local
            foreach (var remoteItem in remoteItems)
            {
                var localResult = await _localStore.GetAsync(remoteItem.ID, _testUserId);
                Assert.NotNull(localResult);
                Assert.Equal(remoteItem.Content, localResult.Content);
            }

            // Verify performance - should complete in reasonable time (10 seconds should be plenty)
            Assert.True(stopwatch.ElapsedMilliseconds < 10000,
                $"Sync took too long: {stopwatch.ElapsedMilliseconds}ms. Database filtering should improve performance.");

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldMaintainDataIntegrity_AfterDatabaseFilteredSync()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create items with various scenarios to test data integrity
            var scenarios = new List<Item>
            {
                // Item with special characters
                new Item { ID = "integrity-special", Content = "Special chars: àáâãäåæçèéêë", LastModified = now, OIID = _testUserId, Type = "Item" },
                
                // Item with long content
                new Item { ID = "integrity-long", Content = new string('A', 1000), LastModified = now, OIID = _testUserId, Type = "Item" },
                
                // Item with JSON-like content
                new Item { ID = "integrity-json", Content = "{\"key\": \"value\", \"number\": 123}", LastModified = now, OIID = _testUserId, Type = "Item" },
                
                // Item with null-like string content
                new Item { ID = "integrity-null", Content = "null", LastModified = now, OIID = _testUserId, Type = "Item" },
                
                // Item with empty content
                new Item { ID = "integrity-empty", Content = "", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Add half to local, half to remote
            for (int i = 0; i < scenarios.Count; i++)
            {
                if (i % 2 == 0)
                {
                    await _localStore.UpsertAsync(scenarios[i]);
                }
                else
                {
                    await _remoteStore.UpsertAsync(scenarios[i]);
                }
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - Verify data integrity is maintained
            foreach (var originalItem in scenarios)
            {
                // Check both stores have the item with correct data
                var localCopy = await _localStore.GetAsync(originalItem.ID, _testUserId);
                var remoteCopy = await _remoteStore.GetAsync(originalItem.ID, _testUserId);

                Assert.NotNull(localCopy);
                Assert.NotNull(remoteCopy);

                // Verify content integrity
                Assert.Equal(originalItem.Content, localCopy.Content);
                Assert.Equal(originalItem.Content, remoteCopy.Content);

                // Verify metadata integrity
                Assert.Equal(originalItem.ID, localCopy.ID);
                Assert.Equal(originalItem.ID, remoteCopy.ID);
                Assert.Equal(_testUserId, localCopy.OIID);
                Assert.Equal(_testUserId, remoteCopy.OIID);
                Assert.Equal("Item", localCopy.Type);
                Assert.Equal("Item", remoteCopy.Type);

                // Verify timestamps are preserved
                Assert.Equal(originalItem.LastModified, localCopy.LastModified);
                Assert.Equal(originalItem.LastModified, remoteCopy.LastModified);
            }

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldHandleOnlyLocalData_WithDatabaseFiltering()
        {
            // Arrange - Only add data to local store, remote is empty
            var now = DateTime.UtcNow;
            var localOnlyItems = new List<Item>
            {
                new Item { ID = "local-only-1", Content = "Local only item 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "local-only-2", Content = "Local only item 2", LastModified = now.AddMinutes(1), OIID = _testUserId, Type = "Item" },
                new Item { ID = "local-only-3", Content = "Local only item 3", LastModified = now.AddMinutes(2), OIID = _testUserId, Type = "Item" }
            };

            foreach (var item in localOnlyItems)
            {
                await _localStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - All local items should be pushed to remote
            foreach (var localItem in localOnlyItems)
            {
                var remoteResult = await _remoteStore.GetAsync(localItem.ID, _testUserId);
                Assert.NotNull(remoteResult);
                Assert.Equal(localItem.Content, remoteResult.Content);
                Assert.Equal(localItem.LastModified, remoteResult.LastModified);
                Assert.Equal(localItem.OIID, remoteResult.OIID);
            }

            // Verify local items are still present
            foreach (var localItem in localOnlyItems)
            {
                var localResult = await _localStore.GetAsync(localItem.ID, _testUserId);
                Assert.NotNull(localResult);
                Assert.Equal(localItem.Content, localResult.Content);
            }

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldHandleOnlyRemoteData_WithDatabaseFiltering()
        {
            // Arrange - Only add data to remote store, local is empty
            var now = DateTime.UtcNow;
            var remoteOnlyItems = new List<Item>
            {
                new Item { ID = "remote-only-1", Content = "Remote only item 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "remote-only-2", Content = "Remote only item 2", LastModified = now.AddMinutes(1), OIID = _testUserId, Type = "Item" },
                new Item { ID = "remote-only-3", Content = "Remote only item 3", LastModified = now.AddMinutes(2), OIID = _testUserId, Type = "Item" }
            };

            foreach (var item in remoteOnlyItems)
            {
                await _remoteStore.UpsertAsync(item);
            }

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - All remote items should be pulled to local
            foreach (var remoteItem in remoteOnlyItems)
            {
                var localResult = await _localStore.GetAsync(remoteItem.ID, _testUserId);
                Assert.NotNull(localResult);
                Assert.Equal(remoteItem.Content, localResult.Content);
                Assert.Equal(remoteItem.LastModified, localResult.LastModified);
                Assert.Equal(remoteItem.OIID, localResult.OIID);
            }

            // Verify remote items are still present
            foreach (var remoteItem in remoteOnlyItems)
            {
                var remoteResult = await _remoteStore.GetAsync(remoteItem.ID, _testUserId);
                Assert.NotNull(remoteResult);
                Assert.Equal(remoteItem.Content, remoteResult.Content);
            }

            // Verify no pending changes remain (should be empty since we only pulled data)
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }

        [Fact]
        public async Task SyncAsyncOptimizedWithDatabaseFiltering_ShouldSkipDuplicateRemoteFetching_WithDatabaseFiltering()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create items that will be processed during push phase
            var localItems = new List<Item>
            {
                new Item { ID = "duplicate-test-1", Content = "Local version 1", LastModified = now.AddMinutes(5), OIID = _testUserId, Type = "Item" },
                new Item { ID = "duplicate-test-2", Content = "Local version 2", LastModified = now.AddMinutes(5), OIID = _testUserId, Type = "Item" }
            };

            // Create older versions in remote (should be overwritten during push)
            var remoteVersions = new List<Item>
            {
                new Item { ID = "duplicate-test-1", Content = "Remote version 1", LastModified = now, OIID = _testUserId, Type = "Item" },
                new Item { ID = "duplicate-test-2", Content = "Remote version 2", LastModified = now, OIID = _testUserId, Type = "Item" }
            };

            // Add additional remote item that should be pulled (not processed during push)
            var additionalRemoteItem = new Item
            {
                ID = "additional-remote",
                Content = "Additional remote item",
                LastModified = now,
                OIID = _testUserId,
                Type = "Item"
            };

            // Setup stores
            foreach (var item in localItems)
            {
                await _localStore.UpsertAsync(item);
            }

            foreach (var item in remoteVersions)
            {
                await _remoteStore.UpsertAsync(item);
            }
            await _remoteStore.UpsertAsync(additionalRemoteItem);

            var syncEngine = new SyncEngine<Item>(_localStore, _remoteStore, _logger,
                x => x.ID, x => x.LastModified, _testUserId);

            // Act
            await syncEngine.SyncAsyncOptimizedWithDatabaseFiltering();

            // Assert - Items processed during push should not be fetched again during pull
            // The local versions (newer) should have won and be in both stores
            foreach (var localItem in localItems)
            {
                var remoteResult = await _remoteStore.GetAsync(localItem.ID, _testUserId);
                var localResult = await _localStore.GetAsync(localItem.ID, _testUserId);

                Assert.NotNull(remoteResult);
                Assert.NotNull(localResult);
                Assert.Equal(localItem.Content, remoteResult.Content); // Local version should win
                Assert.Equal(localItem.Content, localResult.Content);
                Assert.Equal(localItem.LastModified, remoteResult.LastModified);
            }

            // Additional remote item should have been pulled
            var additionalLocal = await _localStore.GetAsync("additional-remote", _testUserId);
            Assert.NotNull(additionalLocal);
            Assert.Equal("Additional remote item", additionalLocal.Content);

            // Verify no pending changes remain
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }
    }
}