using System.Diagnostics;
using System.Linq.Expressions;
using System.Linq;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Sync
{
    /// <summary>
    /// TODO: implement soft deletes
    /// </summary>
    public static class SyncEngine
    {
        private static string GetPropertyName<T, TProperty>(Expression<Func<T, TProperty>> propertyExpression)
        {
            if (propertyExpression.Body is MemberExpression memberExpression)
            {
                return memberExpression.Member.Name;
            }
            else if (propertyExpression.Body is UnaryExpression unaryExpression && unaryExpression.Operand is MemberExpression unaryMemberExpression)
            {
                // Handle cases where the expression is wrapped in a Convert or similar unary operation
                return unaryMemberExpression.Member.Name;
            }

            throw new ArgumentException("Invalid property expression. Ensure the expression is a simple property access, e.g., x => x.PropertyName.");
        }

        public static async Task SyncAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, Expression<Func<T, object>> idExpression, Expression<Func<T, DateTime?>> lastModifiedExpression, string userId = null) where T : class, new()
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
                int itemsPushed = await PushPendingChanges(local, remote, logger, idPropName, lastModifiedPropName, userId);

                // Pull remote changes to local
                int itemsPulled = await PullRemoteChanges(local, remote, logger, idPropName, lastModifiedPropName, userId);

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

        private static async Task<int> PushPendingChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName, string userId = null) where T : class, new()
        {
            int itemsPushed = 0;

            // Get pending changes
            List<T> pending;

            if (userId != null && local is SqliteStore<T> sqliteStore)
            {
                // Use the optimized method for SqliteStore if we have a userId
                // Since userId is mandatory, this will be more efficient than the general method
                pending = await sqliteStore.GetPendingChangesForUserAsync(userId);
                logger.LogInformation("Used optimized query to retrieve {Count} pending changes for user {UserId}", pending.Count, userId);
            }
            else
            {
                // Fall back to standard method if we can't use the optimized path
                pending = await local.GetPendingChangesAsync();

                // If userId is provided, filter pending changes to only include those for the current user
                // This is safe because userId is guaranteed to be present on all documents
                if (userId != null)
                {
                    var userIdProp = typeof(T).GetProperty("UserId");
                    pending = pending.Where(item =>
                        userId.Equals(userIdProp?.GetValue(item)?.ToString(),
                        StringComparison.OrdinalIgnoreCase)).ToList();
                    logger.LogInformation("Filtered to {Count} pending changes for user {UserId}", pending.Count, userId);
                }
            }

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

        private static async Task<int> PullRemoteChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName, string userId = null) where T : class, new()
        {
            int itemsPulled = 0;

            // Get all remote items, filtered by user if provided
            List<T> remoteItems;

            if (!string.IsNullOrEmpty(userId))
            {
                // Use user-specific query if userId is provided
                remoteItems = await remote.GetByUserIdAsync(userId);
                logger.LogInformation("Retrieved {Count} items for user {UserId} from remote store", remoteItems.Count, userId);
            }
            else
            {
                // No user ID provided, retrieve all items (not recommended for production)
                remoteItems = await remote.GetAllAsync();
                logger.LogWarning("No userId provided for filtering. Retrieved all {Count} items from remote store.", remoteItems.Count);
            }

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

        /// <summary>
        /// Performs an initial data pull for a specific user without pushing any local changes
        /// </summary>
        public static async Task InitialUserDataPullAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger,
            Expression<Func<T, object>> idExpression, Expression<Func<T, DateTime?>> lastModifiedExpression, string userId)
            where T : class, new()
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must be provided for initial user data pull", nameof(userId));
            }

            logger.LogInformation("Starting initial data pull for user {UserId} for type {Type}", userId, typeof(T).Name);
            var idPropName = GetPropertyName(idExpression);
            var lastModifiedPropName = GetPropertyName(lastModifiedExpression);

            await PullRemoteChanges(local, remote, logger, idPropName, lastModifiedPropName, userId);

            logger.LogInformation("Initial data pull completed for user {UserId}", userId);
        }
    }
}