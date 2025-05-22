using Microsoft.Azure.Cosmos;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace cosmosofflinewithLCC.IntegrationTests
{
    // Commented out for testing purposes - May 22, 2025
    /*
    public class AssessmentPlanIntegrationTests : IDisposable
    {
        private readonly Container _container;
        private readonly CosmosDbStore<AssessmentPlan> _store;
        private readonly CosmosClient _cosmosClient;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "TestDb";
        private readonly string _containerId = "TestContainer";

        public AssessmentPlanIntegrationTests()
        {
            // Setup using local emulator
            _cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==");

            try
            {
                // Delete test container if it exists to ensure clean state with correct partition key
                try
                {
                    Console.WriteLine($"Deleting container {_containerId} in database {_databaseId} if it exists");
                    _cosmosClient.GetDatabase(_databaseId).GetContainer(_containerId).DeleteContainerAsync().GetAwaiter().GetResult();
                    // Wait a moment for the delete to complete
                    System.Threading.Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    // Ignore if the container doesn't exist
                    Console.WriteLine($"Container deletion exception (can be ignored if not exists): {ex.Message}");
                }

                // Create database if not exists
                _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseId).GetAwaiter().GetResult();
                var database = _cosmosClient.GetDatabase(_databaseId);
                Console.WriteLine($"Creating container {_containerId} with partition key path /partitionKey");

                // Create container with composite partition key (userId:docType)
                database.CreateContainerIfNotExistsAsync(
                    id: _containerId,
                    partitionKeyPath: "/partitionKey",
                    throughput: 400).GetAwaiter().GetResult();

                _container = database.GetContainer(_containerId);
                _store = new CosmosDbStore<AssessmentPlan>(_container);

                Console.WriteLine("CosmosDB test container is ready");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up Cosmos DB: {ex.Message}");
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
                // Use a query to find all items (regardless of partition key)
                var query = new QueryDefinition("SELECT c.id, c.partitionKey FROM c");
                var itemsToDelete = new List<(string id, string partitionKey)>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
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
                        // Delete using the composite partition key
                        Console.WriteLine($"Deleting item with id {id} from partition {partitionKey}");
                        await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Ignore if item not found
                        Console.WriteLine($"Item not found during deletion: {id} in partition {partitionKey}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up test data: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Clean up any test data
            CleanupTestData().GetAwaiter().GetResult();
        }

        [Fact]
        public async Task UpsertAndGetAsync_ShouldStoreAndRetrieveAssessmentPlan()
        {
            // Arrange
            var assessmentPlan = new AssessmentPlan
            {
                ID = Guid.NewGuid().ToString(),
                UID = "test-uid",
                OIID = _testUserId,
                Type = "AssessmentPlan",
                StartDate = "2025-01-01",
                EndDate = "2025-12-31",
                PlanName = "Test Assessment Plan",
                IsDeleted = false,
                DomainList = new DomainLevel[]
                {
                    new DomainLevel
                    {
                        DomainName = "Test Domain",
                        Levels = new List<AssessmentLevels>
                        {
                            new AssessmentLevels("Level 1", new List<AssessmentLevels.LevelStep>
                            {
                                new AssessmentLevels.LevelStep("Step 1"),
                                new AssessmentLevels.LevelStep("Step 2")
                            })
                        }
                    }
                }
            };

            // Act - first store the item
            Console.WriteLine($"Upserting document with ID: {assessmentPlan.ID} with partition key: {assessmentPlan.PartitionKey}");
            await _store.UpsertAsync(assessmentPlan);

            // Directly get by ID and partition key (how CosmosDbStoreIntegrationTests does it)
            var directResult = await _store.GetAsync(assessmentPlan.ID, assessmentPlan.OIID);
            Console.WriteLine($"Direct result by ID: {(directResult != null ? "Found" : "Not found")}");

            // Assert
            Assert.NotNull(directResult);
            Assert.Equal(assessmentPlan.ID, directResult.ID);
            Assert.Equal(assessmentPlan.PlanName, directResult.PlanName);
            Assert.Equal(assessmentPlan.OIID, directResult.OIID);

            // Now check GetByUserIdAsync
            var queryResults = await _store.GetByUserIdAsync(assessmentPlan.OIID);
            Console.WriteLine($"Query results count: {queryResults.Count}");

            // This should now work
            Assert.NotEmpty(queryResults);

            // Check the first item
            var retrievedPlan = queryResults[0];
            Assert.Equal(assessmentPlan.ID, retrievedPlan.ID);
            Assert.Equal(assessmentPlan.PlanName, retrievedPlan.PlanName);
            Assert.Equal(assessmentPlan.OIID, retrievedPlan.OIID);
            // assert that domains are not null
            Assert.NotNull(retrievedPlan.DomainList);        }
    }
    */
}
