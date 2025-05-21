using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// A remote document store implementation that calls Azure Functions endpoints
    /// to perform Cosmos DB operations. The function key is passed in the request headers 
    /// for authentication. This implementation mirrors the partition key handling logic
    /// from CosmosDbStore.
    /// </summary>
    public class FunctionDocumentStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private string _functionKey;
        private readonly PropertyInfo _idProp;
        private readonly PropertyInfo _userIdProp;
        private readonly PropertyInfo _typeProp;
        private const string COSMOS_PARTITION_KEY_NAME = "partitionKey";

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public FunctionDocumentStore(HttpClient httpClient, string baseUrl, string functionKey)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _functionKey = functionKey ?? throw new ArgumentNullException(nameof(functionKey));

            // Mirror CosmosDbStore property initialization
            _idProp = typeof(T).GetProperty("ID") ?? throw new System.Exception("Model must have ID property");
            _userIdProp = typeof(T).GetProperty("OIID") ?? throw new System.Exception("Model must have OIID property for partitioning");
            _typeProp = typeof(T).GetProperty("Type") ?? throw new System.Exception("Model must have Type property for partitioning");
        }

        /// <summary>
        /// Overrides the existing function key with a new value.
        /// </summary>
        /// <param name="newFunctionKey">The new function key to use for authentication.</param>
        public void OverrideFunctionKey(string newFunctionKey)
        {
            if (string.IsNullOrWhiteSpace(newFunctionKey))
            {
                throw new ArgumentException("Function key cannot be null or empty.", nameof(newFunctionKey));
            }
            _functionKey = newFunctionKey;
        }

        /// <summary>
        /// Gets all items (for compatibility with IDocumentStore interface).
        /// </summary>
        public async Task<List<T>> GetAllAsync()
        {
            var url = $"{_baseUrl}/api/GetAll";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-functions-key", _functionKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<List<T>>();
            return result ?? new List<T>();
        }

        /// <summary>
        /// Gets a specific item by ID and user ID.
        /// </summary>
        public async Task<T?> GetAsync(string id, string userId)
        {
            try
            {
                // Mirror CosmosDbStore's partition key logic
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
                var url = $"{_baseUrl}/api/GetItem?id={id}&partitionKey={partitionKey}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-functions-key", _functionKey);

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<T>();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets items by user ID.
        /// </summary>
        public async Task<List<T>> GetByUserIdAsync(string userId)
        {
            // Mirror CosmosDbStore's partition key logic
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
            }

            string partitionKey = $"{userId}:{docType}";
            var url = $"{_baseUrl}/api/GetByUserId?partitionKey={partitionKey}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-functions-key", _functionKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<List<T>>();
            return result ?? new List<T>();
        }

        /// <summary>
        /// Upserts a document, matching CosmosDbStore's partition key handling.
        /// </summary>
        public async Task UpsertAsync(T document)
        {
            // Mirror CosmosDbStore's partition key logic
            string id = _idProp.GetValue(document)?.ToString() ?? throw new System.Exception("Document must have Id");
            string userId = _userIdProp.GetValue(document)?.ToString() ?? throw new System.Exception("Document must have UserId");

            string docType = _typeProp.GetValue(document)?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(docType))
            {
                docType = typeof(T).Name;
            }

            string partitionKey = $"{userId}:{docType}";

            // First serialize to JsonNode to add the partitionKey (same as CosmosDbStore)
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            var jsonNode = JsonNode.Parse(json);
            if (jsonNode is JsonObject jsonObject)
            {
                // Add the composite partition key
                jsonObject[COSMOS_PARTITION_KEY_NAME] = partitionKey;

                // Serialize back to string with added partitionKey
                string finalJson = jsonObject.ToJsonString(_jsonOptions);

                var url = $"{_baseUrl}/api/UpsertItem";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-functions-key", _functionKey);
                request.Content = new StringContent(finalJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
        }

        /// <summary>
        /// Upserts multiple documents.
        /// </summary>
        public async Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            // Like CosmosDbStore, we'll upsert one by one to ensure proper partition key handling
            foreach (var document in documents)
            {
                await UpsertAsync(document);
            }
        }

        /// <summary>
        /// Not supported in remote store, just like CosmosDbStore.
        /// </summary>
        public virtual Task<List<T>> GetPendingChangesAsync()
        {
            // Remote store doesn't track pending changes (identical to CosmosDbStore)
            return Task.FromResult(new List<T>());
        }

        /// <summary>
        /// Not supported in remote store, just like CosmosDbStore.
        /// </summary>
        public virtual Task RemovePendingChangeAsync(string id)
        {
            // Remote store doesn't track pending changes (identical to CosmosDbStore)
            return Task.CompletedTask;
        }

        /// <summary>
        /// Not supported in remote store, just like CosmosDbStore.
        /// </summary>
        public virtual Task<List<T>> GetPendingChangesForUserAsync(string userId)
        {
            // Remote store doesn't track pending changes (identical to CosmosDbStore)
            return Task.FromResult(new List<T>());
        }
    }
}
