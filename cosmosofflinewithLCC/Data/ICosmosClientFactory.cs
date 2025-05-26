using Microsoft.Azure.Cosmos;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// Factory for creating Cosmos clients and containers with token-based authentication
    /// </summary>
    public interface ICosmosClientFactory
    {
        /// <summary>
        /// Creates a new CosmosClient with a fresh resource token
        /// </summary>
        /// <returns>New CosmosClient instance</returns>
        Task<CosmosClient> CreateClientAsync();

        /// <summary>
        /// Gets a container with a fresh token for operations
        /// </summary>
        /// <param name="databaseId">Database identifier</param>
        /// <param name="containerId">Container identifier</param>
        /// <returns>Container instance with fresh authentication</returns>
        Task<Container> GetContainerAsync(string databaseId, string containerId);
    }
}
