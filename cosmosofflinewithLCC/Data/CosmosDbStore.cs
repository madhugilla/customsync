using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;

namespace cosmosofflinewithLCC.Data
{
    // Cosmos DB-based remote store (generic)
    public class CosmosDbStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly Container _container;
        private readonly PropertyInfo _idProp;
        public CosmosDbStore(Container container)
        {
            _container = container;
            _idProp = typeof(T).GetProperty("Id") ?? throw new System.Exception("Model must have Id property");
        }
        public async Task<T?> GetAsync(string id)
        {
            try
            {
                var response = await _container.ReadItemAsync<T>(id, new PartitionKey(id));
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }
        public async Task UpsertAsync(T document)
        {
            var id = _idProp.GetValue(document)?.ToString() ?? throw new System.Exception("Id required");
            await _container.UpsertItemAsync(document, new PartitionKey(id));
        }
        public async Task<List<T>> GetAllAsync()
        {
            var items = new List<T>();
            var query = _container.GetItemQueryIterator<T>("SELECT * FROM c");
            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                items.AddRange(response);
            }
            return items;
        }
        public Task<List<T>> GetPendingChangesAsync() => Task.FromResult(new List<T>()); // Not used for remote
        public Task RemovePendingChangeAsync(string id) => Task.CompletedTask; // Not used for remote

        public async Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            var partitionKey = new PartitionKey(string.Empty); // Assuming all items share the same partition key
            var batch = _container.CreateTransactionalBatch(partitionKey);

            foreach (var document in documents)
            {
                batch.UpsertItem(document);
            }

            var response = await batch.ExecuteAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Bulk upsert failed with status code {response.StatusCode}");
            }
        }
    }
}