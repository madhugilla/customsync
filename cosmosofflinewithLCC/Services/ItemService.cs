using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Services
{
    public class ItemService
    {
        private readonly SyncEngine<Item> _syncEngine;
        private readonly IDocumentStore<Item> _localStore;
        private readonly CosmosDbStore<Item> _remoteStore;
        private readonly ILogger<ItemService> _logger;
        private readonly string _currentUserId;

        public ItemService()
        {
            _currentUserId = "cosmosUser";

            // Create logger factory and logger
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<ItemService>();

            _localStore = new SqliteStore<Item>("itemService.db");            // Create token provider
            var tokenEndpoint = "http://localhost:7071/api/GetCosmosToken";
            var httpClient = new HttpClient();
            var httpTokenLogger = loggerFactory.CreateLogger<HttpTokenProvider>();
            var tokenProvider = new HttpTokenProvider(httpClient, tokenEndpoint, _currentUserId, httpTokenLogger);
            tokenProvider.SetUserId("cosmosUser"); // Set user ID for token provider

            // Create cosmos client factory
            var cosmosEndpoint = "https://localhost:8081";
            var clientFactory = new CosmosClientFactory(tokenProvider, cosmosEndpoint, true);

            // Create remote store
            var databaseId = "SyncTestDB";
            var containerId = "SyncTestContainer";
            _remoteStore = new CosmosDbStore<Item>(clientFactory, databaseId, containerId);

            // Create sync engine
            _syncEngine = new SyncEngine<Item>(
                _localStore,
                _remoteStore,
                _logger,
                item => item.ID,
                item => item.LastModified,
                _currentUserId);
            _syncEngine.UpdateUserId(_currentUserId); // Set user ID for sync engine
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
            _logger.LogInformation("Local Item {ItemId} upserted for user {UserId}.", item.ID, _currentUserId);
        }

        public async Task AddOrUpdateRemoteItemAsync(Item item)
        {
            item.OIID = _currentUserId; // Ensure OIID is set
            item.Type = "Item"; // Ensure Type is set
            await _remoteStore.UpsertAsync(item);
            _logger.LogInformation("Remote Item {ItemId} upserted for user {UserId}.", item.ID, _currentUserId);
        }

        public async Task<Item?> GetLocalItemAsync(string id)
        {
            return await _localStore.GetAsync(id, _currentUserId);
        }

        public async Task<Item?> GetRemoteItemAsync(string id)
        {
            return await _remoteStore.GetAsync(id, _currentUserId);
        }

        public async Task<List<Item>> GetAllLocalItemsAsync()
        {
            return await _localStore.GetByUserIdAsync(_currentUserId);
        }

        public async Task<List<Item>> GetAllRemoteItemsAsync()
        {
            return await _remoteStore.GetByUserIdAsync(_currentUserId);
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

        public async Task DeleteLocalItemAsync(Item item)
        {
            item.IsDeleted = true;
            await AddOrUpdateLocalItemAsync(item);
            _logger.LogInformation("Local Item {ItemId} marked as deleted for user {UserId}.", item.ID, _currentUserId);
        }

        public async Task DeleteRemoteItemAsync(Item item)
        {
            item.IsDeleted = true;
            await AddOrUpdateRemoteItemAsync(item);
            _logger.LogInformation("Remote Item {ItemId} marked as deleted for user {UserId}.", item.ID, _currentUserId);
        }
    }
}
