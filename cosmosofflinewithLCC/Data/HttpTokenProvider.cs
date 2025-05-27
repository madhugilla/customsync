using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// HTTP-based token provider that retrieves Cosmos DB resource tokens from a token service
    /// </summary>
    public class HttpTokenProvider : ICosmosTokenProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _tokenEndpoint;
        private string? _userId;
        private readonly ILogger<HttpTokenProvider>? _logger;

        /// <summary>
        /// Initializes a new instance of the HttpTokenProvider
        /// </summary>
        /// <param name="httpClient">HTTP client for making requests</param>
        /// <param name="tokenEndpoint">Token service endpoint URL</param>
        /// <param name="userId">Optional user ID (can be set later with SetUserId)</param>
        /// <param name="logger">Optional logger</param>
        public HttpTokenProvider(HttpClient httpClient, string tokenEndpoint, string? userId = null, ILogger<HttpTokenProvider>? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));
            _userId = userId; // userId is now optional
            _logger = logger;
        }

        /// <summary>
        /// Sets or updates the user ID at runtime
        /// </summary>
        /// <param name="userId">The user ID to use for token requests</param>
        /// <exception cref="ArgumentNullException">Thrown if userId is null or empty</exception>
        public void SetUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentNullException(nameof(userId));
            }
            
            _userId = userId;
            _logger?.LogInformation("User ID updated to {UserId}", userId);
        }

        /// <summary>
        /// Retrieves a resource token from the HTTP token service
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if no user ID has been set</exception>
        public async Task<string> GetResourceTokenAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_userId))
                {
                    throw new InvalidOperationException("User ID must be set before requesting a resource token. Call SetUserId first.");
                }

                _logger?.LogInformation("Requesting resource token from {TokenEndpoint} for user {UserId}", _tokenEndpoint, _userId); 
                var requestUrl = $"{_tokenEndpoint}?userId={Uri.EscapeDataString(_userId)}";
                var response = await _httpClient.GetAsync(requestUrl);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger?.LogError("Token service returned error {StatusCode}: {ErrorContent}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Token service returned {response.StatusCode}: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    throw new InvalidOperationException("Token service returned empty or null response");
                }

                // Try to parse as PermissionDto JSON
                try
                {
                    var permissionDto = JsonConvert.DeserializeObject<PermissionDto>(responseContent);
                    if (permissionDto?.token != null)
                    {
                        _logger?.LogInformation("Successfully retrieved resource token for user {UserId}", _userId);
                        return permissionDto.token;
                    }
                }
                catch (JsonException)
                {
                    // If JSON parsing fails, treat as plain text token
                    _logger?.LogDebug("Response is not JSON, treating as plain text token");
                }

                // Fallback to plain text
                var token = responseContent.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("Token service returned empty token");
                }

                _logger?.LogInformation("Successfully retrieved resource token for user {UserId}", _userId);
                return token;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError(ex, "Failed to retrieve resource token from {TokenEndpoint} for user {UserId}", _tokenEndpoint, _userId);
                throw new InvalidOperationException($"Failed to retrieve resource token: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError(ex, "Token request timeout for {TokenEndpoint} for user {UserId}", _tokenEndpoint, _userId);
                throw new InvalidOperationException($"Token request timeout: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// DTO for token response from Azure Function
    /// </summary>
    public class PermissionDto
    {
        public string? token { get; set; }
    }
}