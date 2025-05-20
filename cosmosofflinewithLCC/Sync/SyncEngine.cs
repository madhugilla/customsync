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
                // Push local pending changes to remote
                int itemsPushed = await PushPendingChanges();

                // Pull remote changes to local
                int itemsPulled = await PullRemoteChanges();

                stopwatch.Stop();
                _logger.LogInformation("Sync process completed successfully for type {Type} and user {UserId} in {ElapsedMilliseconds} ms",
                    typeof(TDocument).Name, _userId, stopwatch.ElapsedMilliseconds);
                _logger.LogInformation("Metrics: {ItemsPushed} items pushed, {ItemsPulled} items pulled, {ItemsSkipped} items skipped",
                    itemsPushed, itemsPulled, itemsSkipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the sync process for type {Type} and user {UserId}", typeof(TDocument).Name, _userId);
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
                TDocument? localItem = await _local.GetAsync(id, _userId);

                var remoteLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime?;
                var localLast = localItem != null
                    ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localItem) as DateTime?
                    : null;

                if (localItem == null || (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast))
                {
                    _logger.LogInformation(localItem == null
                        ? "Preparing new item with Id {Id} for local"
                        : "Preparing update for item with Id {Id} on local as remote is newer", id);

                    // Check if Type property exists and use it, otherwise use the provided docType
                    var typeProp = typeof(TDocument).GetProperty("Type");
                    string typeValue = docType;
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
                else
                {
                    _logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }
            }

            if (itemsToUpsert.Any())
            {
                await _local.UpsertBulkAsync(itemsToUpsert);
                _logger.LogInformation("{Count} items pulled during initial data pull", itemsToUpsert.Count);
            }

            _logger.LogInformation("Initial data pull completed for user {UserId}", _userId);
        }

        private async Task<int> PushPendingChanges()
        {
            int itemsPushed = 0;
            var pendingChanges = await _local.GetPendingChangesAsync();
            _logger.LogInformation("Found {Count} pending changes to sync to remote", pendingChanges.Count);

            var itemsToUpsert = new List<TDocument>();
            var idsToRemove = new List<string>();

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

                // Always use efficient point read with partition key
                _logger.LogInformation("Using efficient point read with partition key for item {Id}", id);
                var remoteItem = await _remote.GetAsync(id, _userId);

                var localLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localChange) as DateTime?;
                var remoteLast = remoteItem != null ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime? : null;

                if (remoteItem == null || (localLast.HasValue && remoteLast.HasValue && localLast > remoteLast))
                {
                    _logger.LogInformation(remoteItem == null ? "Preparing new item with Id {Id} for remote" : "Preparing update for item with Id {Id} on remote as local is newer", id);
                    itemsToUpsert.Add(localChange);
                }
                else
                {
                    _logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }

                idsToRemove.Add(id);
            }

            if (itemsToUpsert.Any())
            {
                await _remote.UpsertBulkAsync(itemsToUpsert);
                itemsPushed += itemsToUpsert.Count;
            }

            foreach (var id in idsToRemove)
            {
                await _local.RemovePendingChangeAsync(id);
            }

            return itemsPushed;
        }

        private async Task<int> PullRemoteChanges()
        {
            int itemsPulled = 0;

            // Get remote items for the specific user
            _logger.LogInformation("Retrieving items for user {UserId} from remote store", _userId);
            var remoteItems = await _remote.GetByUserIdAsync(_userId);
            _logger.LogInformation("Retrieved {Count} items for user {UserId} from remote store", remoteItems.Count, _userId);

            _logger.LogInformation("Found {Count} items on remote to sync to local", remoteItems.Count);

            var itemsToUpsert = new List<TDocument>();

            foreach (var remoteItem in remoteItems)
            {
                if (remoteItem == null) continue;
                var id = typeof(TDocument).GetProperty(_idPropName)?.GetValue(remoteItem)?.ToString();

                // Always use efficient point read with partition key
                TDocument? localItem = null;
                if (id != null)
                {
                    _logger.LogInformation("Using efficient point read with userId for local item {Id}", id);
                    localItem = await _local.GetAsync(id, _userId);
                }

                var remoteLast = typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(remoteItem) as DateTime?;
                var localLast = localItem != null ? typeof(TDocument).GetProperty(_lastModifiedPropName)?.GetValue(localItem) as DateTime? : null;

                if (localItem == null || (remoteLast.HasValue && localLast.HasValue && remoteLast > localLast))
                {
                    _logger.LogInformation(localItem == null ? "Preparing new item with Id {Id} for local" : "Preparing update for item with Id {Id} on local as remote is newer", id);
                    itemsToUpsert.Add(remoteItem);
                }
                else
                {
                    _logger.LogInformation("Skipping item with Id {Id} as no update is needed", id);
                }
            }

            if (itemsToUpsert.Any())
            {
                await _local.UpsertBulkAsync(itemsToUpsert);
                itemsPulled += itemsToUpsert.Count;
            }

            return itemsPulled;
        }

        private static void EnsureCommonProperties(TDocument document, string userId, string docType)
        {
            // Set the userId if the property exists
            var userIdProp = typeof(TDocument).GetProperty("UserId");
            if (userIdProp != null && userIdProp.CanWrite)
            {
                userIdProp.SetValue(document, userId);
            }
            else
            {
                Console.WriteLine($"WARNING: UserId property not found on type {typeof(TDocument).Name}");
            }

            // Set the type property if it exists
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
                // Handle cases where the expression is wrapped in a Convert or similar unary operation
                return unaryMemberExpression.Member.Name;
            }

            throw new ArgumentException("Invalid property expression. Ensure the expression is a simple property access, e.g., x => x.PropertyName.");
        }
    }
}