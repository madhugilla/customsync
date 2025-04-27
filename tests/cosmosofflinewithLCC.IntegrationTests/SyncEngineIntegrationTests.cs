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

        public SyncEngineIntegrationTests()
        {
            // Setup Cosmos DB (using local emulator)
            _cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");
            var databaseId = "SyncTestDatabase";
            var containerId = "SyncTestContainer";
            
            try
            {
                // Create database and container if they don't exist
                _cosmosClient.CreateDatabaseIfNotExistsAsync(databaseId).GetAwaiter().GetResult();
                var database = _cosmosClient.GetDatabase(databaseId);
                database.CreateContainerIfNotExistsAsync(containerId, "/id").GetAwaiter().GetResult();
                _container = database.GetContainer(containerId);
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
                // Clean up Cosmos DB data
                var query = new QueryDefinition("SELECT c.id FROM c");
                var itemIds = new List<string>();
                
                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var item in response)
                    {
                        string id = item.id;
                        itemIds.Add(id);
                    }
                }

                foreach (var id in itemIds)
                {
                    try
                    {
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(id));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
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
                LastModified = now 
            };

            // Add item only to local store with pending change
            await _localStore.UpsertAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified);

            // Assert
            var remoteItem = await _remoteStore.GetAsync("test1");
            Assert.NotNull(remoteItem);
            Assert.Equal(localItem.Content, remoteItem.Content);
            
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
                LastModified = now
            };

            // Add item only to remote store
            await _remoteStore.UpsertAsync(remoteItem);

            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified);

            // Assert
            var localItem = await _localStore.GetAsync("test2");
            Assert.NotNull(localItem);
            Assert.Equal(remoteItem.Content, localItem.Content);
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
                LastModified = oldTime
            };
            await _remoteStore.UpsertAsync(remoteItem);
            
            // Add newer item to local with the same ID
            var localItem = new Item
            {
                Id = "test3",
                Content = "New local content",
                LastModified = now
            };
            await _localStore.UpsertAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified);

            // Assert - Remote should be updated with local content since local is newer
            var updatedRemoteItem = await _remoteStore.GetAsync("test3");
            Assert.NotNull(updatedRemoteItem);
            Assert.Equal(localItem.Content, updatedRemoteItem.Content);
            Assert.Equal(localItem.LastModified, updatedRemoteItem.LastModified);
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
                new Item { Id = "local1", Content = "Local item 1", LastModified = now },
                new Item { Id = "local2", Content = "Local item 2", LastModified = now }
            };
            
            // Items that should sync from remote to local
            var remoteItems = new List<Item>
            {
                new Item { Id = "remote1", Content = "Remote item 1", LastModified = now },
                new Item { Id = "remote2", Content = "Remote item 2", LastModified = now }
            };
            
            // Conflict items - local is newer
            var localNewerItem = new Item { Id = "conflict1", Content = "Local newer", LastModified = newerTime };
            var remoteOlderItem = new Item { Id = "conflict1", Content = "Remote older", LastModified = oldTime };
            
            // Conflict items - remote is newer
            var localOlderItem = new Item { Id = "conflict2", Content = "Local older", LastModified = oldTime };
            var remoteNewerItem = new Item { Id = "conflict2", Content = "Remote newer", LastModified = newerTime };
            
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
            
            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified);
            
            // Assert
            
            // Local items should be in remote
            foreach (var item in localItems)
            {
                var remoteItem = await _remoteStore.GetAsync(item.Id);
                Assert.NotNull(remoteItem);
                Assert.Equal(item.Content, remoteItem.Content);
            }
            
            // Remote items should be in local
            foreach (var item in remoteItems)
            {
                var localItem = await _localStore.GetAsync(item.Id);
                Assert.NotNull(localItem);
                Assert.Equal(item.Content, localItem.Content);
            }
            
            // Local newer should win conflict
            var resolvedConflict1Remote = await _remoteStore.GetAsync("conflict1");
            Assert.Equal(localNewerItem.Content, resolvedConflict1Remote!.Content);
            
            // Remote newer should win conflict
            var resolvedConflict2Local = await _localStore.GetAsync("conflict2");
            Assert.Equal(remoteNewerItem.Content, resolvedConflict2Local!.Content);
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
                    LastModified = now.AddSeconds(i) // Each item has a slightly different time
                });
            }
            
            // Add all items to local store
            await _localStore.UpsertBulkAsync(localItems);
            
            // Act
            await SyncEngine.SyncAsync(_localStore, _remoteStore, _logger, x => x.Id, x => x.LastModified);
            
            // Assert
            var remoteItems = await _remoteStore.GetAllAsync();
            Assert.Equal(itemCount, remoteItems.Count);
            
            // Verify the content of each item
            foreach (var localItem in localItems)
            {
                var remoteItem = remoteItems.FirstOrDefault(i => i.Id == localItem.Id);
                Assert.NotNull(remoteItem);
                Assert.Equal(localItem.Content, remoteItem!.Content);
            }
            
            // Verify pending changes are removed after sync
            var pendingChanges = await _localStore.GetPendingChangesAsync();
            Assert.Empty(pendingChanges);
        }
    }
}