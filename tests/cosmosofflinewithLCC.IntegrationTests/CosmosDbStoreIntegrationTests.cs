using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace cosmosofflinewithLCC.IntegrationTests
{
    public class CosmosDbStoreIntegrationTests
    {
        private readonly Container _container;
        private readonly CosmosDbStore<Item> _store;

        public CosmosDbStoreIntegrationTests()
        {
            var cosmosClient = new CosmosClient("your-cosmos-db-connection-string");
            var database = cosmosClient.GetDatabase("TestDatabase");
            _container = database.GetContainer("TestContainer");
            _store = new CosmosDbStore<Item>(_container);
        }

        [Fact]
        public async Task UpsertAndGetAsync_ShouldStoreAndRetrieveItem()
        {
            var item = new Item { Id = "1", Content = "Test Item", LastModified = DateTime.UtcNow };

            await _store.UpsertAsync(item);
            var retrievedItem = await _store.GetAsync("1");

            Assert.NotNull(retrievedItem);
            Assert.Equal(item.Id, retrievedItem.Id);
            Assert.Equal(item.Content, retrievedItem.Content);
        }

        [Fact]
        public async Task UpsertBulkAsync_ShouldStoreMultipleItems()
        {
            var items = new List<Item>
            {
                new Item { Id = "1", Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { Id = "2", Content = "Item 2", LastModified = DateTime.UtcNow }
            };

            await _store.UpsertBulkAsync(items);
            var retrievedItems = await _store.GetAllAsync();

            Assert.Equal(2, retrievedItems.Count);
        }

        [Fact]
        public async Task GetAllAsync_ShouldReturnAllItems()
        {
            var items = new List<Item>
            {
                new Item { Id = "1", Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { Id = "2", Content = "Item 2", LastModified = DateTime.UtcNow }
            };

            await _store.UpsertBulkAsync(items);
            var retrievedItems = await _store.GetAllAsync();

            Assert.Equal(2, retrievedItems.Count);
            Assert.Contains(retrievedItems, i => i.Id == "1" && i.Content == "Item 1");
            Assert.Contains(retrievedItems, i => i.Id == "2" && i.Content == "Item 2");
        }
    }
}