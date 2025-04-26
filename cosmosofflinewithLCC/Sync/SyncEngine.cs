using System.Diagnostics;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Sync
{
    public static class SyncEngine
    {
        public static async Task SyncAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger) where T : class, new()
        {
            var idProp = typeof(T).GetProperty("Id") ?? throw new System.Exception("Model must have Id property");
            var lastModifiedProp = typeof(T).GetProperty("LastModified") ?? throw new System.Exception("Model must have LastModified property");

            logger.LogInformation("Starting sync process for type {Type}", typeof(T).Name);

            // Metrics
            int itemsPushed = 0;
            int itemsPulled = 0;
            int itemsSkipped = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Push local pending changes to remote
                var pending = await local.GetPendingChangesAsync();
                logger.LogInformation("Found {Count} pending changes to push to remote", pending.Count);

                foreach (var localChange in pending)
                {
                    var id = idProp.GetValue(localChange)?.ToString();
                    var remoteItem = id != null ? await remote.GetAsync(id) : null;
                    var localLast = lastModifiedProp.GetValue(localChange) as System.DateTime?;
                    var remoteLast = remoteItem != null ? lastModifiedProp.GetValue(remoteItem) as System.DateTime? : null;

                    if (remoteItem == null)
                    {
                        logger.LogInformation("Pushing new item with Id {Id} to remote", id);
                        await remote.UpsertAsync(localChange);
                        itemsPushed++;
                    }
                    else if (localLast.HasValue && remoteLast.HasValue && localLast > remoteLast)
                    {
                        logger.LogInformation("Updating item with Id {Id} on remote as local is newer", id);
                        await remote.UpsertAsync(localChange);
                        itemsPushed++;
                    }
                    else
                    {
                        itemsSkipped++;
                    }

                    if (id != null)
                    {
                        logger.LogInformation("Removing pending change for Id {Id}", id);
                        await local.RemovePendingChangeAsync(id);
                    }
                }

                // 2. Pull remote changes to local
                var remoteItems = await remote.GetAllAsync();
                logger.LogInformation("Found {Count} items on remote to sync to local", remoteItems.Count);

                foreach (var remoteItem in remoteItems)
                {
                    if (remoteItem == null) continue;

                    var id = idProp.GetValue(remoteItem)?.ToString();
                    var localItem = id != null ? await local.GetAsync(id) : null;
                    var remoteLast = lastModifiedProp.GetValue(remoteItem) as System.DateTime?;
                    var localLast = localItem != null ? lastModifiedProp.GetValue(localItem) as System.DateTime? : null;

                    if (localItem == null)
                    {
                        logger.LogInformation("Pulling new item with Id {Id} to local", id);
                        await local.UpsertAsync(remoteItem);
                        itemsPulled++;
                    }
                    else if (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast)
                    {
                        logger.LogInformation("Updating item with Id {Id} on local as remote is newer", id);
                        await local.UpsertAsync(remoteItem);
                        itemsPulled++;
                    }
                    else
                    {
                        itemsSkipped++;
                    }
                }

                stopwatch.Stop();
                logger.LogInformation("Sync process completed successfully for type {Type} in {ElapsedMilliseconds} ms", typeof(T).Name, stopwatch.ElapsedMilliseconds);
                logger.LogInformation("Metrics: {ItemsPushed} items pushed, {ItemsPulled} items pulled, {ItemsSkipped} items skipped", itemsPushed, itemsPulled, itemsSkipped);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during the sync process for type {Type}", typeof(T).Name);
                throw;
            }
        }
    }
}