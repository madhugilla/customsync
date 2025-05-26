using Microsoft.Azure.Cosmos;
using cosmosofflinewithLCC.Data;

namespace cosmosofflinewithLCC.IntegrationTests
{
    /// <summary>
    /// Test implementation of ICosmosClientFactory for integration tests
    /// Wraps an existing Container instance to work with the factory pattern
    /// </summary>
    public class TestCosmosClientFactory : ICosmosClientFactory
    {
        private readonly Container _container;

        public TestCosmosClientFactory(Container container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
        }

        /// <summary>
        /// Returns the CosmosClient from the existing container
        /// </summary>
        public Task<CosmosClient> CreateClientAsync()
        {
            // Get the client from the container's database
            var client = _container.Database.Client;
            return Task.FromResult(client);
        }

        /// <summary>
        /// Returns the test container instance
        /// Ignores the databaseId and containerId parameters for testing
        /// </summary>
        public Task<Container> GetContainerAsync(string databaseId, string containerId)
        {
            return Task.FromResult(_container);
        }
    }
}
