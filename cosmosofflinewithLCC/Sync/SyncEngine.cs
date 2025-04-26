using System.Diagnostics;
using System.Linq.Expressions;
using System.Linq;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Sync
{
    public static class SyncEngine
    {
        private static string GetPropertyName<T, TProperty>(Expression<Func<T, TProperty>> propertyExpression)
        {
            if (propertyExpression.Body is MemberExpression memberExpression)
            {
                return memberExpression.Member.Name;
            }
            throw new ArgumentException("Invalid property expression");
        }

        public static async Task SyncAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, Expression<Func<T, object>> idExpression, Expression<Func<T, DateTime?>> lastModifiedExpression) where T : class, new()
        {
            var idPropName = GetPropertyName(idExpression);
            var lastModifiedPropName = GetPropertyName(lastModifiedExpression);

            logger.LogInformation("Starting sync process for type {Type}", typeof(T).Name);
            int itemsSkipped = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Metrics
                // Push local pending changes to remote
                int itemsPushed = await PushPendingChanges(local, remote, logger, idPropName, lastModifiedPropName);

                // Pull remote changes to local
                int itemsPulled = await PullRemoteChanges(local, remote, logger, idPropName, lastModifiedPropName);

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

        private static async Task<int> PushPendingChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName) where T : class, new()
        {
            int itemsPushed = 0;
            var pending = await local.GetPendingChangesAsync();
            logger.LogInformation("Found {Count} pending changes to push to remote", pending.Count);

            var itemsToUpsert = new List<T>();
            var idsToRemove = new List<string>();

            foreach (var localChange in pending)
            {
                var id = typeof(T).GetProperty(idPropName)?.GetValue(localChange)?.ToString();
                var remoteItem = id != null ? await remote.GetAsync(id) : null;
                var localLast = typeof(T).GetProperty(lastModifiedPropName)?.GetValue(localChange) as DateTime?;
                var remoteLast = remoteItem != null ? typeof(T).GetProperty(lastModifiedPropName)?.GetValue(remoteItem) as DateTime? : null;

                if (remoteItem == null || (localLast.HasValue && remoteLast.HasValue && localLast > remoteLast))
                {
                    logger.LogInformation(remoteItem == null ? "Preparing new item with Id {Id} for remote" : "Preparing update for item with Id {Id} on remote as local is newer", id);
                    itemsToUpsert.Add(localChange);
                }
                else
                {
                    logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }

                if (id != null)
                {
                    idsToRemove.Add(id);
                }
            }

            if (itemsToUpsert.Any())
            {
                await remote.UpsertBulkAsync(itemsToUpsert);
                itemsPushed += itemsToUpsert.Count;
            }

            foreach (var id in idsToRemove)
            {
                await local.RemovePendingChangeAsync(id);
            }

            return itemsPushed;
        }

        private static async Task<int> PullRemoteChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName) where T : class, new()
        {
            int itemsPulled = 0;
            var remoteItems = await remote.GetAllAsync();
            logger.LogInformation("Found {Count} items on remote to sync to local", remoteItems.Count);

            var itemsToUpsert = new List<T>();

            foreach (var remoteItem in remoteItems)
            {
                if (remoteItem == null) continue;

                var id = typeof(T).GetProperty(idPropName)?.GetValue(remoteItem)?.ToString();
                var localItem = id != null ? await local.GetAsync(id) : null;
                var remoteLast = typeof(T).GetProperty(lastModifiedPropName)?.GetValue(remoteItem) as DateTime?;
                var localLast = localItem != null ? typeof(T).GetProperty(lastModifiedPropName)?.GetValue(localItem) as DateTime? : null;

                if (localItem == null || (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast))
                {
                    logger.LogInformation(localItem == null ? "Preparing new item with Id {Id} for local" : "Preparing update for item with Id {Id} on local as remote is newer", id);
                    itemsToUpsert.Add(remoteItem);
                }
                else
                {
                    logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }
            }

            if (itemsToUpsert.Any())
            {
                await local.UpsertBulkAsync(itemsToUpsert);
                itemsPulled += itemsToUpsert.Count;
            }

            return itemsPulled;
        }
    }
}