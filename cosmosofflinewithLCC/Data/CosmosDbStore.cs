using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;

namespace cosmosofflinewithLCC.Data
{
    // Cosmos DB-based remote store (generic)
    public class CosmosDbStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly Container _container;
        private readonly PropertyInfo _idProp;
        private readonly PropertyInfo _userIdProp;
        private readonly PropertyInfo _typeProp;
        private const string COSMOS_PARTITION_KEY_NAME = "partitionKey";

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CosmosDbStore(Container container)
        {
            _container = container; _idProp = typeof(T).GetProperty("ID") ?? throw new System.Exception("Model must have ID property");
            _userIdProp = typeof(T).GetProperty("OIID") ?? throw new System.Exception("Model must have OIID property for partitioning");
            _typeProp = typeof(T).GetProperty("Type") ?? throw new System.Exception("Model must have Type property for partitioning");
        }

        public async Task<List<T>> GetAllAsync()
        {
            var query = _container.GetItemQueryIterator<T>();
            var results = new List<T>();

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }
        public async Task<T?> GetAsync(string id, string userId)
        {
            try
            {
                // Create instance with userId to generate partition key
                var instance = new T();
                _userIdProp.SetValue(instance, userId);

                var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                    throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

                string partitionKey = partitionKeyProp.GetValue(instance)?.ToString() ??
                    throw new InvalidOperationException("PartitionKey cannot be null");

                var result = await _container.ReadItemAsync<T>(
                    id: id,
                    partitionKey: new PartitionKey(partitionKey)
                );
                return result.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }
        public async Task<List<T>> GetByUserIdAsync(string userId)
        {
            var results = new List<T>();

            // Create instance with userId to generate partition key
            var instance = new T();
            _userIdProp.SetValue(instance, userId);

            var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

            string partitionKey = partitionKeyProp.GetValue(instance)?.ToString() ??
                throw new InvalidOperationException("PartitionKey cannot be null");

            var query = _container.GetItemQueryIterator<T>(
                new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @partitionKey")
                .WithParameter("@partitionKey", partitionKey)
            );

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }

        public Task UpsertAsync(T document)
        {
            return UpsertAsync(document, true);
        }

        public async Task UpsertAsync(T document, bool markAsPending)
        {
            // Validate document has required properties
            string id = _idProp.GetValue(document)?.ToString() ?? throw new System.Exception("Document must have Id");

            // Get partition key from document
            var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

            string partitionKey = partitionKeyProp.GetValue(document)?.ToString() ??
                throw new InvalidOperationException("PartitionKey cannot be null");

            // Serialize document
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            var jsonNode = JsonNode.Parse(json);
            if (jsonNode is JsonObject jsonObject)
            {
                // Add the partition key
                jsonObject[COSMOS_PARTITION_KEY_NAME] = partitionKey;

                // Serialize back to string with added partitionKey
                string finalJson = jsonObject.ToJsonString(_jsonOptions);

                // Use stream overload to preserve exact JSON structure
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(finalJson));
                await _container.UpsertItemStreamAsync(
                    streamPayload: stream,
                    partitionKey: new PartitionKey(partitionKey)
                );
            }
        }

        public Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            return UpsertBulkAsync(documents, true);
        }

        public async Task UpsertBulkAsync(IEnumerable<T> documents, bool markAsPending)
        {
            foreach (var document in documents)
            {
                await UpsertAsync(document, markAsPending);
            }
        }

        public Task UpsertBulkWithoutPendingAsync(IEnumerable<T> documents)
        {
            // For remote store, this is the same as regular UpsertBulkAsync since we don't track pending changes
            return UpsertBulkAsync(documents);
        }

        public virtual Task<List<T>> GetPendingChangesAsync()
        {
            // Remote store doesn't track pending changes
            return Task.FromResult(new List<T>());
        }

        public virtual Task RemovePendingChangeAsync(string id)
        {
            // Remote store doesn't track pending changes
            return Task.CompletedTask;
        }

        public virtual Task<List<T>> GetPendingChangesForUserAsync(string userId)
        {
            // Remote store doesn't track pending changes
            return Task.FromResult(new List<T>());
        }
    }
}