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
        _logger.LogInformation("GetCosmosToken function started");

        // Extract userId from query string or request body
        string? userId = req.Query["userId"];
        userId = "fasdfas"; // For testing purposes, you can hardcode a userId
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
            var client = new CosmosClient(cosmosEndpoint, cosmosKey);
            //TODO: create the user during Infrastructure setup
            var user = await client.GetDatabase(databaseName).UpsertUserAsync(userId);

            var perm = await user.User.UpsertPermissionAsync(
                new PermissionProperties(
                    id: "mobile-access",
                    permissionMode: PermissionMode.All,
                    container: client.GetContainer(databaseName, containerName),
                    resourcePartitionKey: null),
                tokenExpiryInSeconds: 60 * 60);      // 1 h

            _logger.LogInformation("Successfully generated Cosmos token for user: {UserId}", userId);

            _logger.LogInformation("Successfully generated Cosmos token : {UserId}", perm.Resource.Token);
            return new OkObjectResult(new PermissionDto { token = perm.Resource.Token });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Cosmos token for user: {UserId}", userId);
            return new StatusCodeResult(500);
        }
    }
}