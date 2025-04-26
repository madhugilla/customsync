using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
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
                    // Configuration (replace with your actual values or use a config file)
                    string cosmosEndpoint = "<COSMOS_ENDPOINT>"; // e.g., https://your-account.documents.azure.com:443/
                    string cosmosKey = "<COSMOS_KEY>";
                    string databaseId = "AppDb";
                    string containerId = "Items";
                    string sqlitePath = "local.db";

                    // Register CosmosClient and container for Item
                    services.AddSingleton(_ => new CosmosClient(cosmosEndpoint, cosmosKey));
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

            // Simulate offline change for Item
            var item = new Item { Id = "1", Content = "Local version", LastModified = DateTime.UtcNow };
            await localStore.UpsertAsync(item);

            // Simulate remote change (conflict) for Item
            var remoteItem = new Item { Id = "1", Content = "Remote version", LastModified = DateTime.UtcNow.AddSeconds(-10) };
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            await SyncEngine.SyncAsync(localStore, remoteStore, logger);

            // Sync for Item
            await SyncEngine.SyncAsync(localStore, remoteStore, logger);

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
    }
}
