using System.Threading.Tasks;
using System.Collections.Generic;

namespace cosmosofflinewithLCC.Data
{
    public interface IDocumentStore<T>
    {
        Task<T?> GetAsync(string id);
        Task UpsertAsync(T document);
        Task UpsertBulkAsync(IEnumerable<T> documents);
        Task<List<T>> GetAllAsync();
        Task<List<T>> GetPendingChangesAsync(); // For local store
        Task RemovePendingChangeAsync(string id);  // For local store
        Task<List<T>> GetByUserIdAsync(string userId); // For user-specific querying
    }
}