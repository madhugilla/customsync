using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.Reflection;

namespace cosmosofflinewithLCC.Data
{    // Cosmos DB-based remote store (generic) with token-based authentication
    public class CosmosDbStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly ICosmosClientFactory _clientFactory;
        private readonly string _databaseId;
        private readonly string _containerId;
        private readonly PropertyInfo _idProp;
        private readonly PropertyInfo _userIdProp;
        private readonly PropertyInfo _typeProp;
        private const string COSMOS_PARTITION_KEY_NAME = "partitionKey";

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CosmosDbStore(ICosmosClientFactory clientFactory, string databaseId, string containerId)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _databaseId = databaseId ?? throw new ArgumentNullException(nameof(databaseId));
            _containerId = containerId ?? throw new ArgumentNullException(nameof(containerId));

            _idProp = typeof(T).GetProperty("ID") ?? throw new System.Exception("Model must have ID property");
            _userIdProp = typeof(T).GetProperty("OIID") ?? throw new System.Exception("Model must have OIID property for partitioning");
            _typeProp = typeof(T).GetProperty("Type") ?? throw new System.Exception("Model must have Type property for partitioning");
        }
        public async Task<List<T>> GetAllAsync()
        {
            var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
            var query = container.GetItemQueryIterator<T>();
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
                var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);

                // Create instance with userId and type to generate partition key
                var instance = new T();
                _userIdProp.SetValue(instance, userId);
                _typeProp.SetValue(instance, typeof(T).Name); // Set the Type property

                var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                    throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

                // The 'partitionKey' string's format, including any colons (e.g., "userId:Type"),
                // is determined by the implementation of the 'PartitionKey' property getter in the model class T.
                // This store assumes the model's PartitionKey property correctly combines OIID and Type.
                string partitionKey = partitionKeyProp.GetValue(instance)?.ToString() ??
                    throw new InvalidOperationException("PartitionKey cannot be null or empty after setting OIID and Type.");

                var result = await container.ReadItemAsync<T>(
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
            var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
            var results = new List<T>();

            // Create instance with userId and type to generate partition key
            var instance = new T();
            _userIdProp.SetValue(instance, userId);
            _typeProp.SetValue(instance, typeof(T).Name); // Set the Type property

            var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

            // The 'partitionKey' string's format, including any colons (e.g., "userId:Type"),
            // is determined by the implementation of the 'PartitionKey' property getter in the model class T.
            // This store assumes the model's PartitionKey property correctly combines OIID and Type.
            string partitionKey = partitionKeyProp.GetValue(instance)?.ToString() ??
                throw new InvalidOperationException("PartitionKey cannot be null or empty after setting OIID and Type.");

            // Using QueryRequestOptions with PartitionKey - This instructs Cosmos DB to query only within the specified partition
            // This is equivalent to "SELECT * FROM c" restricted to the given partition
            var query = container.GetItemQueryIterator<T>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) }
            );

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }

        /// <summary>
        /// Retrieves items by their IDs and userId, batching the queries for efficiency
        /// </summary>
        /// <param name="ids">Collection of document IDs to retrieve</param>
        /// <param name="userId">The user ID to generate partition keys</param>
        /// <returns>Dictionary mapping document IDs to their corresponding documents</returns>
        public async Task<Dictionary<string, T>> GetItemsByIdsAsync(IEnumerable<string> ids, string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty for efficient retrieval", nameof(userId));
            }

            // For small number of IDs, parallel point reads might be more efficient
            var idsList = ids.ToList();
            var result = new Dictionary<string, T>();

            if (idsList.Count == 0)
                return result;

            // Create instance with userId and type to generate partition key
            var instance = new T();
            _userIdProp.SetValue(instance, userId);
            _typeProp.SetValue(instance, typeof(T).Name); // Set the Type property

            var partitionKeyProp = typeof(T).GetProperty("PartitionKey") ??
                throw new InvalidOperationException($"Type {typeof(T).Name} must have PartitionKey property");

            string partitionKey = partitionKeyProp.GetValue(instance)?.ToString() ??
                throw new InvalidOperationException("PartitionKey cannot be null or empty after setting OIID and Type.");

            // For large number of IDs, use a query with an IN clause - more efficient than individual reads
            // Build the query with parameter placeholder for each ID
            var parameterNames = idsList.Select((_, index) => $"@id{index}").ToList();
            var queryText = $"SELECT * FROM c WHERE c.partitionKey = @partitionKey AND c.id IN ({string.Join(", ", parameterNames)})";

            var queryDefinition = new QueryDefinition(queryText)
                .WithParameter("@partitionKey", partitionKey);            // Add all IDs as parameters
            for (int i = 0; i < idsList.Count; i++)
            {
                queryDefinition = queryDefinition.WithParameter($"@id{i}", idsList[i]);
            }

            var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
            var query = container.GetItemQueryIterator<T>(queryDefinition);

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                foreach (var item in response)
                {
                    var id = _idProp.GetValue(item)?.ToString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        result[id] = item;
                    }
                }
            }

            return result;
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

            // The 'partitionKey' string's format, including any colons (e.g., "userId:Type"),
            // is determined by the implementation of the 'PartitionKey' property getter in the model class T,
            // using the OIID and Type properties already set on the 'document'.
            string partitionKey = partitionKeyProp.GetValue(document)?.ToString() ??
                throw new InvalidOperationException("PartitionKey cannot be null");            // Serialize document directly without modifying it
            string json = JsonSerializer.Serialize(document, _jsonOptions);

            // Use stream overload to preserve exact JSON structure
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
            await container.UpsertItemStreamAsync(
                streamPayload: stream,
                partitionKey: new PartitionKey(partitionKey),
                requestOptions: new ItemRequestOptions
                {
                    EnableContentResponseOnWrite = false,

                }
            );
        }
        public Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            return UpsertBulkAsync(documents, true);
        }
        public async Task UpsertBulkAsync(IEnumerable<T> documents, bool markAsPending)
        {
            var docsList = documents.ToList();
            if (docsList.Count == 0) return;

            try
            {
                // Fall back to individual upserts for reliability
                var tasks = new List<Task>();
                foreach (var doc in docsList)
                {
                    tasks.Add(UpsertAsync(doc, markAsPending));
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in UpsertBulkAsync: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                throw;
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