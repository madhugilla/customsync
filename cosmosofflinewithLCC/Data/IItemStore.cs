using System.Threading.Tasks;
using System.Collections.Generic;

namespace cosmosofflinewithLCC.Data
{
    public interface IDocumentStore<T>
    {
        Task<T?> GetAsync(string id);
        Task UpsertAsync(T document);
        Task<List<T>> GetAllAsync();
        Task<List<T>> GetPendingChangesAsync(); // For local store
        Task RemovePendingChangeAsync(string id);  // For local store
    }
}