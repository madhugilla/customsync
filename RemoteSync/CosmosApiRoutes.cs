using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace RemoteSync
{
    /// <summary>
    /// Azure Functions class that provides all the necessary endpoints for the FunctionDocumentStore.
    /// These endpoints mirror the operations in CosmosDbStore.
    /// </summary>
    public class CosmosApiRoutes
    {
        private readonly ILogger _logger;
        private readonly CosmosClient _cosmosClient;
        private readonly Container _container;
        private const string COSMOS_PARTITION_KEY_NAME = "partitionKey";

        private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CosmosApiRoutes(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<CosmosApiRoutes>();

            // Get configuration from environment variables (populated from local.settings.json by Azure Functions runtime)
            string? connectionString = Environment.GetEnvironmentVariable("CosmosDbConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("CosmosDbConnection environment variable is not set or empty");
                throw new InvalidOperationException("CosmosDbConnection environment variable is not set or empty. Check your local.settings.json file.");
            }

            string? databaseId = Environment.GetEnvironmentVariable("CosmosDbDatabaseId");
            if (string.IsNullOrEmpty(databaseId))
            {
                _logger.LogWarning("CosmosDbDatabaseId environment variable is not set. Using default value: SyncTestDb");
                databaseId = "SyncTestDb";
            }

            string? containerId = Environment.GetEnvironmentVariable("CosmosDbContainerId");
            if (string.IsNullOrEmpty(containerId))
            {
                _logger.LogWarning("CosmosDbContainerId environment variable is not set. Using default value: SyncTestContainer");
                containerId = "SyncTestContainer";
            }

            _logger.LogInformation("Initializing Cosmos DB client with database: {DatabaseId}, container: {ContainerId}", databaseId, containerId);
            _cosmosClient = new CosmosClient(connectionString);
            _container = _cosmosClient.GetContainer(databaseId, containerId);
        }

        [Function("GetItem")]
        public async Task<IActionResult> GetItem(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "GetItem")] HttpRequest req)
        {
            _logger.LogInformation("Processing GetItem request");

            string? id = req.Query["id"];
            string? partitionKey = req.Query["partitionKey"];

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(partitionKey))
            {
                return new BadRequestObjectResult("Please provide both id and partitionKey in the query string");
            }

            try
            {
                var response = await _container.ReadItemStreamAsync(id, new PartitionKey(partitionKey));

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new NotFoundResult();
                }

                using var reader = new StreamReader(response.Content);
                var documentContent = await reader.ReadToEndAsync();

                return new ContentResult
                {
                    Content = documentContent,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Item with id {Id} not found", id);
                return new NotFoundResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving item with id {Id}", id);
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [Function("UpsertItem")]
        public async Task<IActionResult> UpsertItem(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "UpsertItem")] HttpRequest req)
        {
            _logger.LogInformation("Processing UpsertItem request");

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrEmpty(requestBody))
                {
                    return new BadRequestObjectResult("Request body is empty");
                }

                // Parse the document to get the partitionKey property
                var jsonNode = JsonNode.Parse(requestBody);
                if (jsonNode is not JsonObject jsonObject)
                {
                    return new BadRequestObjectResult("Invalid JSON format");
                }

                // Extract the partition key
                if (!jsonObject.TryGetPropertyValue(COSMOS_PARTITION_KEY_NAME, out var partitionKeyNode) ||
                    partitionKeyNode?.GetValue<string>() == null)
                {
                    return new BadRequestObjectResult($"The {COSMOS_PARTITION_KEY_NAME} property is required");
                }

                string partitionKey = partitionKeyNode.GetValue<string>();

                // Use stream to preserve exact JSON structure
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(requestBody));
                var response = await _container.UpsertItemStreamAsync(stream, new PartitionKey(partitionKey));

                using var responseReader = new StreamReader(response.Content);
                var responseContent = await responseReader.ReadToEndAsync();

                return new ContentResult
                {
                    Content = responseContent,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error upserting item");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [Function("UpsertBulk")]
        public async Task<IActionResult> UpsertBulk(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "UpsertBulk")] HttpRequest req)
        {
            _logger.LogInformation("Processing UpsertBulk request");

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrEmpty(requestBody))
                {
                    return new BadRequestObjectResult("Request body is empty");
                }

                // Parse the array of documents
                var jsonNode = JsonNode.Parse(requestBody);
                if (jsonNode is not JsonArray jsonArray)
                {
                    return new BadRequestObjectResult("Invalid JSON format. Expected an array of items.");
                }

                var results = new List<object>();

                // Process each document
                foreach (var item in jsonArray)
                {
                    if (item is not JsonObject jsonObject)
                    {
                        continue;
                    }

                    // Extract the partition key
                    if (!jsonObject.TryGetPropertyValue(COSMOS_PARTITION_KEY_NAME, out var partitionKeyNode) ||
                        partitionKeyNode?.GetValue<string>() == null)
                    {
                        continue;
                    }

                    string partitionKey = partitionKeyNode.GetValue<string>();
                    string itemJson = item.ToJsonString();

                    // Use stream to preserve exact JSON structure
                    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(itemJson));
                    var response = await _container.UpsertItemStreamAsync(stream, new PartitionKey(partitionKey));

                    using var responseReader = new StreamReader(response.Content);
                    var responseContent = await responseReader.ReadToEndAsync();

                    var resultNode = JsonNode.Parse(responseContent);
                    if (resultNode != null)
                    {
                        results.Add(resultNode);
                    }
                }

                return new OkObjectResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in bulk upsert operation");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [Function("GetByUserId")]
        public async Task<IActionResult> GetByUserId(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "GetByUserId")] HttpRequest req)
        {
            _logger.LogInformation("Processing GetByUserId request");

            string? partitionKey = req.Query["partitionKey"];

            if (string.IsNullOrEmpty(partitionKey))
            {
                return new BadRequestObjectResult("Please provide partitionKey in the query string");
            }

            try
            {
                var query = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @partitionKey")
                    .WithParameter("@partitionKey", partitionKey);

                var results = new List<dynamic>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    results.AddRange(response);
                }

                return new OkObjectResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving items for partition key {PartitionKey}", partitionKey);
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [Function("GetAll")]
        public async Task<IActionResult> GetAll(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "GetAll")] HttpRequest req)
        {
            _logger.LogInformation("Processing GetAll request");

            try
            {
                var query = new QueryDefinition("SELECT * FROM c");
                var results = new List<dynamic>();

                using var iterator = _container.GetItemQueryIterator<dynamic>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    results.AddRange(response);
                }

                return new OkObjectResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all items");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
