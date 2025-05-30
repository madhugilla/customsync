using Microsoft.Azure.Cosmos;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System;
using System.Threading.Tasks;

namespace cosmosofflinewithLCC.IntegrationTests
{
    /// <summary>
    /// Test implementation of ICosmosTokenProvider for integration tests
    /// Uses HTTP token provider to call the Azure Function for tokens
    /// </summary>
    public class TestCosmosTokenProvider : ICosmosTokenProvider, IDisposable
    {
        private readonly HttpTokenProvider _httpTokenProvider;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public TestCosmosTokenProvider(string azureFunctionEndpoint, string userId)
        {
            if (string.IsNullOrEmpty(azureFunctionEndpoint))
                throw new ArgumentNullException(nameof(azureFunctionEndpoint));
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentNullException(nameof(userId));

            // Create HTTP client for token requests
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Create HTTP token provider that calls the Azure Function
            var cache = new MemoryCache(new MemoryCacheOptions());
            _httpTokenProvider = new HttpTokenProvider(
                _httpClient,
                azureFunctionEndpoint,
                cache,
                logger: null); // Could add logger if needed

            // Set the user ID
            _httpTokenProvider.SetUserId(userId);
        }

        /// <summary>
        /// Gets a current resource token for Cosmos DB operations
        /// </summary>
        /// <returns>Resource token string</returns>
        public Task<string> GetResourceTokenAsync()
        {
            return _httpTokenProvider.GetResourceTokenAsync();
        }

        /// <summary>
        /// Sets or updates the user ID at runtime
        /// </summary>
        /// <param name="userId">The user ID to use for token requests</param>
        public void SetUserId(string userId)
        {
            _httpTokenProvider.SetUserId(userId);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _httpClient.Dispose();

            _disposed = true;
        }
    }
}
