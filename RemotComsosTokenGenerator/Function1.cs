using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;

namespace RemotComsosTokenGenerator;

public class Function1
{
    private readonly ILogger<Function1> _logger;

    public Function1(ILogger<Function1> logger)
    {
        _logger = logger;
    }
    [Function("GetCosmosToken")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("GetCosmosToken function started");        // Extract userId from query string or request body
        string? userId = req.Query["userId"];
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("Request missing required userId parameter");
            return new BadRequestObjectResult("userId is required");
        }

        var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_EP");
        var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY");
        var databaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME");
        var containerName = Environment.GetEnvironmentVariable("COSMOS_CONTAINER_NAME");

        // Validate required environment variables
        if (string.IsNullOrEmpty(cosmosEndpoint) || string.IsNullOrEmpty(cosmosKey) ||
            string.IsNullOrEmpty(databaseName) || string.IsNullOrEmpty(containerName))
        {
            _logger.LogCritical("Missing required Cosmos DB configuration - endpoint: {HasEndpoint}, key: {HasKey}, database: {HasDatabase}, container: {HasContainer}",
                !string.IsNullOrEmpty(cosmosEndpoint), !string.IsNullOrEmpty(cosmosKey),
                !string.IsNullOrEmpty(databaseName), !string.IsNullOrEmpty(containerName));
            return new BadRequestObjectResult("Missing required Cosmos DB configuration");
        }
        try
        {
            // Configure Cosmos client options for emulator (bypass SSL validation)
            var cosmosClientOptions = new CosmosClientOptions()
            {
                HttpClientFactory = () =>
                {
                    HttpMessageHandler httpMessageHandler = new HttpClientHandler()
                    {
                        ServerCertificateCustomValidationCallback = (req, cert, certChain, errors) => true
                    };
                    return new HttpClient(httpMessageHandler);
                },
                ConnectionMode = ConnectionMode.Gateway
            };

            var client = new CosmosClient(cosmosEndpoint, cosmosKey, cosmosClientOptions);

            // Ensure the database exists
            var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(databaseName);
            var database = databaseResponse.Database;
            _logger.LogInformation("Database ensured: {DatabaseName}", databaseName);

            // Ensure the container exists with partition key
            var containerResponse = await database.CreateContainerIfNotExistsAsync(containerName, "/partitionKey");
            _logger.LogInformation("Container ensured: {ContainerName}", containerName);

            // TODO: create the user during Infrastructure setup
            var user = await database.UpsertUserAsync(userId);

            var perm = await user.User.UpsertPermissionAsync(
                new PermissionProperties(
                    id: "mobile-access",
                    permissionMode: PermissionMode.All,
                    container: client.GetContainer(databaseName, containerName),
                    resourcePartitionKey: null),
                tokenExpiryInSeconds: 60 * 60);      // 1 h

            _logger.LogInformation("Successfully generated Cosmos token for user: {UserId}", userId);
            _logger.LogInformation("Successfully generated Cosmos token : {Token}", perm.Resource.Token);
            return new OkObjectResult(new PermissionDto { token = perm.Resource.Token });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Cosmos token for user: {UserId}. Error: {ErrorMessage}", userId, ex.Message);

            // Return more specific error information for debugging
            var errorResponse = new
            {
                error = "Failed to generate Cosmos token",
                message = ex.Message,
                type = ex.GetType().Name,
                userId = userId,
                cosmosEndpoint = cosmosEndpoint,
                databaseName = databaseName,
                containerName = containerName
            };

            return new ObjectResult(errorResponse) { StatusCode = 500 };
        }
    }
}