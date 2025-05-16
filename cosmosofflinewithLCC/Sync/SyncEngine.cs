using System.Diagnostics;
using System.Linq.Expressions;
using System.Linq;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Sync
{
    /// <summary>
    /// TODO: implement soft deletes
    /// </summary>\
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

        /// <summary>
        /// Ensures a document has the required properties for Cosmos DB
        /// </summary>
        private static void EnsureCosmosProperties<T>(T document, string userId, string docType) where T : class, new()
        {
            // Set the userId (partition key) if the property exists
            var userIdProp = typeof(T).GetProperty("UserId");
            if (userIdProp != null && userIdProp.CanWrite)
            {
                // Always set the userId to match the provided value to ensure partition key consistency
                // This is critical for Cosmos DB operations
                userIdProp.SetValue(document, userId);

                Console.WriteLine($"Set UserId to {userId} for document");
            }
            else
            {
                Console.WriteLine($"WARNING: UserId property not found on type {typeof(T).Name}");
            }

            // Set the type property if it exists
            var typeProp = typeof(T).GetProperty("Type");
            if (typeProp != null && typeProp.CanWrite)
            {
                // Set the type property to the provided document type
                typeProp.SetValue(document, docType);
                Console.WriteLine($"Set Type to {docType} for document");
            }
        }

        /// <summary>
        /// Synchronizes data between local and remote stores for a specific user
        /// </summary>
        public static async Task SyncAsync<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, Expression<Func<T, object>> idExpression, Expression<Func<T, DateTime?>> lastModifiedExpression, string userId) where T : class, new()
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must be provided for sync operations", nameof(userId));
            }

            var idPropName = GetPropertyName(idExpression);
            var lastModifiedPropName = GetPropertyName(lastModifiedExpression);

            logger.LogInformation("Starting sync process for type {Type} and user {UserId}", typeof(T).Name, userId);
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
                logger.LogInformation("Sync process completed successfully for type {Type} and user {UserId} in {ElapsedMilliseconds} ms", typeof(T).Name, userId, stopwatch.ElapsedMilliseconds);
                logger.LogInformation("Metrics: {ItemsPushed} items pushed, {ItemsPulled} items pulled, {ItemsSkipped} items skipped", itemsPushed, itemsPulled, itemsSkipped);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during the sync process for type {Type} and user {UserId}", typeof(T).Name, userId);
                throw;
            }
        }

        private static async Task<int> PushPendingChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName, string userId) where T : class, new()
        {
            int itemsPushed = 0;

            // Get pending changes for the specific user
            List<T> pending;

            if (local is SqliteStore<T> sqliteStore)
            {
                // Use the optimized method for SqliteStore
                pending = await sqliteStore.GetPendingChangesForUserAsync(userId);
                logger.LogInformation("Used optimized query to retrieve {Count} pending changes for user {UserId}", pending.Count, userId);
            }
            else
            {
                // Fall back to standard method if we can't use the optimized path
                pending = await local.GetPendingChangesAsync();

                // Filter pending changes to only include those for the current user
                var userIdProp = typeof(T).GetProperty("UserId");
                pending = pending.Where(item =>
                    userId.Equals(userIdProp?.GetValue(item)?.ToString(),
                    StringComparison.OrdinalIgnoreCase)).ToList();
                logger.LogInformation("Filtered to {Count} pending changes for user {UserId}", pending.Count, userId);
            }

            logger.LogInformation("Found {Count} pending changes to push to remote", pending.Count);

            var itemsToUpsert = new List<T>();
            var idsToRemove = new List<string>();

            foreach (var localChange in pending)
            {
                // Ensure document has required Cosmos DB properties before sending to remote
                if (remote is CosmosDbStore<T>)
                {
                    // Get the existing type if available
                    string docType = typeof(T).Name;
                    var typeProp = typeof(T).GetProperty("Type");
                    if (typeProp != null)
                    {
                        var currentTypeValue = typeProp.GetValue(localChange)?.ToString();
                        if (!string.IsNullOrEmpty(currentTypeValue))
                        {
                            docType = currentTypeValue;
                        }
                    }

                    // Always ensure the userId and type are set for proper partitioning and querying
                    EnsureCosmosProperties(localChange, userId, docType);
                }

                var id = typeof(T).GetProperty(idPropName)?.GetValue(localChange)?.ToString();
                if (string.IsNullOrEmpty(id))
                {
                    logger.LogWarning("Item has null or empty ID and will be skipped");
                    continue;
                }

                var remoteItem = await remote.GetAsync(id);
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

                idsToRemove.Add(id);
            }
            if (itemsToUpsert.Any())
            {
                // Important: Ensure each item in the bulk operation has the correct partition key
                foreach (var item in itemsToUpsert)
                {
                    // Ensure all items in the bulk operation have the correct userId and type
                    // Get the existing type if available
                    string docType = typeof(T).Name;
                    var typeProp = typeof(T).GetProperty("Type");
                    if (typeProp != null)
                    {
                        var currentTypeValue = typeProp.GetValue(item)?.ToString();
                        if (!string.IsNullOrEmpty(currentTypeValue))
                        {
                            docType = currentTypeValue;
                        }
                    }

                    // Always ensure proper properties are set
                    EnsureCosmosProperties(item, userId, docType);
                }

                await remote.UpsertBulkAsync(itemsToUpsert);
                itemsPushed += itemsToUpsert.Count;
            }

            foreach (var id in idsToRemove)
            {
                await local.RemovePendingChangeAsync(id);
            }

            return itemsPushed;
        }

        private static async Task<int> PullRemoteChanges<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string idPropName, string lastModifiedPropName, string userId) where T : class, new()
        {
            int itemsPulled = 0;

            // Get remote items for the specific user
            logger.LogInformation("Retrieving items for user {UserId} from remote store", userId);
            var remoteItems = await remote.GetByUserIdAsync(userId);
            logger.LogInformation("Retrieved {Count} items for user {UserId} from remote store", remoteItems.Count, userId);

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
            Expression<Func<T, object>> idExpression, Expression<Func<T, DateTime?>> lastModifiedExpression,
            string userId, string docType) where T : class, new()
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must be provided for initial user data pull", nameof(userId));
            }

            if (string.IsNullOrEmpty(docType))
            {
                throw new ArgumentException("docType must be provided for initial data pull", nameof(docType));
            }

            logger.LogInformation("Starting initial data pull for user {UserId} for type {Type}", userId, docType);

            var idPropName = GetPropertyName(idExpression);
            var lastModifiedPropName = GetPropertyName(lastModifiedExpression);

            await PullRemoteChanges(local, remote, logger, idPropName, lastModifiedPropName, userId);

            logger.LogInformation("Initial data pull completed for user {UserId}", userId);
        }
    }
}