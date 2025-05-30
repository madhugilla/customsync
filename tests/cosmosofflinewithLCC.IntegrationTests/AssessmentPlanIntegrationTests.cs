using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace cosmosofflinewithLCC.IntegrationTests
{
    public class AssessmentPlanIntegrationTests : IDisposable
    {
        private readonly CosmosDbStore<AssessmentPlan> _store;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "SyncTestDB"; // Match Azure Function configuration
        private readonly string _containerId = "SyncTestContainer"; // Match Azure Function configuration
        private readonly TestCosmosTokenProvider _tokenProvider;
        private readonly string _cosmosEndpoint = "https://localhost:8081"; // Cosmos DB emulator endpoint

        public AssessmentPlanIntegrationTests()
        {
            Console.WriteLine("Setting up integration tests with token provider...");

            try
            {
                // Create the token provider for tests using local function URL
                _tokenProvider = new TestCosmosTokenProvider("http://localhost:7071/api/GetCosmosToken", _testUserId);

                // Create the store using the token provider
                _store = new CosmosDbStore<AssessmentPlan>(_tokenProvider, _cosmosEndpoint, _databaseId, _containerId);

                Console.WriteLine("Token-based CosmosDB store is ready");
                Console.WriteLine($"Using Cosmos endpoint: {_cosmosEndpoint}");
                Console.WriteLine($"Target database: {_databaseId}, container: {_containerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up token-based Cosmos DB: {ex.Message}");
                throw;
            }

            // Clean up any existing data
            Console.WriteLine("Cleaning up any existing test data");
            CleanupTestData().GetAwaiter().GetResult();
        }
        private async Task CleanupTestData()
        {
            try
            {
                // Initialize the store by doing a simple operation to ensure client is initialized
                try
                {
                    await _store.GetAllAsync();
                }
                catch
                {
                    // Ignore errors, we just need to initialize the client
                }                // Create a container client for cleanup
                var cosmosClient = await _store.GetInternalClientAsync();
                var container = cosmosClient.GetContainer(_databaseId, _containerId);

                // Use a query to find all items (regardless of partition key)
                var query = new QueryDefinition("SELECT c.id, c.partitionKey FROM c");
                var itemsToDelete = new List<(string id, string partitionKey)>();

                using var iterator = container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var item in response)
                    {
                        string id = item.id;
                        string partitionKey = item.partitionKey;

                        if (string.IsNullOrEmpty(partitionKey))
                        {
                            // Fall back to old format if needed
                            if (item.oiid != null)
                            {
                                string userId = item.oiid;
                                partitionKey = $"{userId}:AssessmentPlan"; // Default type to AssessmentPlan if not specified
                            }
                            else
                            {
                                partitionKey = $"{_testUserId}:AssessmentPlan"; // Default fallback
                            }
                        }

                        itemsToDelete.Add((id, partitionKey));
                    }
                }
                Console.WriteLine($"Found {itemsToDelete.Count} items to delete in cleanup");

                foreach (var (id, partitionKey) in itemsToDelete)
                {
                    try
                    {
                        await container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up test data: {ex.Message}");
                throw;
            }
        }

        [Fact]
        public async Task CanUpsertAndRetrieveAssessmentPlan()
        {
            // Create a test assessment plan
            var plan = new AssessmentPlan
            {
                ID = Guid.NewGuid().ToString(),
                PlanName = "Test Plan",
                Type = "Assessment",
                LastModified = DateTime.UtcNow,
                OIID = _testUserId
            };

            // Upsert the plan
            await _store.UpsertAsync(plan);

            // Retrieve the plan
            var retrievedPlan = await _store.GetAsync(plan.ID, plan.OIID);

            // Assert
            Assert.NotNull(retrievedPlan);
            Assert.Equal(plan.ID, retrievedPlan.ID);
            Assert.Equal(plan.PlanName, retrievedPlan.PlanName);
            Assert.Equal(plan.Type, retrievedPlan.Type);
            Assert.Equal(plan.OIID, retrievedPlan.OIID);
        }

        public void Dispose()
        {
            // Clean up when done
            CleanupTestData().GetAwaiter().GetResult();
            _store?.Dispose();
        }
    }
}
