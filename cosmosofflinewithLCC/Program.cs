using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;

namespace cosmosofflinewithLCC
{
    public class Program
    {
        private static async Task<bool> IsLocalDbEmpty<T>(IDocumentStore<T> localStore) where T : class, new()
        {
            var items = await localStore.GetAllAsync();
            return items.Count == 0;
        }

        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configuration from environment variables or fallback to Cosmos DB Emulator defaults
                    string cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "https://localhost:8081/";
                    string cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
                    string databaseId = "AppDb";
                    string containerId = "Documents"; // Single container for all document types
                    string sqlitePath = "local.db";

                    // Register CosmosClient - shared for all document types
                    services.AddSingleton(_ => new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        SerializerOptions = new CosmosSerializationOptions
                        {
                            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                        }
                    }));

                    // Register shared container for all document types - direct access to existing resources
                    services.AddSingleton(provider =>
                    {
                        var client = provider.GetRequiredService<CosmosClient>();

                        // Direct access to existing container (created by IAC)
                        return client.GetDatabase(databaseId).GetContainer(containerId);
                    });

                    // Register stores for Item type
                    services.AddSingleton<IDocumentStore<Item>>(provider =>
                    {
                        var sqlite = new SqliteStore<Item>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<Item>>(provider =>
                    {
                        var container = provider.GetRequiredService<Container>();
                        return new CosmosDbStore<Item>(container);
                    });

                    // Register stores for Order type - using the same container but different partition key
                    services.AddSingleton<IDocumentStore<Order>>(provider =>
                    {
                        var sqlite = new SqliteStore<Order>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<Order>>(provider =>
                    {
                        var container = provider.GetRequiredService<Container>();
                        return new CosmosDbStore<Order>(container);
                    });

                    // Register logging
                    services.AddLogging();

                    // Register SyncEngine for Item type
                    services.AddScoped<SyncEngine<Item>>(sp =>
                    {
                        var currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";
                        return new SyncEngine<Item>(
                            sp.GetRequiredService<IDocumentStore<Item>>(),
                            sp.GetRequiredService<CosmosDbStore<Item>>(),
                            sp.GetRequiredService<ILogger<SyncEngine<Item>>>(),
                            x => x.ID,
                            x => x.LastModified,
                            currentUserId);
                    });

                    // Register SyncEngine for Order type
                    services.AddScoped<SyncEngine<Order>>(sp =>
                    {
                        var currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";
                        return new SyncEngine<Order>(
                            sp.GetRequiredService<IDocumentStore<Order>>(),
                            sp.GetRequiredService<CosmosDbStore<Order>>(),
                            sp.GetRequiredService<ILogger<SyncEngine<Order>>>(),
                            x => x.ID,
                            x => x.LastModified,
                            currentUserId);
                    });

                    // Register ItemService
                    services.AddScoped<ItemService>();
                })
                .Build();

            // Get the required services
            using var serviceScope = host.Services.CreateScope();
            var itemService = serviceScope.ServiceProvider.GetRequiredService<ItemService>();
            var syncEngineOrder = serviceScope.ServiceProvider.GetRequiredService<SyncEngine<Order>>();
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            // Set the current user ID - in a real app this would come from authentication
            string currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";

            // Check if this is the first launch by checking if the SQLite DB exists and has any data
            string sqlitePath = "local.db";
            bool isFirstLaunch = !File.Exists(sqlitePath) || await itemService.IsLocalStoreEmptyAsync();

            if (isFirstLaunch)
            {
                await InitialDataPullAsync(itemService, syncEngineOrder, logger, currentUserId);
            }

            // Demonstrate Item operations
            await DemonstrateItemOperations(itemService, logger);

            // Demonstrate Order operations
            await DemonstrateOrderOperations(serviceScope, syncEngineOrder, logger, currentUserId);
        }

        /// <summary>
        /// Performs initial data pull for Items and Orders
        /// </summary>
        private static async Task InitialDataPullAsync(
            ItemService itemService,
            SyncEngine<Order> syncEngineOrder,
            ILogger logger,
            string currentUserId)
        {
            logger.LogInformation("First application launch detected. Performing initial data pull for user {UserId} from remote store...", currentUserId);

            // Perform initial pull for Item type using ItemService
            await itemService.InitialDataPullAsync();

            // Perform initial pull for Order type
            await syncEngineOrder.InitialUserDataPullAsync("Order");

            logger.LogInformation("Initial data pull completed successfully for user {UserId}.", currentUserId);
        }

        /// <summary>
        /// Demonstrates Item operations including local changes, remote changes, and sync
        /// </summary>
        private static async Task DemonstrateItemOperations(ItemService itemService, ILogger logger)
        {
            // Simulate offline change for Item using ItemService
            var item = new Item()
            {
                ID = "1",
                Content = "Local version",
                LastModified = DateTime.UtcNow
                // OIID and Type will be set by ItemService
            };
            await itemService.AddOrUpdateLocalItemAsync(item);
            logger.LogInformation("Created local item with ID: {ItemId}", item.ID);

            // Simulate remote change (conflict) for Item using ItemService
            var remoteItem = new Item()
            {
                ID = "1",
                Content = "Remote version",
                LastModified = DateTime.UtcNow.AddMinutes(-10)
                // OIID and Type will be set by ItemService
            };
            await itemService.AddOrUpdateRemoteItemAsync(remoteItem);
            logger.LogInformation("Created remote item with ID: {ItemId}", remoteItem.ID);

            // Sync using ItemService
            await itemService.SyncItemsAsync();
            logger.LogInformation("Completed item sync");

            // Result for Item using ItemService
            var syncedRemote = await itemService.GetRemoteItemAsync("1");
            var syncedLocal = await itemService.GetLocalItemAsync("1");
            Console.WriteLine($"Remote Content: {syncedRemote?.Content}");
            Console.WriteLine($"Local Content: {syncedLocal?.Content}");
        }

        /// <summary>
        /// Demonstrates Order operations including creation and sync
        /// </summary>
        private static async Task DemonstrateOrderOperations(
            IServiceScope serviceScope,
            SyncEngine<Order> syncEngineOrder,
            ILogger logger,
            string currentUserId)
        {
            // Get Order stores
            var orderLocalStore = serviceScope.ServiceProvider.GetRequiredService<IDocumentStore<Order>>();
            var orderRemoteStore = serviceScope.ServiceProvider.GetRequiredService<CosmosDbStore<Order>>();

            // Create and sync an order
            var order = new Order()
            {
                ID = "order1",
                Description = "Test order",
                LastModified = DateTime.UtcNow,
                OIID = currentUserId,
                Type = "Order"
            };

            await orderLocalStore.UpsertAsync(order);
            logger.LogInformation("Created local order with ID: {OrderId}", order.ID);            // Sync Order data using the Order-specific SyncEngine instance
            try
            {
                await syncEngineOrder.SyncAsync();
                logger.LogInformation("Order sync completed successfully");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Order sync encountered an issue: {Message}", ex.Message);
            }

            // Verify the order was synced
            var syncedOrder = await orderRemoteStore.GetAsync("order1", currentUserId);
            Console.WriteLine($"Order Description: {syncedOrder?.Description}");
        }
    }
}
