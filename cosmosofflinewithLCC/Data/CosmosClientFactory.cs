using System;
using Microsoft.Azure.Cosmos;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// Factory that creates CosmosClients with fresh resource tokens on demand
    /// </summary>
    public class CosmosClientFactory : ICosmosClientFactory
    {
        private readonly ICosmosTokenProvider _tokenProvider;
        private readonly string _cosmosEndpoint;
        private readonly CosmosClientOptions _clientOptions; public CosmosClientFactory(
            ICosmosTokenProvider tokenProvider,
            string cosmosEndpoint,
            CosmosClientOptions? clientOptions = null)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _cosmosEndpoint = cosmosEndpoint ?? throw new ArgumentNullException(nameof(cosmosEndpoint));
            _clientOptions = clientOptions ?? GetDefaultOptions();
        }

        /// <summary>
        /// Constructor for common configuration scenarios
        /// </summary>
        public CosmosClientFactory(
            ICosmosTokenProvider tokenProvider,
            string cosmosEndpoint,
            bool isDevelopment)
            : this(tokenProvider, cosmosEndpoint, GetConfiguredOptions(isDevelopment))
        {
        }

        /// <summary>
        /// Gets default CosmosClientOptions with optimal settings
        /// </summary>
        private static CosmosClientOptions GetDefaultOptions()
        {
            var isDevelopment = Environment.GetEnvironmentVariable("ENVIRONMENT") == "Development" ||
                               Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";

            return new CosmosClientOptions
            {
                ConnectionMode = isDevelopment ? ConnectionMode.Gateway : ConnectionMode.Direct,
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                },
                // Optimize for token-based authentication scenarios
                MaxRetryAttemptsOnRateLimitedRequests = 3,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
                // Disable bulk support for token scenarios (can cause issues with resource tokens)
                AllowBulkExecution = false,
                // Conservative request timeout for token scenarios
                RequestTimeout = TimeSpan.FromSeconds(60)
            };
        }

        /// <summary>
        /// Gets CosmosClientOptions configured for specific environments
        /// </summary>
        private static CosmosClientOptions GetConfiguredOptions(bool isDevelopment)
        {
            return new CosmosClientOptions
            {
                ConnectionMode = isDevelopment ? ConnectionMode.Gateway : ConnectionMode.Direct,
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                },
                MaxRetryAttemptsOnRateLimitedRequests = isDevelopment ? 5 : 3,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(isDevelopment ? 60 : 30),
                AllowBulkExecution = false,
                RequestTimeout = TimeSpan.FromSeconds(isDevelopment ? 120 : 60)
            };
        }

        /// <summary>
        /// Creates a new CosmosClient with a fresh resource token
        /// </summary>
        public async Task<CosmosClient> CreateClientAsync()
        {
            var token = await _tokenProvider.GetResourceTokenAsync();
            return new CosmosClient(_cosmosEndpoint, token, _clientOptions);
        }

        /// <summary>
        /// Gets a container with a fresh token for operations
        /// Each call creates a new client with a fresh token
        /// </summary>
        public async Task<Container> GetContainerAsync(string databaseId, string containerId)
        {
            var client = await CreateClientAsync();
            return client.GetDatabase(databaseId).GetContainer(containerId);
        }
    }
}
