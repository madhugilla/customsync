using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace cosmosofflinewithLCC.Data
{
    // Cosmos DB-based remote store (generic)
    public class CosmosDbStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly Container _container;
        private readonly PropertyInfo _idProp;
        private readonly PropertyInfo? _userIdProp;
        private readonly PropertyInfo? _typeProp;

        // Cosmos DB requires exact casing for the partition key property
        private const string COSMOS_PARTITION_KEY_NAME = "partitionKey";

        public CosmosDbStore(Container container)
        {
            _container = container;
            _idProp = typeof(T).GetProperty("Id") ?? throw new System.Exception("Model must have Id property");

            // Find the userId property (could be UserId or userId)
            _userIdProp = typeof(T).GetProperty("UserId") ?? typeof(T).GetProperty("userId");

            // Find the Type property
            _typeProp = typeof(T).GetProperty("Type");

            if (_userIdProp == null)
            {
                throw new System.Exception("Model must have UserId property for partitioning");
            }

            if (_typeProp == null)
            {
                throw new System.Exception("Model must have Type property for partitioning");
            }
        }

        /// <summary>
        /// Gets a document by ID and userId (partition key)
        /// </summary>
        /// <param name="id">The document ID</param>
        /// <param name="userId">The user ID (part of the partition key)</param>
        /// <returns>The retrieved document or null if not found</returns>
        public async Task<T?> GetAsync(string id, string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty for efficient point reads", nameof(userId));
            }

            try
            {
                // Get the document type from the generic type parameter T
                string docType = typeof(T).Name;

                // Create the composite partition key
                string partitionKey = $"{userId}:{docType}";

                // Use a direct point read with the composite partition key for maximum efficiency
                var response = await _container.ReadItemAsync<T>(
                    id: id,
                    partitionKey: new PartitionKey(partitionKey)
                );

                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task UpsertAsync(T document)
        {
            try
            {
                // Get the document ID and convert to lowercase for Cosmos DB
                string? id = _idProp.GetValue(document)?.ToString();
                if (string.IsNullOrEmpty(id))
                {
                    throw new System.Exception("Id cannot be null or empty");
                }

                // Get the partition key value components (userId and docType)
                string? userId = _userIdProp?.GetValue(document)?.ToString();
                if (string.IsNullOrEmpty(userId))
                {
                    throw new System.Exception("UserId cannot be null or empty");
                }

                // Get document type (can be from the Type property or from the class name)
                string? docType = _typeProp?.GetValue(document)?.ToString();
                if (string.IsNullOrEmpty(docType))
                {
                    // Fallback to class name if Type property is empty
                    docType = typeof(T).Name;
                }

                // Create composite partition key
                string partitionKey = $"{userId}:{docType}";

                // Serialize to JSON string first
                string json = JsonConvert.SerializeObject(document);

                // Parse into JObject to manipulate properties
                JObject jObject = JObject.Parse(json);                // Ensure the document has the correct properties for Cosmos DB:
                // 1. Lowercase 'id' (Cosmos DB standard)
                jObject.Remove("Id");
                jObject["id"] = id;

                // Add the composite partition key with the exact name expected by Cosmos DB
                // Remove any variations of userId/UserId that might exist
                jObject.Remove("UserId");
                jObject.Remove("userId");

                // Keep the original UserId property for backward compatibility
                jObject["userId"] = userId;

                // Add the composite partition key for Cosmos DB partitioning
                jObject[COSMOS_PARTITION_KEY_NAME] = partitionKey;

                Console.WriteLine($"Upserting document with ID: {id}, partition key: {partitionKey}, key property: {COSMOS_PARTITION_KEY_NAME}");

                // Use the composite partition key value for the operation
                await _container.UpsertItemAsync<JObject>(
                    item: jObject,
                    partitionKey: new PartitionKey(partitionKey)
                );
            }
            catch (CosmosException ex)
            {
                Console.WriteLine($"Cosmos DB Error: {ex.StatusCode}, {ex.SubStatusCode}, Message: {ex.Message}");
                throw;
            }
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
            try
            {
                // Create a list to track failures
                var failedItems = new List<(T document, Exception exception)>();

                // Process each document individually
                foreach (var document in documents)
                {
                    try
                    {                        // Get the document ID and convert to lowercase for Cosmos DB
                        string? id = _idProp.GetValue(document)?.ToString();
                        if (string.IsNullOrEmpty(id))
                        {
                            throw new System.Exception("Id cannot be null or empty");
                        }

                        // Get the partition key value components (userId and docType)
                        string? userId = _userIdProp?.GetValue(document)?.ToString();
                        if (string.IsNullOrEmpty(userId))
                        {
                            throw new System.Exception("UserId cannot be null or empty");
                        }

                        // Get document type (can be from the Type property or from the class name)
                        string? docType = _typeProp?.GetValue(document)?.ToString();
                        if (string.IsNullOrEmpty(docType))
                        {
                            // Fallback to class name if Type property is empty
                            docType = typeof(T).Name;
                        }

                        // Create composite partition key
                        string partitionKey = $"{userId}:{docType}";

                        // Serialize to JSON string first
                        string json = JsonConvert.SerializeObject(document);

                        // Parse into JObject to manipulate properties
                        JObject jObject = JObject.Parse(json);

                        // Ensure the document has the correct properties for Cosmos DB:
                        // 1. Lowercase 'id' (Cosmos DB standard)
                        jObject.Remove("Id");
                        jObject["id"] = id;

                        // Remove any variations of userId/UserId that might exist
                        jObject.Remove("UserId");
                        jObject.Remove("userId");

                        // Keep the original UserId property for backward compatibility
                        jObject["userId"] = userId;
                        // Add the composite partition key for Cosmos DB partitioning
                        jObject[COSMOS_PARTITION_KEY_NAME] = partitionKey;

                        Console.WriteLine($"Bulk upserting document with ID: {id}, partition key: {partitionKey}, key property: {COSMOS_PARTITION_KEY_NAME}");

                        // Use the composite partition key value for the operation
                        await _container.UpsertItemAsync<JObject>(
                            item: jObject,
                            partitionKey: new PartitionKey(partitionKey)
                        );
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in bulk operation for item {_idProp.GetValue(document)}: {ex.Message}");
                        failedItems.Add((document, ex));
                    }
                }

                // If we had any failures, throw an exception with details
                if (failedItems.Count > 0)
                {
                    throw new Exception($"Error during bulk upsert: {failedItems.Count} items failed",
                        failedItems.FirstOrDefault().exception);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bulk operation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets all documents for a specific user ID and document type
        /// </summary>
        /// <param name="userId">The user ID to filter by</param>
        /// <returns>A list of documents belonging to the specified user for the current document type</returns>
        public async Task<List<T>> GetByUserIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty", nameof(userId));
            }

            var items = new List<T>();

            try
            {
                // Use a query to find all items that belong to this user regardless of document type
                // This searches for any documents with a partitionKey that starts with the userId prefix
                string queryText = $"SELECT * FROM c WHERE STARTSWITH(c.partitionKey, '{userId}:')";

                Console.WriteLine($"Executing query: {queryText}");
                var query = _container.GetItemQueryIterator<T>(
                  queryText: queryText
              );

                while (query.HasMoreResults)
                {
                    var response = await query.ReadNextAsync();
                    items.AddRange(response);
                }

                Console.WriteLine($"Found {items.Count} items in Cosmos DB for userId {userId}");
            }
            catch (CosmosException ex)
            {
                Console.WriteLine($"Error in GetByUserIdAsync: {ex.Message}. Status code: {ex.StatusCode}, Substatus: {ex.SubStatusCode}");
                throw;
            }

            return items;
        }

        /// <summary>
        /// Gets pending changes for a specific user by running a filtered query
        /// </summary>
        /// <param name="userId">The user ID to filter by</param>
        /// <returns>A list of pending changes for the specified user</returns>
        public Task<List<T>> GetPendingChangesForUserAsync(string userId)
        {
            // This is a stub since Cosmos DB doesn't track pending changes
            // The implementation is on the local SQLite side
            return Task.FromResult(new List<T>());
        }
    }
}