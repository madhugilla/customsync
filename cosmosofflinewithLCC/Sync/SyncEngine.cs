using System.Reflection;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;

namespace cosmosofflinewithLCC.Sync
{
    // Generic sync logic with Last Write Wins (async)
    public static class SyncEngine
    {
        public static async Task SyncAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote) where T : class, new()
        {
            var idProp = typeof(T).GetProperty("Id") ?? throw new System.Exception("Model must have Id property");
            var lastModifiedProp = typeof(T).GetProperty("LastModified") ?? throw new System.Exception("Model must have LastModified property");

            // 1. Push local pending changes to remote
            var pending = await local.GetPendingChangesAsync();
            foreach (var localChange in pending)
            {
                var id = idProp.GetValue(localChange)?.ToString();
                var remoteItem = id != null ? await remote.GetAsync(id) : null;
                var localLast = lastModifiedProp.GetValue(localChange) as System.DateTime?;
                var remoteLast = remoteItem != null ? lastModifiedProp.GetValue(remoteItem) as System.DateTime? : null;

                if (remoteItem == null)
                {
                    await remote.UpsertAsync(localChange);
                }
                else if (localLast.HasValue && remoteLast.HasValue && localLast > remoteLast)
                {
                    await remote.UpsertAsync(localChange);
                }
                // Always remove the pending change after processing
                if (id != null)
                    await local.RemovePendingChangeAsync(id);
            }

            // 2. Pull remote changes to local
            var remoteItems = await remote.GetAllAsync();
            foreach (var remoteItem in remoteItems)
            {
                if (remoteItem == null) continue; // Skip null remote items

                var id = idProp.GetValue(remoteItem)?.ToString();
                var localItem = id != null ? await local.GetAsync(id) : null;
                var remoteLast = lastModifiedProp.GetValue(remoteItem) as System.DateTime?;
                var localLast = localItem != null ? lastModifiedProp.GetValue(localItem) as System.DateTime? : null;

                if (localItem == null)
                {
                    await local.UpsertAsync(remoteItem);
                }
                else if (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast)
                {
                    await local.UpsertAsync(remoteItem);
                }
            }
        }
    }
}