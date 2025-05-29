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
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("Request missing required userId parameter");
            return new BadRequestObjectResult("userId is required");
        }

        var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_EP");
        var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY");
        var databaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME");
        var containerName = Environment.GetEnvironmentVariable("COSMOS_CONTAINER_NAME");
        var tokenExpirySecondsStr = Environment.GetEnvironmentVariable("COSMOS_TOKEN_EXPIRY_SECONDS");

        // Parse token expiry seconds with default fallback of 1 hour (3600 seconds)
        int tokenExpirySeconds = 3600; // Default 1 hour
        if (!string.IsNullOrEmpty(tokenExpirySecondsStr) && int.TryParse(tokenExpirySecondsStr, out int configuredExpiry))
        {
            tokenExpirySeconds = configuredExpiry;
            _logger.LogInformation("Using configured token expiry: {ExpirySeconds} seconds", tokenExpirySeconds);
        }
        else
        {
            _logger.LogInformation("Using default token expiry: {ExpirySeconds} seconds", tokenExpirySeconds);
        }

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
        {            // Simple Cosmos client configuration
            var client = new CosmosClient(cosmosEndpoint, cosmosKey);

            // Get reference to the existing database and container
            var database = client.GetDatabase(databaseName);
            _logger.LogInformation("Using database: {DatabaseName}", databaseName);

            // Create the user (this still needs to be done at runtime for token generation)
            var user = await database.UpsertUserAsync(userId);

            var perm = await user.User.UpsertPermissionAsync(
                new PermissionProperties(
                    id: "mobile-access",
                    permissionMode: PermissionMode.All,
                    container: client.GetContainer(databaseName, containerName),
                    resourcePartitionKey: null),
                tokenExpiryInSeconds: tokenExpirySeconds);

            _logger.LogInformation("Successfully generated Cosmos token for user: {UserId}", userId);
            _logger.LogInformation("Successfully generated Cosmos token : {Token}", perm.Resource.Token);

            // Create DTO with token and calculated expiry time
            var permissionDto = new PermissionDto
            {
                Token = perm.Resource.Token,
                ExpiryDateTime = DateTime.UtcNow.AddSeconds(tokenExpirySeconds)
            };

            return new OkObjectResult(permissionDto);
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
            };

            return new ObjectResult(errorResponse) { StatusCode = 500 };
        }
    }
}