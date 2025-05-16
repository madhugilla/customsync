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

                Console.WriteLine($"Creating container {_containerId} with partition key path /userId");

                // Use userId as the partition key path
                database.CreateContainerIfNotExistsAsync(
                    id: _containerId,
                    partitionKeyPath: "/userId",
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
                        Console.WriteLine($"Deleting item with id {id} from partition {userId}");

                        // Delete using userId as the partition key
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(userId));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                        Console.WriteLine($"Item not found during deletion: {id} in partition {userId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting item {id} from partition {userId}: {ex.Message}");
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
                Id = "test1",
                Content = "Local content",
                LastModified = now,
                UserId = _testUserId,
                Type = "Item" // Add Type property
            };

            // Add item only to local store with pending change
            await _localStore.UpsertAsync(localItem);

            // Act
            Console.WriteLine($"Starting sync with userId {_testUserId}");
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, _testUserId);

            // Assert
            Console.WriteLine($"Checking remote store for item with ID test1"); var remoteItem = await _remoteStore.GetAsync("test1", _testUserId);
            Assert.NotNull(remoteItem);
            Assert.Equal(localItem.Content, remoteItem.Content);
            Assert.Equal(_testUserId, remoteItem.UserId);
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
                Id = "test2",
                Content = "Remote content",
                LastModified = now,
                UserId = _testUserId,
                Type = "Item" // Add Type property
            };

            // Add item only to remote store
            await _remoteStore.UpsertAsync(remoteItem);

            // Act - Make sure to pass the userId parameter
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, _testUserId);            // Assert - Verify item was synced from remote to local
            var localItem = await _localStore.GetAsync("test2", _testUserId);
            Assert.NotNull(localItem);
            Assert.Equal(remoteItem.Content, localItem.Content);
            Assert.Equal(_testUserId, localItem.UserId);
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
                Id = "test3",
                Content = "Old remote content",
                LastModified = oldTime,
                UserId = _testUserId,
                Type = "Item" // Add Type property
            };
            await _remoteStore.UpsertAsync(remoteItem);

            // Add newer item to local with the same ID
            var localItem = new Item
            {
                Id = "test3",
                Content = "New local content",
                LastModified = now,
                UserId = _testUserId,
                Type = "Item" // Add Type property
            };
            await _localStore.UpsertAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, _testUserId);            // Assert - Remote should be updated with local content since local is newer
            var updatedRemoteItem = await _remoteStore.GetAsync("test3", _testUserId);
            Assert.NotNull(updatedRemoteItem);
            Assert.Equal(localItem.Content, updatedRemoteItem.Content);
            Assert.Equal(localItem.LastModified, updatedRemoteItem.LastModified);
            Assert.Equal(_testUserId, updatedRemoteItem.UserId);
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
                new Item { Id = "local1", Content = "Local item 1", LastModified = now, UserId = _testUserId, Type = "Item" },
                new Item { Id = "local2", Content = "Local item 2", LastModified = now, UserId = _testUserId, Type = "Item" }
            };

            // Items that should sync from remote to local
            var remoteItems = new List<Item>
            {
                new Item { Id = "remote1", Content = "Remote item 1", LastModified = now, UserId = _testUserId, Type = "Item" },
                new Item { Id = "remote2", Content = "Remote item 2", LastModified = now, UserId = _testUserId, Type = "Item" }
            };

            // Conflict items - local is newer
            var localNewerItem = new Item { Id = "conflict1", Content = "Local newer", LastModified = newerTime, UserId = _testUserId, Type = "Item" };
            var remoteOlderItem = new Item { Id = "conflict1", Content = "Remote older", LastModified = oldTime, UserId = _testUserId, Type = "Item" };

            // Conflict items - remote is newer
            var localOlderItem = new Item { Id = "conflict2", Content = "Local older", LastModified = oldTime, UserId = _testUserId, Type = "Item" };
            var remoteNewerItem = new Item { Id = "conflict2", Content = "Remote newer", LastModified = newerTime, UserId = _testUserId, Type = "Item" };

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

            // Act - explicitly pass the test user ID
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, _testUserId);

            // Wait briefly to ensure sync completes
            await Task.Delay(500);

            // Assert            // Local items should be in remote
            foreach (var item in localItems)
            {
                var remoteItem = await _remoteStore.GetAsync(item.Id, _testUserId);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
                Assert.Equal(_testUserId, remoteItem.UserId);
                Assert.Equal("Item", remoteItem.Type); // Verify Type property is set
            }            // Remote items should be in local
            foreach (var item in remoteItems)
            {
                var localItem = await _localStore.GetAsync(item.Id, _testUserId);
                Assert.NotNull(localItem);
                Assert.Equal(item.Content, localItem.Content);
                Assert.Equal(_testUserId, localItem.UserId);
                Assert.Equal("Item", localItem.Type); // Verify Type property is set
            }            // Local newer should win conflict
            var resolvedConflict1Remote = await _remoteStore.GetAsync("conflict1", _testUserId);
            Assert.NotNull(resolvedConflict1Remote);
            Assert.Equal(localNewerItem.Content, resolvedConflict1Remote.Content);
            Assert.Equal(_testUserId, resolvedConflict1Remote.UserId);
            Assert.Equal("Item", resolvedConflict1Remote.Type); // Verify Type property is set            // Remote newer should win conflict
            var resolvedConflict2Local = await _localStore.GetAsync("conflict2", _testUserId);
            Assert.NotNull(resolvedConflict2Local);
            Assert.Equal(remoteNewerItem.Content, resolvedConflict2Local.Content);
            Assert.Equal(_testUserId, resolvedConflict2Local.UserId);
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
                    Id = $"bulk{i}",
                    Content = $"Bulk content {i}",
                    LastModified = now.AddSeconds(i), // Each item has a slightly different time
                    UserId = _testUserId,
                    Type = "Item" // Add Type property
                });
            }

            // Add all items to local store
            await _localStore.UpsertBulkAsync(localItems);

            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, _testUserId);

            // Assert
            // We need to use GetByUserIdAsync to efficiently get items for a specific user 
            var remoteItems = await _remoteStore.GetByUserIdAsync(_testUserId);
            Assert.Equal(itemCount, remoteItems.Count);

            // Verify the content of each item
            foreach (var localItem in localItems)
            {
                var remoteItem = remoteItems.FirstOrDefault(i => i.Id == localItem.Id);
                Assert.NotNull(remoteItem);
                Assert.Equal(localItem.Content, remoteItem!.Content);
                Assert.Equal(_testUserId, remoteItem.UserId);
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
            var user1Item = new Item { Id = "user1Item", Content = "User 1 data", LastModified = now, UserId = "user1", Type = "Item" };
            var user2Item = new Item { Id = "user2Item", Content = "User 2 data", LastModified = now, UserId = "user2", Type = "Item" };

            // Add both items to remote store
            await _remoteStore.UpsertAsync(user1Item);
            await _remoteStore.UpsertAsync(user2Item);

            // Wait a moment to ensure items are properly persisted
            await Task.Delay(500);

            // Act - Sync with user1 filter
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, "user1");

            // Wait briefly to ensure sync completes
            await Task.Delay(500);

            // Assert            // Should only have user1's item in local store
            var localUser1Item = await _localStore.GetAsync("user1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("user2Item", "user2");

            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.UserId);
            Assert.Equal("Item", localUser1Item.Type); // Verify Type property is set

            // User2's item should not be synced
            Assert.Null(localUser2Item);
        }

        [Fact]
        public async Task SyncEngine_InitialUserDataPull_ShouldOnlyPullUserSpecificData()
        {
            // Arrange
            var now = DateTime.UtcNow;

            // Create items for different users
            var user1Item = new Item { Id = "initUser1Item", Content = "User 1 data", LastModified = now, UserId = "user1", Type = "Item" };
            var user2Item = new Item { Id = "initUser2Item", Content = "User 2 data", LastModified = now, UserId = "user2", Type = "Item" };
            var user1ItemDiffType = new Item { Id = "initUser1ItemOrder", Content = "User 1 order", LastModified = now, UserId = "user1", Type = "Order" };

            // Add items to remote store
            await _remoteStore.UpsertAsync(user1Item);
            await _remoteStore.UpsertAsync(user2Item);
            await _remoteStore.UpsertAsync(user1ItemDiffType);

            // Wait a moment to ensure items are properly persisted
            await Task.Delay(500);

            // Act - Perform initial data pull for user1 and specify "Item" as the type
            await SyncEngine.InitialUserDataPullAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified, "user1", "Item");

            // Wait briefly to ensure sync completes
            await Task.Delay(500);

            // Assert            // Should have all user1's items but not user2's
            var localUser1Item = await _localStore.GetAsync("initUser1Item", "user1");
            var localUser2Item = await _localStore.GetAsync("initUser2Item", "user2");
            var localUser1ItemDiffType = await _localStore.GetAsync("initUser1ItemOrder", "user1");

            Assert.NotNull(localUser1Item);
            Assert.Equal("User 1 data", localUser1Item.Content);
            Assert.Equal("user1", localUser1Item.UserId);
            Assert.Equal("Item", localUser1Item.Type);

            // User2's item should not be synced
            Assert.Null(localUser2Item);

            // User1's item with different type should still be synced (it's in the same partition)
            Assert.NotNull(localUser1ItemDiffType);
            Assert.Equal("User 1 order", localUser1ItemDiffType.Content);
            Assert.Equal("user1", localUser1ItemDiffType.UserId);
            Assert.Equal("Order", localUser1ItemDiffType.Type);
        }
    }
}