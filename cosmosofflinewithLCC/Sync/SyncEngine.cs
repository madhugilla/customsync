using System.Diagnostics;
using System.Linq.Expressions;
using cosmosofflinewithLCC.Data;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Sync
{
    /// <summary>
    /// Provides bi-directional synchronization between local and remote data stores.
    /// Each instance can handle synchronization for a specific type and user context.
    /// </summary>
    public class SyncEngine<TDocument> where TDocument : class, new()
    {
        private readonly IDocumentStore<TDocument> _local;
        private readonly IDocumentStore<TDocument> _remote;
        private readonly ILogger _logger;
        private string _userId;  // Changed from readonly to allow updates
        private readonly string _idPropName;
        private readonly string _lastModifiedPropName;

        /// <summary>
        /// Initializes a new instance of the SyncEngine for a specific type and configuration.
        /// </summary>
        /// <param name="local">The local document store</param>
        /// <param name="remote">The remote document store</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="idExpression">Expression to access the ID property</param>
        /// <param name="lastModifiedExpression">Expression to access the LastModified property</param>
        /// <param name="userId">The user ID for filtering data</param>
        public SyncEngine(
            IDocumentStore<TDocument> local,
            IDocumentStore<TDocument> remote,
            ILogger logger,
            Expression<Func<TDocument, object>> idExpression,
            Expression<Func<TDocument, DateTime?>> lastModifiedExpression,
            string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must be provided for sync operations", nameof(userId));
            }

            _local = local;
            _remote = remote;
            _logger = logger;
            _userId = userId;
            _idPropName = GetPropertyName(idExpression);
            _lastModifiedPropName = GetPropertyName(lastModifiedExpression);
        }

        /// <summary>
        /// Updates the user ID for subsequent sync operations
        /// </summary>
        /// <param name="userId">The new user ID to use</param>
        /// <exception cref="ArgumentException">Thrown when userId is null or empty</exception>
        public void UpdateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("userId must not be null or empty", nameof(userId));
            }
            _logger.LogInformation("Updating user ID from {OldUserId} to {NewUserId}", _userId, userId);
            _userId = userId;
        }

        /// <summary>
        /// Synchronizes data between local and remote stores
        /// </summary>
        public async Task SyncAsync()
        {
            _logger.LogInformation("Starting sync process for type {Type} and user {UserId}", typeof(TDocument).Name, _userId);
            int itemsSkipped = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Push local changes to remote
                int itemsPushed = await PushPendingChanges();
                _logger.LogInformation("Pushed {Count} local changes to remote store", itemsPushed);

                // Pull remote changes to local
                int itemsPulled = await PullRemoteChanges();
                _logger.LogInformation("Pulled {Count} changes from remote store", itemsPulled);

                stopwatch.Stop();
                _logger.LogInformation("Sync completed in {ElapsedMilliseconds}ms. Pushed: {ItemsPushed}, Pulled: {ItemsPulled}, Skipped: {ItemsSkipped}",
                    stopwatch.ElapsedMilliseconds, itemsPushed, itemsPulled, itemsSkipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during sync: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Performs an initial data pull without pushing any local changes
        /// </summary>
        public async Task InitialUserDataPullAsync(string docType)
        {
            if (string.IsNullOrEmpty(docType))
            {
                throw new ArgumentException("docType must be provided for initial data pull", nameof(docType));
            }

            _logger.LogInformation("Starting initial data pull for user {UserId} for type {Type}", _userId, docType);

            // Get remote items for the specific user
            _logger.LogInformation("Retrieving items for user {UserId} from remote store", _userId);
            var remoteItems = await _remote.GetByUserIdAsync(_userId);
            _logger.LogInformation("Retrieved {Count} items for user {UserId} from remote store", remoteItems.Count, _userId);

            var itemsToUpsert = new List<TDocument>();

            foreach (var remoteItem in remoteItems)
            {
                if (remoteItem == null) continue;

                var id = typeof(TDocument).GetProperty(_idPropName)?.GetValue(remoteItem)?.ToString();
                if (id == null) continue;

                // Always use efficient point read with partition key
                var localItem = await _local.GetAsync(id, _userId);

                var remoteLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime?;
                var localLast = localItem != null ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localItem) as DateTime? : null;

                // Type is dynamic - get or infer it
                string typeValue = docType;
                var typeProp = typeof(TDocument).GetProperty("Type");
                if (typeProp != null)
                {
                    var currentTypeValue = typeProp.GetValue(remoteItem)?.ToString();
                    if (!string.IsNullOrEmpty(currentTypeValue))
                    {
                        typeValue = currentTypeValue;
                    }
                }
                EnsureCommonProperties(remoteItem, _userId, typeValue);
                itemsToUpsert.Add(remoteItem);
            }
            if (itemsToUpsert.Any())
            {
                // Don't mark initial remote data as pending changes
                await _local.UpsertBulkAsync(itemsToUpsert, false);
                _logger.LogInformation("{Count} items pulled during initial data pull", itemsToUpsert.Count);
            }

            _logger.LogInformation("Initial data pull completed for user {UserId}", _userId);
        }

        private async Task<int> PushPendingChanges()
        {
            int itemsPushed = 0;
            var pendingChanges = await _local.GetPendingChangesAsync();
            _logger.LogInformation("Found {Count} pending changes to sync to remote", pendingChanges.Count);

            // Optimization: Quit early if no pending changes
            if (pendingChanges.Count == 0)
            {
                return 0;
            }

            var itemsToUpsert = new List<TDocument>();
            var idsToRemove = new List<string>();

            // Extract all document ids for batch retrieval
            var documentIds = new List<string>();
            var idToLocalChangesMap = new Dictionary<string, TDocument>();

            // First pass: Set common properties and extract IDs
            foreach (var localChange in pendingChanges)
            {
                // Get or infer the document type
                string docType = typeof(TDocument).Name;
                var typeProp = typeof(TDocument).GetProperty("Type");
                if (typeProp != null)
                {
                    var currentTypeValue = typeProp.GetValue(localChange)?.ToString();
                    if (!string.IsNullOrEmpty(currentTypeValue))
                    {
                        docType = currentTypeValue;
                    }
                }
                EnsureCommonProperties(localChange, _userId, docType);

                var id = typeof(TDocument).GetProperty(_idPropName)?.GetValue(localChange)?.ToString();
                if (string.IsNullOrEmpty(id))
                {
                    _logger.LogWarning("Item has null or empty ID and will be skipped");
                    continue;
                }

                // Add ID to our list for batch retrieval
                documentIds.Add(id);
                idToLocalChangesMap[id] = localChange;
            }

            // Optimization: Batch retrieve all remote items in one query
            Dictionary<string, TDocument> remoteItemsById = new Dictionary<string, TDocument>();

            if (_remote is CosmosDbStore<TDocument> cosmosStore)
            {
                _logger.LogInformation("Using bulk operation to retrieve {Count} remote items", documentIds.Count);
                remoteItemsById = await cosmosStore.GetItemsByIdsAsync(documentIds, _userId);
            }
            else
            {
                // Fall back to individual fetches if not using CosmosDbStore
                foreach (var id in documentIds)
                {
                    var remoteItem = await _remote.GetAsync(id, _userId);
                    if (remoteItem != null)
                    {
                        remoteItemsById[id] = remoteItem;
                    }
                }
            }

            // Second pass: Compare and determine which items need to be updated
            foreach (var id in documentIds)
            {
                var localChange = idToLocalChangesMap[id];
                remoteItemsById.TryGetValue(id, out var remoteItem);

                var localLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localChange) as DateTime?;
                var remoteLast = remoteItem != null
                    ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime?
                    : null;

                var shouldUpdate = remoteItem == null ||
                    (localLast.HasValue && (!remoteLast.HasValue || localLast.Value > remoteLast.Value));

                if (shouldUpdate)
                {
                    _logger.LogInformation(remoteItem == null ?
                        "Preparing new item with Id {Id} for remote" :
                        "Preparing update for item with Id {Id} on remote as local is newer", id);
                    itemsToUpsert.Add(localChange);
                }
                else
                {
                    _logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }

                idsToRemove.Add(id);
            }

            // Perform bulk upsert for all items needing update
            if (itemsToUpsert.Any())
            {
                await _remote.UpsertBulkAsync(itemsToUpsert, true);
                itemsPushed += itemsToUpsert.Count;
            }

            // Remove all items from pending changes
            foreach (var id in idsToRemove)
            {
                await _local.RemovePendingChangeAsync(id);
            }

            return itemsPushed;
        }

        private async Task<int> PullRemoteChanges()
        {
            int itemsPulled = 0;
            var remoteItems = await _remote.GetByUserIdAsync(_userId);
            _logger.LogInformation("Retrieved {Count} items from remote store for user {UserId}", remoteItems.Count, _userId);

            var itemsToUpsert = new List<TDocument>();

            foreach (var remoteItem in remoteItems)
            {
                if (remoteItem == null) continue;

                string docType = typeof(TDocument).Name;
                var id = typeof(TDocument).GetProperty(_idPropName)?.GetValue(remoteItem)?.ToString();
                if (id == null) continue;

                var localItem = await _local.GetAsync(id, _userId);

                // Get timestamps for conflict resolution
                var remoteLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime?;
                var localLast = localItem != null ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localItem) as DateTime? : null;

                // Type is dynamic - get or infer it
                var typeProp = typeof(TDocument).GetProperty("Type");
                if (typeProp != null)
                {
                    var currentTypeValue = typeProp.GetValue(remoteItem)?.ToString();
                    if (!string.IsNullOrEmpty(currentTypeValue))
                    {
                        docType = currentTypeValue;
                    }
                }

                // Last-write-wins strategy
                if (localItem == null || (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast))
                {
                    EnsureCommonProperties(remoteItem, _userId, docType);
                    itemsToUpsert.Add(remoteItem);
                    itemsPulled++;
                }
            }

            if (itemsToUpsert.Any())
            {
                // Don't mark these as pending changes to avoid cyclic syncing
                await _local.UpsertBulkAsync(itemsToUpsert, false);
            }

            return itemsPulled;
        }

        private static void EnsureCommonProperties(TDocument document, string userId, string docType)
        {
            // Set OIID property (userId) if available
            var userIdProp = typeof(TDocument).GetProperty("OIID");
            if (userIdProp != null && userIdProp.CanWrite)
            {
                userIdProp.SetValue(document, userId);
            }
            else
            {
                throw new InvalidOperationException($"Type {typeof(TDocument).Name} must have writable OIID property");
            }

            // Set Type property if available
            var typeProp = typeof(TDocument).GetProperty("Type");
            if (typeProp != null && typeProp.CanWrite)
            {
                typeProp.SetValue(document, docType);
            }
        }

        private static string GetPropertyName<TProperty>(Expression<Func<TDocument, TProperty>> propertyExpression)
        {
            if (propertyExpression.Body is MemberExpression memberExpression)
            {
                return memberExpression.Member.Name;
            }
            else if (propertyExpression.Body is UnaryExpression unaryExpression && unaryExpression.Operand is MemberExpression unaryMemberExpression)
            {
                return unaryMemberExpression.Member.Name;
            }

            throw new ArgumentException("Expression must be a property access expression", nameof(propertyExpression));
        }
    }
}