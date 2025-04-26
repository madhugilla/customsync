using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace cosmosofflinewithLCC.IntegrationTests
{
    public class CosmosDbStoreIntegrationTests : IDisposable
    {
        private readonly Container _container;
        private readonly CosmosDbStore<Item> _store;

        public CosmosDbStoreIntegrationTests()
        {
            var cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");
            var database = cosmosClient.GetDatabase("TestDatabase");
            _container = database.GetContainer("TestContainer");
            _store = new CosmosDbStore<Item>(_container);

            // Perform setup logic
            CleanupContainerAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            // Perform teardown logic
            CleanupContainerAsync().GetAwaiter().GetResult();
        }

        private async Task CleanupContainerAsync()
        {
            // Batch delete items in the container
            var query = new QueryDefinition("SELECT c.id FROM c");
            using var iterator = _container.GetItemQueryIterator<dynamic>(query);

            var idsToDelete = new List<string>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var item in response)
                {
                    var id = (string)item.id; // Explicitly cast to string
                    Console.WriteLine($"Deleting item with ID: {id}");
                    idsToDelete.Add(id);
                }
            }

            if (idsToDelete.Any())
            {
                await DeleteBulkAsync(idsToDelete);
            }

            // Verify cleanup
            using var verifyIterator = _container.GetItemQueryIterator<dynamic>(query);
            if (verifyIterator.HasMoreResults)
            {
                var verifyResponse = await verifyIterator.ReadNextAsync();
                if (verifyResponse.Any())
                {
                    foreach (var item in verifyResponse)
                    {
                        Console.WriteLine($"Remaining item after cleanup: {item.id}");
                    }
                    throw new Exception("Cleanup failed: Items still exist in the container.");
                }
            }
        }

        private async Task DeleteBulkAsync(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                try
                {
                    await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(id));
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Ignore NotFound errors as the item might already be deleted
                }
            }
        }

        [Fact]
        public async Task UpsertAndGetAsync_ShouldStoreAndRetrieveItem()
        {
            var item = new Item { Id = Guid.NewGuid().ToString(), Content = "Test Item", LastModified = DateTime.UtcNow };

            await _store.UpsertAsync(item);
            var retrievedItem = await _store.GetAsync(item.Id);

            Assert.NotNull(retrievedItem);
            Assert.Equal(item.Id, retrievedItem.Id);
            Assert.Equal(item.Content, retrievedItem.Content);
        }

        [Fact]
        public async Task UpsertBulkAsync_ShouldStoreMultipleItems()
        {
            var items = new List<Item>
            {
                new Item { Id = Guid.NewGuid().ToString(), Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { Id = Guid.NewGuid().ToString(), Content = "Item 2", LastModified = DateTime.UtcNow }
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
                new Item { Id = Guid.NewGuid().ToString(), Content = "Item 1", LastModified = DateTime.UtcNow },
                new Item { Id = Guid.NewGuid().ToString(), Content = "Item 2", LastModified = DateTime.UtcNow }
            };

            await _store.UpsertBulkAsync(items);
            var retrievedItems = await _store.GetAllAsync();

            Assert.Equal(2, retrievedItems.Count);
            Assert.Contains(retrievedItems, i => i.Content == "Item 1");
            Assert.Contains(retrievedItems, i => i.Content == "Item 2");
        }
    }
}