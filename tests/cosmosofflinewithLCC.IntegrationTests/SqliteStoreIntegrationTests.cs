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
            var item = new Item { Id = "1", Content = "Test Item", LastModified = DateTime.UtcNow, UserId = "testUser" };

            await store.UpsertAsync(item);
            var retrievedItem = await store.GetAsync("1", "testUser");

            Assert.NotNull(retrievedItem);
            Assert.Equal(item.Id, retrievedItem.Id);
            Assert.Equal(item.Content, retrievedItem.Content);
            Assert.Equal(item.UserId, retrievedItem.UserId);
        }

        [Fact]
        public async Task UpsertBulkAsync_ShouldStoreMultipleItems()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var items = new List<Item>
            {
                new Item { Id = "1", Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { Id = "2", Content = "Item 2", LastModified = DateTime.UtcNow }
            };

            await store.UpsertBulkAsync(items);
            var retrievedItems = await store.GetAllAsync();

            Assert.Equal(2, retrievedItems.Count);
        }

        [Fact]
        public async Task GetPendingChangesAsync_ShouldReturnPendingItems()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var item = new Item { Id = "1", Content = "Pending Item", LastModified = DateTime.UtcNow };

            await store.UpsertAsync(item);
            var pendingItems = await store.GetPendingChangesAsync();

            Assert.Single(pendingItems);
            Assert.Equal(item.Id, pendingItems[0].Id);
        }

        [Fact]
        public async Task RemovePendingChangeAsync_ShouldRemovePendingItem()
        {
            var store = new SqliteStore<Item>(_dbPath);
            var item = new Item { Id = "1", Content = "Pending Item", LastModified = DateTime.UtcNow };

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
                Id = "user1_item",
                Content = "User 1 content",
                LastModified = DateTime.UtcNow,
                UserId = user1,
                Type = "Item"
            };

            var user2Item = new Item
            {
                Id = "user2_item",
                Content = "User 2 content",
                LastModified = DateTime.UtcNow,
                UserId = user2,
                Type = "Item"
            };

            var user1Order = new Order
            {
                Id = "user1_order",
                Description = "User 1 order",
                LastModified = DateTime.UtcNow,
                UserId = user1,
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
            Assert.Equal("user1_item", user1Items[0].Id);

            Assert.Single(user2Items);
            Assert.Equal("user2_item", user2Items[0].Id);

            Assert.Single(user1Orders);
            Assert.Equal("user1_order", user1Orders[0].Id);
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
                Id = "pending1",
                Content = "Pending User 1",
                LastModified = DateTime.UtcNow,
                UserId = user1,
                Type = "Item"
            };

            var user2Item = new Item
            {
                Id = "pending2",
                Content = "Pending User 2",
                LastModified = DateTime.UtcNow,
                UserId = user2,
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
            Assert.Equal("pending1", user1Pending[0].Id);
            Assert.Equal(user1, user1Pending[0].UserId);

            Assert.Single(user2Pending);
            Assert.Equal("pending2", user2Pending[0].Id);
            Assert.Equal(user2, user2Pending[0].UserId);
        }
    }
}
