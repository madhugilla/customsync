using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using System.Diagnostics;

namespace cosmosofflinewithLCC
{
    public class Program
    {
        private static async Task<bool> IsLocalDbEmpty<T>(IDocumentStore<T> localStore) where T : class, new()
        {
            var items = await localStore.GetAllAsync();
            return items.Count == 0;
        }        /// <summary>
                 /// Tests the CosmosDB store with SQLite and SyncEngine
                 /// </summary>
                 /// <param name="serviceScope">The service scope containing required services</param>
                 /// <param name="userId">Current user ID</param>
                 /// <returns>Async task</returns>
        private static async Task TestCosmosStoreSync(IServiceScope serviceScope, string userId)
        {
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Starting Cosmos DB Store sync test for user {UserId}", userId);

            // Create SQLite stores specific to this test
            string sqlitePath = "cosmos_local.db";
            var itemLocalStore = new SqliteStore<Item>(sqlitePath);
            var orderLocalStore = new SqliteStore<Order>(sqlitePath);

            // Get remote Cosmos DB stores from DI
            var itemRemoteStore = serviceScope.ServiceProvider.GetRequiredService<CosmosDbStore<Item>>();
            var orderRemoteStore = serviceScope.ServiceProvider.GetRequiredService<CosmosDbStore<Order>>();

            // Create sync engines with the locally created SQLite stores
            var syncEngineItem = new SyncEngine<Item>(
                itemLocalStore,
                itemRemoteStore,
                logger,
                x => x.ID,
                x => x.LastModified,
                userId);

            var syncEngineOrder = new SyncEngine<Order>(
                orderLocalStore,
                orderRemoteStore,
                logger,
                x => x.ID,
                x => x.LastModified,
                userId);

            try
            {
                // Check if this is the first launch by checking if the SQLite DB exists and has any data
                bool isFirstLaunch = !File.Exists(sqlitePath) || await IsLocalDbEmpty(itemLocalStore);

                if (isFirstLaunch)
                {
                    logger.LogInformation("First launch detected for Cosmos test. Performing initial data pull from remote store for user {UserId}...", userId);
                    await syncEngineItem.InitialUserDataPullAsync("Item");
                    await syncEngineOrder.InitialUserDataPullAsync("Order");
                    logger.LogInformation("Initial data pull completed successfully for Cosmos test for user {UserId}.", userId);
                }

                // Simulate offline change for Item
                logger.LogInformation("Creating test item in local store");
                var item = new Item()
                {
                    ID = "1",
                    Content = "Local version",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                };
                await itemLocalStore.UpsertAsync(item);

                // Simulate remote change (conflict) for Item
                logger.LogInformation("Creating test item in remote Cosmos DB store");
                var remoteItem = new Item()
                {
                    ID = "1",
                    Content = "Remote version",
                    LastModified = DateTime.UtcNow.AddMinutes(-10),
                    OIID = userId,
                    Type = "Item"
                };
                await itemRemoteStore.UpsertAsync(remoteItem);

                // Sync using the instance-based SyncEngine
                logger.LogInformation("Syncing items with Cosmos DB store");
                await syncEngineItem.SyncAsync();

                // Result for Item
                var syncedRemote = await itemRemoteStore.GetAsync("1", userId);
                var syncedLocal = await itemLocalStore.GetAsync("1", userId);
                logger.LogInformation("Synced remote item: {Content}", syncedRemote?.Content);
                logger.LogInformation("Synced local item: {Content}", syncedLocal?.Content);

                // Demonstrate using Order type
                logger.LogInformation("Creating test order in local store");
                var order = new Order()
                {
                    ID = "order1",
                    Description = "Test order",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Order"
                };
                await orderLocalStore.UpsertAsync(order);

                // Sync Order data using the Order-specific SyncEngine instance
                logger.LogInformation("Syncing orders with Cosmos DB store");
                await syncEngineOrder.SyncAsync();

                // Verify the order was synced
                var syncedOrder = await orderRemoteStore.GetAsync("order1", userId);
                logger.LogInformation("Synced remote order: {Description}", syncedOrder?.Description);

                // Output results to console for visibility
                Console.WriteLine("\n=== Cosmos DB Store Sync Test Results ===");
                Console.WriteLine($"Local Item -> Remote Status: {(syncedRemote != null ? "Success" : "Failed")}");
                Console.WriteLine($"Remote Item -> Local Status: {(syncedLocal != null ? "Success" : "Failed")}");
                Console.WriteLine($"Local Order -> Remote Status: {(syncedOrder != null ? "Success" : "Failed")}");
                Console.WriteLine("=======================================\n");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Cosmos DB store sync test");
                Console.WriteLine($"Cosmos DB store sync test failed: {ex.Message}");
            }
        }/// <summary>
         /// Tests the FunctionDocumentStore with SQLite and SyncEngine
         /// </summary>
         /// <param name="serviceScope">The service scope containing required services</param>
         /// <param name="userId">Current user ID</param>
         /// <returns>Async task</returns>
        private static async Task TestFunctionStoreSync(IServiceScope serviceScope, string userId)
        {
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Starting Function Store sync test for user {UserId}", userId);

            // Create SQLite stores specific to this test
            string sqlitePath = "function_local.db";
            var itemLocalStore = new SqliteStore<Item>(sqlitePath);
            var orderLocalStore = new SqliteStore<Order>(sqlitePath);

            // Get Function stores from DI
            var itemFunctionStore = serviceScope.ServiceProvider.GetRequiredService<FunctionDocumentStore<Item>>();
            var orderFunctionStore = serviceScope.ServiceProvider.GetRequiredService<FunctionDocumentStore<Order>>();

            // Create sync engines specifically for function+SQLite test
            var syncEngineItemFunction = new SyncEngine<Item>(
                itemLocalStore,
                itemFunctionStore,
                logger,
                x => x.ID,
                x => x.LastModified,
                userId);

            var syncEngineOrderFunction = new SyncEngine<Order>(
                orderLocalStore,
                orderFunctionStore,
                logger,
                x => x.ID,
                x => x.LastModified,
                userId);

            try
            {
                // Check if this is the first launch by checking if the SQLite DB exists and has any data
                bool isFirstLaunch = !File.Exists(sqlitePath) || await IsLocalDbEmpty(itemLocalStore);

                if (isFirstLaunch)
                {
                    logger.LogInformation("First launch detected for Function test. Performing initial data pull from remote store for user {UserId}...", userId);
                    await syncEngineItemFunction.InitialUserDataPullAsync("Item");
                    await syncEngineOrderFunction.InitialUserDataPullAsync("Order");
                    logger.LogInformation("Initial data pull completed successfully for Function test for user {UserId}.", userId);
                }

                // Test Item synchronization
                logger.LogInformation("Creating test item in local store");
                var functionTestItem = new Item()
                {
                    ID = "function-test-1",
                    Content = "Function test item created locally",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Item"
                };
                await itemLocalStore.UpsertAsync(functionTestItem);

                // Create a remote item directly with the function store
                logger.LogInformation("Creating test item in remote Function store");
                var remoteTestItem = new Item()
                {
                    ID = "function-test-2",
                    Content = "Function test item created remotely",
                    LastModified = DateTime.UtcNow.AddMinutes(-5), // Older timestamp to test conflict resolution
                    OIID = userId,
                    Type = "Item"
                };
                await itemFunctionStore.UpsertAsync(remoteTestItem);

                // Sync items
                logger.LogInformation("Syncing items with Function store");
                await syncEngineItemFunction.SyncAsync();

                // Verify results
                var syncedRemoteItem1 = await itemFunctionStore.GetAsync("function-test-1", userId);
                var syncedLocalItem2 = await itemLocalStore.GetAsync("function-test-2", userId);

                logger.LogInformation("Synced remote item 1: {Content}", syncedRemoteItem1?.Content);
                logger.LogInformation("Synced local item 2: {Content}", syncedLocalItem2?.Content);

                // Test Order synchronization
                logger.LogInformation("Creating test order in local store");
                var functionTestOrder = new Order()
                {
                    ID = "function-order-1",
                    Description = "Function test order created locally",
                    LastModified = DateTime.UtcNow,
                    OIID = userId,
                    Type = "Order"
                };
                await orderLocalStore.UpsertAsync(functionTestOrder);

                // Sync orders
                logger.LogInformation("Syncing orders with Function store");
                await syncEngineOrderFunction.SyncAsync();

                // Verify result
                var syncedRemoteOrder = await orderFunctionStore.GetAsync("function-order-1", userId);
                logger.LogInformation("Synced remote order: {Description}", syncedRemoteOrder?.Description);

                // Output results to console for visibility
                Console.WriteLine("\n=== Function Store Sync Test Results ===");
                Console.WriteLine($"Local Item -> Remote Status: {(syncedRemoteItem1 != null ? "Success" : "Failed")}");
                Console.WriteLine($"Remote Item -> Local Status: {(syncedLocalItem2 != null ? "Success" : "Failed")}");
                Console.WriteLine($"Local Order -> Remote Status: {(syncedRemoteOrder != null ? "Success" : "Failed")}");
                Console.WriteLine("=======================================\n");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Function store sync test");
                Console.WriteLine($"Function store sync test failed: {ex.Message}");
            }
        }
        public static async Task Main(string[] args)
        {
            // Configure services
            var host = CreateHostWithServices();

            // Create a service scope for executing the application
            using var serviceScope = host.Services.CreateScope();
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            // Set the current user ID - in a real app this would come from authentication
            string currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";

            // Test Cosmos DB store synchronization
            // await TestCosmosStoreSync(serviceScope, currentUserId);

            // Test with Function Store if environment variables are provided
            string functionKey = Environment.GetEnvironmentVariable("FUNCTION_KEY") ?? "";

            // if (!string.IsNullOrEmpty(functionKey))

            logger.LogInformation("Function key found, testing Function store synchronization");
            await TestFunctionStoreSync(serviceScope, currentUserId);

        }

