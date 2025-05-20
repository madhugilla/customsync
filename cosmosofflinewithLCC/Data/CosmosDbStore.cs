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
                string docType = typeof(T).Name;
                var typeProperty = typeof(T).GetProperty("Type");
                if (typeProperty != null)
                {
                    var defaultInstance = new T();
                    var defaultType = typeProperty.GetValue(defaultInstance)?.ToString();
                    if (!string.IsNullOrEmpty(defaultType))
                    {
                        docType = defaultType;
                    }
                }

                string partitionKey = $"{userId}:{docType}";
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
            string docType = typeof(T).Name;
            var typeProperty = typeof(T).GetProperty("Type");
            if (typeProperty != null)
            {
                var defaultInstance = new T();
                var defaultType = typeProperty.GetValue(defaultInstance)?.ToString();
                if (!string.IsNullOrEmpty(defaultType))
                {
                    docType = defaultType;
                }
            }            // Query for all items for this user, regardless of type
            var query = _container.GetItemQueryIterator<T>(
                new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.partitionKey, @userId)")
                .WithParameter("@userId", $"{userId}:")
            );

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }

        public async Task UpsertAsync(T document)
        {
            // Get the ID and userId from the document
            string id = _idProp.GetValue(document)?.ToString() ?? throw new System.Exception("Document must have Id");
            string userId = _userIdProp.GetValue(document)?.ToString() ?? throw new System.Exception("Document must have UserId");

            // Get document type from the Type property or class name
            string docType = _typeProp.GetValue(document)?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(docType))
            {
                // Fallback to class name if Type property is empty
                docType = typeof(T).Name;
            }

            // Create composite partition key
            string partitionKey = $"{userId}:{docType}";

            // First serialize to JsonNode to add the partitionKey
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            var jsonNode = JsonNode.Parse(json);
            if (jsonNode is JsonObject jsonObject)
            {
                // Add the composite partition key
                jsonObject[COSMOS_PARTITION_KEY_NAME] = partitionKey;

                // Serialize back to string with added partitionKey
                string finalJson = jsonObject.ToJsonString(_jsonOptions);                // Use stream overload to preserve exact JSON structure
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(finalJson));
                await _container.UpsertItemStreamAsync(
                    streamPayload: stream,
                    partitionKey: new PartitionKey(partitionKey)
                );
            }
        }

        public async Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            foreach (var document in documents)
            {
                await UpsertAsync(document);
            }
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