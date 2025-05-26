using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using Microsoft.Azure.Cosmos;

namespace cosmosofflinewithLCC.IntegrationTests
{
    public class AssessmentPlanIntegrationTests : IDisposable
    {
        private readonly CosmosDbStore<AssessmentPlan> _store;
        private readonly string _testUserId = "testUser1";
        private readonly string _databaseId = "SyncTestDB"; // Match Azure Function configuration
        private readonly string _containerId = "SyncTestContainer"; // Match Azure Function configuration
        private readonly string _azureFunctionUrl = "http://localhost:7071/api/GetCosmosToken";
        private readonly string _cosmosEndpoint = "https://localhost:8081"; // Cosmos DB emulator endpoint
        private readonly TestCosmosClientFactory _clientFactory;

        public AssessmentPlanIntegrationTests()
        {
            Console.WriteLine("Setting up integration tests with HTTP token provider...");

            try
            {
                // Create the HTTP token-based client factory
                _clientFactory = new TestCosmosClientFactory(_azureFunctionUrl, _testUserId, _cosmosEndpoint);

                // Create the store using the token-based factory
                _store = new CosmosDbStore<AssessmentPlan>(_clientFactory, _databaseId, _containerId);

                Console.WriteLine("HTTP token-based CosmosDB store is ready");
                Console.WriteLine($"Using Azure Function: {_azureFunctionUrl}");
                Console.WriteLine($"Using Cosmos endpoint: {_cosmosEndpoint}");
                Console.WriteLine($"Target database: {_databaseId}, container: {_containerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up HTTP token-based Cosmos DB: {ex.Message}");
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
                // Get a container instance through the client factory
                var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);

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
                        // Get a fresh container for each delete operation (fresh token)
                        var deleteContainer = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
                        Console.WriteLine($"Deleting item with id {id} from partition {partitionKey}");
                        await deleteContainer.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
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
                // Don't fail the test setup if cleanup fails
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
            Assert.Equal(assessmentPlan.PlanName, retrievedPlan.PlanName); Assert.Equal(assessmentPlan.OIID, retrievedPlan.OIID);
            // assert that domains are not null
            Assert.NotNull(retrievedPlan.DomainList);
        }
    }
}
