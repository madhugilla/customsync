using Microsoft.Azure.Cosmos;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.IntegrationTests
{
    /// <summary>
    /// Test implementation of ICosmosClientFactory for integration tests
    /// Uses HTTP token provider to call the Azure Function for tokens
    /// </summary>
    public class TestCosmosClientFactory : ICosmosClientFactory
    {
        private readonly ICosmosClientFactory _innerFactory;

        public TestCosmosClientFactory(string azureFunctionEndpoint, string userId, string cosmosEndpoint)
        {
            if (string.IsNullOrEmpty(azureFunctionEndpoint))
                throw new ArgumentNullException(nameof(azureFunctionEndpoint));
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(cosmosEndpoint))
                throw new ArgumentNullException(nameof(cosmosEndpoint));

            // Create HTTP client for token requests
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };            // Create HTTP token provider that calls the Azure Function
            var cache = new MemoryCache(new MemoryCacheOptions());
            var tokenProvider = new HttpTokenProvider(
                httpClient,
                azureFunctionEndpoint,
                cache,
                logger: null); // Could add logger if needed

            // Set the user ID
            tokenProvider.SetUserId(userId);

            // Create the actual factory with token provider
            _innerFactory = new CosmosClientFactory(tokenProvider, cosmosEndpoint);
        }

        /// <summary>
        /// Creates a CosmosClient using a fresh token from the Azure Function
        /// </summary>
        public async Task<CosmosClient> CreateClientAsync()
        {
            return await _innerFactory.CreateClientAsync();
        }

        /// <summary>
        /// Gets a container using a fresh token from the Azure Function
        /// </summary>
        public async Task<Container> GetContainerAsync(string databaseId, string containerId)
        {
            return await _innerFactory.GetContainerAsync(databaseId, containerId);
        }
    }
}
