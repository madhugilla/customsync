using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// HTTP-based token provider that retrieves Cosmos DB resource tokens from a token service
    /// </summary>
    public class HttpTokenProvider : ICosmosTokenProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _tokenEndpoint;
        private readonly ILogger<HttpTokenProvider>? _logger;

        public HttpTokenProvider(HttpClient httpClient, string tokenEndpoint, ILogger<HttpTokenProvider>? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));
            _logger = logger;
        }

        /// <summary>
        /// Retrieves a resource token from the HTTP token service
        /// </summary>
        public async Task<string> GetResourceTokenAsync()
        {
            try
            {
                _logger?.LogInformation("Requesting resource token from {TokenEndpoint}", _tokenEndpoint);

                var response = await _httpClient.PostAsync(_tokenEndpoint, null);
                response.EnsureSuccessStatusCode();

                var token = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("Token service returned empty or null token");
                }

                _logger?.LogInformation("Successfully retrieved resource token");
                return token.Trim();
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError(ex, "Failed to retrieve resource token from {TokenEndpoint}", _tokenEndpoint);
                throw new InvalidOperationException($"Failed to retrieve resource token: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError(ex, "Token request timeout for {TokenEndpoint}", _tokenEndpoint);
                throw new InvalidOperationException($"Token request timeout: {ex.Message}", ex);
            }
        }
    }
}
