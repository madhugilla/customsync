using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
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
                            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                        }
                    }));

                    // Register shared container for all document types using composite partition key
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

                    // Register stores for Item type
                    services.AddSingleton<IDocumentStore<Item>>(provider =>
                    {
                        var sqlite = new SqliteStore<Item>(sqlitePath);
                        return sqlite;
                    });
                    services.AddSingleton<CosmosDbStore<Item>>(provider =>
                    {
                        var container = provider.GetRequiredService<Task<Container>>().GetAwaiter().GetResult();
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
                        var container = provider.GetRequiredService<Task<Container>>().GetAwaiter().GetResult();
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
                })
                .Build();

            // Get the required services
            using var serviceScope = host.Services.CreateScope();
            var syncEngineItem = serviceScope.ServiceProvider.GetRequiredService<SyncEngine<Item>>();
            var syncEngineOrder = serviceScope.ServiceProvider.GetRequiredService<SyncEngine<Order>>();
            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var localStore = serviceScope.ServiceProvider.GetRequiredService<IDocumentStore<Item>>();
            var remoteStore = serviceScope.ServiceProvider.GetRequiredService<CosmosDbStore<Item>>();

            // Set the current user ID - in a real app this would come from authentication
            string currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";

            // Check if this is the first launch by checking if the SQLite DB exists and has any data
            string sqlitePath = "local.db";
            bool isFirstLaunch = !File.Exists(sqlitePath) || await IsLocalDbEmpty(localStore);

            if (isFirstLaunch)
            {
                logger.LogInformation("First application launch detected. Performing initial data pull for user {UserId} from remote store...", currentUserId);

                // Perform initial pull for both Item and Order types
                await syncEngineItem.InitialUserDataPullAsync("Item");
                await syncEngineOrder.InitialUserDataPullAsync("Order");

                logger.LogInformation("Initial data pull completed successfully for user {UserId}.", currentUserId);
            }

            // Simulate offline change for Item
            var item = new Item()
            {
                ID = "1",
                Content = "Local version",
                LastModified = DateTime.UtcNow,
                OIID = currentUserId,
                Type = "Item"
            };
            await localStore.UpsertAsync(item);

            // Simulate remote change (conflict) for Item
            var remoteItem = new Item()
            {
                ID = "1",
                Content = "Remote version",
                LastModified = DateTime.UtcNow.AddMinutes(-10),
                OIID = currentUserId,
                Type = "Item"
            };
            await remoteStore.UpsertAsync(remoteItem);

            // Sync using the instance-based SyncEngine
            await syncEngineItem.SyncAsync();

            // Result for Item
            var syncedRemote = await remoteStore.GetAsync("1", currentUserId);
            var syncedLocal = await localStore.GetAsync("1", currentUserId);
            Console.WriteLine($"Remote Content: {syncedRemote?.Content}");
            Console.WriteLine($"Local Content: {syncedLocal?.Content}");

            // Demonstrate using Order type
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

            // Sync Order data using the Order-specific SyncEngine instance
            await syncEngineOrder.SyncAsync();

            // Verify the order was synced
            var syncedOrder = await orderRemoteStore.GetAsync("order1", currentUserId);
            Console.WriteLine($"Order Description: {syncedOrder?.Description}");
        }
    }
}
