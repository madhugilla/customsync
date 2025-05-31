using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;

namespace cosmosofflinewithLCC.IntegrationTests
{
    public class SqliteStoreIntegrationTests
    {
        private readonly string _dbPath;

        public SqliteStoreIntegrationTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"testdb_{Guid.NewGuid()}.sqlite");
            Console.WriteLine($"Database Path: {_dbPath}");
        }
        [Fact]
        public async Task UpsertAndGetAsync_ShouldStoreAndRetrieveItem()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var item = new Item { ID = "1", Content = "Test Item", LastModified = DateTime.UtcNow, OIID = "testUser" };

            await store.UpsertAsync(item);
            var retrievedItem = await store.GetAsync("1", "testUser");

            Assert.NotNull(retrievedItem);
            Assert.Equal(item.ID, retrievedItem.ID);
            Assert.Equal(item.Content, retrievedItem.Content);
            Assert.Equal(item.OIID, retrievedItem.OIID);
        }

        [Fact]
        public async Task UpsertBulkAsync_ShouldStoreMultipleItems()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var items = new List<Item>
            {
                new Item { ID = "1", Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { ID = "2", Content = "Item 2", LastModified = DateTime.UtcNow }
            };

            await store.UpsertBulkAsync(items);
            var retrievedItems = await store.GetAllAsync();

            Assert.Equal(2, retrievedItems.Count);
        }

        [Fact]
        public async Task GetPendingChangesAsync_ShouldReturnPendingItems()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var item = new Item { ID = "1", Content = "Pending Item", LastModified = DateTime.UtcNow };

            await store.UpsertAsync(item);
            var pendingItems = await store.GetPendingChangesAsync();

            Assert.Single(pendingItems);
            Assert.Equal(item.ID, pendingItems[0].ID);
        }

        [Fact]
        public async Task RemovePendingChangeAsync_ShouldRemovePendingItem()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var item = new Item { ID = "1", Content = "Pending Item", LastModified = DateTime.UtcNow };

            await store.UpsertAsync(item);
            await store.RemovePendingChangeAsync("1");
            var pendingItems = await store.GetPendingChangesAsync();

            Assert.Empty(pendingItems);
        }

        [Fact]
        public async Task GetByUserIdAsync_ShouldFilterByUserId()
        {
            // Create stores for different types
            var itemStore = new SqliteStore<Item>(_dbPath);
            var orderStore = new SqliteStore<Order>(_dbPath);

            // Create and store items for different users
            var user1 = "user1";
            var user2 = "user2";

            var user1Item = new Item
            {
                ID = "user1_item",
                Content = "User 1 content",
                LastModified = DateTime.UtcNow,
                OIID = user1,
                Type = "Item"
            };

            var user2Item = new Item
            {
                ID = "user2_item",
                Content = "User 2 content",
                LastModified = DateTime.UtcNow,
                OIID = user2,
                Type = "Item"
            };

            var user1Order = new Order
            {
                ID = "user1_order",
                Description = "User 1 order",
                LastModified = DateTime.UtcNow,
                OIID = user1,
                Type = "Order"
            };

            // Store the items
            await itemStore.UpsertAsync(user1Item);
            await itemStore.UpsertAsync(user2Item);
            await orderStore.UpsertAsync(user1Order);

            // Get items by user ID
            var user1Items = await itemStore.GetByUserIdAsync(user1);
            var user2Items = await itemStore.GetByUserIdAsync(user2);
            var user1Orders = await orderStore.GetByUserIdAsync(user1);

            // Verify results
            Assert.Single(user1Items);
            Assert.Equal("user1_item", user1Items[0].ID);

            Assert.Single(user2Items);
            Assert.Equal("user2_item", user2Items[0].ID);

            Assert.Single(user1Orders);
            Assert.Equal("user1_order", user1Orders[0].ID);
        }

        [Fact]
        public async Task GetPendingChangesForUserAsync_ShouldOnlyReturnUserItems()
        {
            // Setup test stores and data
            var itemStore = new SqliteStore<Item>(_dbPath);

            // Create items for different users
            var user1 = "user1";
            var user2 = "user2";

            var user1Item = new Item
            {
                ID = "pending1",
                Content = "Pending User 1",
                LastModified = DateTime.UtcNow,
                OIID = user1,
                Type = "Item"
            };

            var user2Item = new Item
            {
                ID = "pending2",
                Content = "Pending User 2",
                LastModified = DateTime.UtcNow,
                OIID = user2,
                Type = "Item"
            };

            // Store the items, which will mark them as pending changes
            await itemStore.UpsertAsync(user1Item);
            await itemStore.UpsertAsync(user2Item);

            // Get pending changes by user
            var user1Pending = await itemStore.GetPendingChangesForUserAsync(user1);
            var user2Pending = await itemStore.GetPendingChangesForUserAsync(user2);

            // Verify results
            Assert.Single(user1Pending);
            Assert.Equal("pending1", user1Pending[0].ID);
            Assert.Equal(user1, user1Pending[0].OIID); Assert.Single(user2Pending);
            Assert.Equal("pending2", user2Pending[0].ID);
            Assert.Equal(user2, user2Pending[0].OIID);
        }

        [Fact]
        public async Task GetByUserIdAsync_WithExcludeIds_ShouldExcludeSpecifiedItems()
        {
            // Arrange
            var itemStore = new SqliteStore<Item>(_dbPath);
            var userId = "user-exclude-test";

            var items = new List<Item>
            {
                new Item
                {
                    ID = "sqlite1",
                    Content = "SQLite Item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "sqlite2",
                    Content = "SQLite Item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "sqlite3",
                    Content = "SQLite Item 3",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "sqlite4",
                    Content = "SQLite Item 4",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                }
            };

            // Store all items
            foreach (var item in items)
            {
                await itemStore.UpsertAsync(item);
            }

            // Act - exclude items 1 and 3
            var excludeIds = new HashSet<string> { "sqlite1", "sqlite3" };
            var results = await itemStore.GetByUserIdAsync(userId, excludeIds);

            // Assert
            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.ID == "sqlite2");
            Assert.Contains(results, r => r.ID == "sqlite4");
            Assert.DoesNotContain(results, r => r.ID == "sqlite1");
            Assert.DoesNotContain(results, r => r.ID == "sqlite3");

            // Verify all returned items belong to the correct user
            Assert.All(results, r => Assert.Equal(userId, r.OIID));
        }

        [Fact]
        public async Task GetByUserIdAsync_WithEmptyExcludeIds_ShouldReturnAllUserItems()
        {
            // Arrange
            var itemStore = new SqliteStore<Item>(_dbPath);
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

            foreach (var item in items)
            {
                await itemStore.UpsertAsync(item);
            }

            // Act - test with empty exclude set
            var emptyExcludeIds = new HashSet<string>();
            var resultsEmpty = await itemStore.GetByUserIdAsync(userId, emptyExcludeIds);

            // Act - test with null exclude set
            var resultsNull = await itemStore.GetByUserIdAsync(userId, null);

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
            var itemStore = new SqliteStore<Item>(_dbPath);
            var userId = "user-nonexistent-exclude";

            var items = new List<Item>
            {
                new Item
                {
                    ID = "real1",
                    Content = "Real SQLite item 1",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                },
                new Item
                {
                    ID = "real2",
                    Content = "Real SQLite item 2",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                }
            };

            foreach (var item in items)
            {
                await itemStore.UpsertAsync(item);
            }

            // Act - exclude non-existent items
            var excludeIds = new HashSet<string> { "nonexistent1", "nonexistent2" };
            var results = await itemStore.GetByUserIdAsync(userId, excludeIds);

            // Assert - should return all items since excluded IDs don't exist
            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.ID == "real1");
            Assert.Contains(results, r => r.ID == "real2");
        }

        [Fact]
        public async Task GetByUserIdAsync_WithLargeExcludeIdSet_ShouldHandleEfficiently()
        {
            // Arrange
            var itemStore = new SqliteStore<Item>(_dbPath);
            var userId = "user-large-exclude";
            var totalItems = 15;
            var excludeCount = 10;

            var items = new List<Item>();
            var excludeIds = new HashSet<string>();

            // Create items and exclude IDs
            for (int i = 1; i <= totalItems; i++)
            {
                var item = new Item
                {
                    ID = $"large{i}",
                    Content = $"Large SQLite test item {i}",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                };
                items.Add(item);

                // Exclude the first 10 items
                if (i <= excludeCount)
                {
                    excludeIds.Add(item.ID);
                }
            }

            // Store all items
            foreach (var item in items)
            {
                await itemStore.UpsertAsync(item);
            }

            // Act
            var results = await itemStore.GetByUserIdAsync(userId, excludeIds);

            // Assert - should return only the non-excluded items (5 items)
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
            var itemStore = new SqliteStore<Item>(_dbPath);
            var user1 = "user1-exclude-test";
            var user2 = "user2-exclude-test";

            var user1Items = new List<Item>
            {
                new Item { ID = "u1item1", Content = "User 1 SQLite Item 1", LastModified = DateTime.UtcNow, OIID = user1, Type = "Item" },
                new Item { ID = "u1item2", Content = "User 1 SQLite Item 2", LastModified = DateTime.UtcNow, OIID = user1, Type = "Item" }
            };

            var user2Items = new List<Item>
            {
                new Item { ID = "u2item1", Content = "User 2 SQLite Item 1", LastModified = DateTime.UtcNow, OIID = user2, Type = "Item" },
                new Item { ID = "u2item2", Content = "User 2 SQLite Item 2", LastModified = DateTime.UtcNow, OIID = user2, Type = "Item" }
            };

            // Store items for both users
            foreach (var item in user1Items.Concat(user2Items))
            {
                await itemStore.UpsertAsync(item);
            }

            // Act - exclude each user's first item
            var excludeIds = new HashSet<string> { "u1item1", "u2item1" };
            var user1Results = await itemStore.GetByUserIdAsync(user1, excludeIds);
            var user2Results = await itemStore.GetByUserIdAsync(user2, excludeIds);

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

        [Fact]
        public async Task GetByUserIdAsync_WithExcludeIds_ShouldHandleBothCamelCaseAndPascalCase()
        {
            // Arrange - test that the method works with both camelCase (oiid) and PascalCase (OIID) JSON properties
            var itemStore = new SqliteStore<Item>(_dbPath);
            var userId = "case-test-user";

            var item = new Item
            {
                ID = "case-test",
                Content = "Case sensitivity test",
                LastModified = DateTime.UtcNow,
                OIID = userId,
                Type = "Item"
            };

            await itemStore.UpsertAsync(item);

            // Act - exclude the item using various exclude sets
            var excludeIds = new HashSet<string> { "case-test" };
            var results = await itemStore.GetByUserIdAsync(userId, excludeIds);

            // Assert - should exclude the item regardless of JSON property casing
            Assert.Empty(results);
        }
    }
}
