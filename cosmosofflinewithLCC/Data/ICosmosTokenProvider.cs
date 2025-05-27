namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// Provides resource tokens for Cosmos DB authentication
    /// </summary>
    public interface ICosmosTokenProvider
    {
        /// <summary>
        /// Gets a current resource token for Cosmos DB operations
        /// </summary>
        /// <returns>Resource token string</returns>
        Task<string> GetResourceTokenAsync();
        
        /// <summary>
        /// Sets or updates the user ID at runtime
        /// </summary>
        /// <param name="userId">The user ID to use for token requests</param>
        void SetUserId(string userId);
    }
}