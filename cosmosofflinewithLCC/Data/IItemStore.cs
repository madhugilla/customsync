namespace cosmosofflinewithLCC.Data
{
    public interface IDocumentStore<T>
    {
        Task<T?> GetAsync(string id, string userId); // Efficient point read with partition key
        Task UpsertAsync(T document);
        Task UpsertBulkAsync(IEnumerable<T> documents);
        Task UpsertBulkAsync(IEnumerable<T> documents, bool markAsPending);
        Task<List<T>> GetAllAsync();
        Task<List<T>> GetPendingChangesAsync(); // For local store
        Task RemovePendingChangeAsync(string id);  // For local store
        Task<List<T>> GetByUserIdAsync(string userId); // For user-specific querying
        Task<List<T>> GetByUserIdAsync(string userId, HashSet<string>? excludeIds); // For user-specific querying with exclusions
        Task<List<T>> GetPendingChangesForUserAsync(string userId); // For user-specific pending changes
    }
}