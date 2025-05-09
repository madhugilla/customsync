﻿using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace cosmosofflinewithLCC
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configuration from environment variables or fallback to Cosmos DB Emulator defaults
                    string cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "https://localhost:8081/";
                    string cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
                    string databaseId = "AppDb";
                    string containerId = "Items";
                    string sqlitePath = "local.db";

                    // Register CosmosClient and container for Item
                    services.AddSingleton(_ => new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        SerializerOptions = new CosmosSerializationOptions
                        {
                            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                        }
                    }));
                    services.AddSingleton(async provider =>
                    {
                        var client = provider.GetRequiredService<CosmosClient>();
                        var db = await client.CreateDatabaseIfNotExistsAsync(databaseId);
                        var container = await db.Database.CreateContainerIfNotExistsAsync(containerId, "/id");
                        return container.Container;
                    });

                    // Register generic stores for Item
                    services.AddSingleton<IDocumentStore<Item>>(provider =>
                    {
                        var sqlite = new SqliteStore<Item>(sqlitePath);
                        return sqlite;
                    });

                    // Register logging
                    services.AddLogging();
                    services.AddSingleton<CosmosDbStore<Item>>(provider =>
                    {
                        var container = provider.GetRequiredService<Task<Container>>().GetAwaiter().GetResult();
                        return new CosmosDbStore<Item>(container);
                    });

                    // Example: Register for another document type (Uncomment and define Order model to use)
                    // services.AddSingleton<IDocumentStore<Order>>(provider => new SqliteStore<Order>(sqlitePath));
                    // services.AddSingleton<CosmosDbStore<Order>>(provider => new CosmosDbStore<Order>(container));
                })
                .Build();

            // Resolve services for Item
            var localStore = host.Services.GetRequiredService<IDocumentStore<Item>>();
            var remoteStore = host.Services.GetRequiredService<CosmosDbStore<Item>>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            // Set the current user ID - in a real app this would come from authentication
            string currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1";

            // Check if this is the first launch by checking if the SQLite DB exists and has any data
            string sqlitePath = "local.db";
            bool isFirstLaunch = !File.Exists(sqlitePath) || await IsLocalDbEmpty(localStore);

            if (isFirstLaunch)
            {
                logger.LogInformation("First application launch detected. Performing initial data pull for user {UserId} from remote store...", currentUserId);

                // Perform initial pull from remote to local without pushing any local changes, filtered by user ID
                await InitialUserDataPull(localStore, remoteStore, logger, currentUserId);

                logger.LogInformation("Initial data pull completed successfully for user {UserId}.", currentUserId);
            }

            // Simulate offline change for Item
            var item = new Item
            {
                Id = "1",
                Content = "Local version",
                LastModified = DateTime.UtcNow,
                UserId = currentUserId
            };
            await localStore.UpsertAsync(item);

            // Simulate remote change (conflict) for Item
            var remoteItem = new Item
            {
                Id = "1",
                Content = "Remote version",
                LastModified = DateTime.UtcNow.AddSeconds(-10),
                UserId = currentUserId
            };
            await remoteStore.UpsertAsync(remoteItem);

            // Sync for Item
            await SyncEngine.SyncAsync(localStore, remoteStore, logger, x => x.Id, x => x.LastModified);

            // Result for Item
            var syncedRemote = await remoteStore.GetAsync("1");
            var syncedLocal = await localStore.GetAsync("1");
            Console.WriteLine($"Remote Content: {syncedRemote?.Content}");
            Console.WriteLine($"Local Content: {syncedLocal?.Content}");

            // Example: Use for another document type (Uncomment and define Order model to use)
            // var orderLocalStore = host.Services.GetRequiredService<IDocumentStore<Order>>();
            // var orderRemoteStore = host.Services.GetRequiredService<CosmosDbStore<Order>>();
            // await SyncEngine.SyncAsync(orderLocalStore, orderRemoteStore);
        }

        private static async Task<bool> IsLocalDbEmpty<T>(IDocumentStore<T> localStore) where T : class, new()
        {
            var items = await localStore.GetAllAsync();
            return items.Count == 0;
        }

        private static async Task InitialUserDataPull<T>(IDocumentStore<T> local, IDocumentStore<T> remote, ILogger logger, string userId) where T : class, new()
        {
            try
            {
                // Get only the items for the specific user from remote store
                var remoteItems = await remote.GetByUserIdAsync(userId);
                logger.LogInformation("Found {Count} items for user {UserId} in remote store to pull into local database", remoteItems.Count, userId);

                if (remoteItems.Count > 0)
                {
                    // Insert user-specific remote items to local store
                    await local.UpsertBulkAsync(remoteItems);
                    logger.LogInformation("Successfully pulled {Count} items for user {UserId} from remote store", remoteItems.Count, userId);
                }
                else
                {
                    logger.LogInformation("No items found for user {UserId} in remote store to pull", userId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during initial data pull from remote store for user {UserId}", userId);
                throw;
            }
        }
    }
}
