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
                {                    // Configuration from environment variables or fallback to Cosmos DB Emulator defaults
                    string cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "https://localhost:8081/";
                    string tokenEndpoint = Environment.GetEnvironmentVariable("TOKEN_ENDPOINT") ?? "https://your-token-service/api/token";
                    string databaseId = "AppDb";
                    string containerId = "Documents"; // Single container for all document types
                    string sqlitePath = "local.db";

                    // Register token provider and client factory for token-based authentication
                    // We don't pass the userId in the constructor anymore, it will be set at runtime
                    services.AddSingleton<ICosmosTokenProvider>(provider =>
                        new SampleTokenProvider(tokenEndpoint));

                    // Option 1: Use factory with default options (recommended)
                    // Factory automatically configures optimal settings for token-based authentication
                    services.AddSingleton<ICosmosClientFactory>(provider =>
                    {
                        var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
                        return new CosmosClientFactory(tokenProvider, cosmosEndpoint);
                    });

                    // Option 2: Use factory with environment-specific configuration
                    // Uncomment if you need environment-specific settings
                    /*
                    services.AddSingleton<ICosmosClientFactory>(provider =>
                    {
                        var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
                        var isDevelopment = Environment.GetEnvironmentVariable("ENVIRONMENT") == "Development";
                        return new CosmosClientFactory(tokenProvider, cosmosEndpoint, isDevelopment);
                    });
                    */

                    // Option 3: Use factory with custom options
                    // Uncomment if you need specific configuration
                    /*
                    services.AddSingleton<ICosmosClientFactory>(provider =>
                    {
                        var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
                        var customOptions = new CosmosClientOptions
                        {
                            ConnectionMode = ConnectionMode.Direct,
                            MaxRetryAttemptsOnRateLimitedRequests = 5,
                            RequestTimeout = TimeSpan.FromSeconds(90)
                        };
                        return new CosmosClientFactory(tokenProvider, cosmosEndpoint, customOptions);
                    });
                    */

                    // Register stores for Item type
                    services.AddSingleton<IDocumentStore<Item>>(provider =>
                    {
                        var sqlite = new SqliteStore<Item>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<Item>>(provider =>
                    {
                        var clientFactory = provider.GetRequiredService<ICosmosClientFactory>();
                        return new CosmosDbStore<Item>(clientFactory, databaseId, containerId);
                    });

                    // Register stores for Order type - using the same container but different partition key
                    services.AddSingleton<IDocumentStore<Order>>(provider =>
                    {
                        var sqlite = new SqliteStore<Order>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<Order>>(provider =>
                    {
                        var clientFactory = provider.GetRequiredService<ICosmosClientFactory>();
                        return new CosmosDbStore<Order>(clientFactory, databaseId, containerId);
                    });

                    // Register stores for AssessmentPlan type - using the same container but different partition key
                    services.AddSingleton<IDocumentStore<AssessmentPlan>>(provider =>
                    {
                        var sqlite = new SqliteStore<AssessmentPlan>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<AssessmentPlan>>(provider =>
                    {
                        var clientFactory = provider.GetRequiredService<ICosmosClientFactory>();
                        return new CosmosDbStore<AssessmentPlan>(clientFactory, databaseId, containerId);
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
                    });                    // Register SyncEngine for Order type
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
                    // Register SyncEngine for AssessmentPlan type
                    services.AddScoped<SyncEngine<AssessmentPlan>>(sp =>
                    {
                        var currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";
                        return new SyncEngine<AssessmentPlan>(
                            sp.GetRequiredService<IDocumentStore<AssessmentPlan>>(),
                            sp.GetRequiredService<CosmosDbStore<AssessmentPlan>>(),
                            sp.GetRequiredService<ILogger<SyncEngine<AssessmentPlan>>>(),
                            x => x.ID,
                            x => x.LastModified,
                            currentUserId);
                    });

                    // Register AssessmentPlanService
                    services.AddScoped<Services.AssessmentPlanService>();
                })
                .Build();

            // Get the required services
            using var serviceScope = host.Services.CreateScope();
            var syncEngineOrder = serviceScope.ServiceProvider.GetRequiredService<SyncEngine<Order>>();
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var tokenProvider = serviceScope.ServiceProvider.GetRequiredService<ICosmosTokenProvider>();

            // Set the current user ID - in a real app this would come from authentication
            string currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";

            // Set the user ID on the token provider
            logger.LogInformation("Setting user ID to {UserId} on token provider", currentUserId);
            tokenProvider.SetUserId(currentUserId);

            // Check if this is the first launch by checking if the SQLite DB exists and has any data
            string sqlitePath = "local.db";
            var localStore = serviceScope.ServiceProvider.GetRequiredService<IDocumentStore<Order>>();
            bool isFirstLaunch = !File.Exists(sqlitePath) || await IsLocalDbEmpty<Order>(localStore);

            if (isFirstLaunch)
            {
                await InitialDataPullAsync(syncEngineOrder, logger, currentUserId);
            }

            // Demonstrate Order operations
            await DemonstrateOrderOperations(serviceScope, syncEngineOrder, logger, currentUserId);
        }        /// <summary>
                 /// Performs initial data pull for Orders
                 /// </summary>
        private static async Task InitialDataPullAsync(
            SyncEngine<Order> syncEngineOrder,
            ILogger logger,
            string currentUserId)
        {
            logger.LogInformation("First application launch detected. Performing initial data pull for user {UserId} from remote store...", currentUserId);

            // Perform initial pull for Order type
            await syncEngineOrder.InitialUserDataPullAsync("Order");

            logger.LogInformation("Initial data pull completed successfully for user {UserId}.", currentUserId);
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
