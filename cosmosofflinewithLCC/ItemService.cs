using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace cosmosofflinewithLCC
{
    public class ItemService
    {
        private readonly SyncEngine<Item> _syncEngine;
        private readonly IDocumentStore<Item> _localStore;
        private readonly CosmosDbStore<Item> _remoteStore;
        private readonly ILogger<ItemService> _logger;
        private readonly string _currentUserId;

        public ItemService(
            SyncEngine<Item> syncEngine,
            IDocumentStore<Item> localStore,
            CosmosDbStore<Item> remoteStore,
            ILogger<ItemService> logger)
        {
            _syncEngine = syncEngine;
            _localStore = localStore;
            _remoteStore = remoteStore;
            _logger = logger;
            _currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1"; // Or get from auth service
        }

        public async Task InitialDataPullAsync()
        {
            _logger.LogInformation("Performing initial data pull for Items for user {UserId}...", _currentUserId);
            await _syncEngine.InitialUserDataPullAsync("Item");
            _logger.LogInformation("Initial data pull for Items completed for user {UserId}.", _currentUserId);
        }

        public async Task AddOrUpdateLocalItemAsync(Item item)
        {
            item.OIID = _currentUserId; // Ensure OIID is set
            item.Type = "Item"; // Ensure Type is set
            await _localStore.UpsertAsync(item);
            _logger.LogInformation("Local item {ItemId} upserted for user {UserId}.", item.ID, _currentUserId);
        }

        public async Task AddOrUpdateRemoteItemAsync(Item item)
        {
            item.OIID = _currentUserId; // Ensure OIID is set
            item.Type = "Item"; // Ensure Type is set
            await _remoteStore.UpsertAsync(item);
            _logger.LogInformation("Remote item {ItemId} upserted for user {UserId}.", item.ID, _currentUserId);
        }

        public async Task<Item?> GetLocalItemAsync(string id)
        {
            return await _localStore.GetAsync(id, _currentUserId);
        }

        public async Task<Item?> GetRemoteItemAsync(string id)
        {
            return await _remoteStore.GetAsync(id, _currentUserId);
        }

        public async Task SyncItemsAsync()
        {
            _logger.LogInformation("Starting sync for Items for user {UserId}...", _currentUserId);
            await _syncEngine.SyncAsync();
            _logger.LogInformation("Sync for Items completed for user {UserId}.", _currentUserId);
        }

        public async Task<bool> IsLocalStoreEmptyAsync()
        {
            var items = await _localStore.GetAllAsync();
            return items.Count == 0;
        }
    }
}