        /// <summary>
        /// Configures and creates the host with all required services
        /// </summary>
        private static IHost CreateHostWithServices()
        {
            return Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {                    // Configuration from environment variables or fallback to Cosmos DB Emulator defaults
                    string cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "https://localhost:8081/";
                    string cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
                    string databaseId = "AppDb";
                    string containerId = "Documents"; // Single container for all document types

                    // Get Function configuration from environment variables
                    string functionBaseUrl = Environment.GetEnvironmentVariable("FUNCTION_BASE_URL") ?? "http://localhost:7071";
                    string functionKey = Environment.GetEnvironmentVariable("FUNCTION_KEY") ?? "";

                    // Register HttpClient for Function store
                    services.AddSingleton<HttpClient>();

                    // Register CosmosClient - shared for all document types
                    services.AddSingleton(_ => new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        SerializerOptions = new CosmosSerializationOptions
                        {
                            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                        }
                    }));                    // Register shared container for all document types using composite partition key
                    services.AddSingleton(async provider =>
                    {
                        var client = provider.GetRequiredService<CosmosClient>();
                        var db = await client.CreateDatabaseIfNotExistsAsync(databaseId);

                        // Use a composite partition key (userId:docType) for multi-tenant and multi-type support
                        var container = await db.Database.CreateContainerIfNotExistsAsync(
                            id: containerId,
                            partitionKeyPath: "/partitionKey",
                            throughput: 400
                        );

                        return container.Container;
                    });

                    // Register Cosmos DB stores
                    services.AddSingleton<CosmosDbStore<Item>>(provider =>
                    {
                        var container = provider.GetRequiredService<Task<Container>>().GetAwaiter().GetResult();
                        return new CosmosDbStore<Item>(container);
                    });
                    services.AddSingleton<CosmosDbStore<Order>>(provider =>
                    {
                        var container = provider.GetRequiredService<Task<Container>>().GetAwaiter().GetResult();
                        return new CosmosDbStore<Order>(container);
                    });                    // Register Function stores
                    services.AddSingleton<FunctionDocumentStore<Item>>(provider =>
                    {
                        var httpClient = provider.GetRequiredService<HttpClient>();
                        return new FunctionDocumentStore<Item>(httpClient, functionBaseUrl, functionKey);
                    });
                    services.AddSingleton<FunctionDocumentStore<Order>>(provider =>
                    {
                        var httpClient = provider.GetRequiredService<HttpClient>();
                        return new FunctionDocumentStore<Order>(httpClient, functionBaseUrl, functionKey);
                    });

                    // Register logging
                    services.AddLogging();
                })
                .Build();
        }
    }
}
