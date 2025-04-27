using Microsoft.Azure.Cosmos;
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
            var partitionKeyProperty = typeof(T).GetProperty("Id"); // Replace with your partition key property name
            if (partitionKeyProperty == null)
            {
                throw new Exception("PartitionKey property is required for bulk upsert");
            }

            var groupedByPartitionKey = documents
                .GroupBy(doc => partitionKeyProperty.GetValue(doc)?.ToString() ?? string.Empty);

            foreach (var group in groupedByPartitionKey)
            {
                var partitionKey = new PartitionKey(group.Key);
                var batch = _container.CreateTransactionalBatch(partitionKey);

                foreach (var document in group)
                {
                    var id = partitionKeyProperty.GetValue(document)?.ToString();
                    Console.WriteLine($"Upserting document with ID: {id} in partition: {group.Key}");
                    batch.UpsertItem(document);
                }

                var response = await batch.ExecuteAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Bulk upsert failed for partition key {group.Key} with status code {response.StatusCode}");
                }
            }
        }

        public async Task<List<T>> GetByUserIdAsync(string userId)
        {
            var items = new List<T>();

            // Using parameterized query to avoid SQL injection
            var queryText = "SELECT * FROM c WHERE c.userId = @userId";
            var queryDefinition = new QueryDefinition(queryText)
                .WithParameter("@userId", userId);

            var query = _container.GetItemQueryIterator<T>(queryDefinition);

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                items.AddRange(response);
            }

            return items;
        }
    }
}