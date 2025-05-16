using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Xunit;

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
    }
}
