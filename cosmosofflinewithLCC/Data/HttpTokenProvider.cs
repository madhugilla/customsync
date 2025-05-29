using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// HTTP-based token provider that retrieves Cosmos DB resource tokens from a token service with caching
    /// </summary>
    public class HttpTokenProvider : ICosmosTokenProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _tokenEndpoint;
        private string? _userId;
        private readonly ILogger<HttpTokenProvider>? _logger;
        private readonly IMemoryCache _cache;

        /// <summary>
        /// Initializes a new instance of the HttpTokenProvider
        /// </summary>
        /// <param name="httpClient">HTTP client for making requests</param>
        /// <param name="tokenEndpoint">Token service endpoint URL</param>
        /// <param name="cache">Memory cache for token caching</param>
        /// <param name="userId">Optional user ID (can be set later with SetUserId)</param>
        /// <param name="logger">Optional logger</param>
        public HttpTokenProvider(
            HttpClient httpClient,
            string tokenEndpoint,
            IMemoryCache cache,
            string? userId = null,
            ILogger<HttpTokenProvider>? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _userId = userId;
            _logger = logger;
        }

        /// <summary>
        /// Sets or updates the user ID at runtime and clears any cached tokens for the previous user
        /// </summary>
        /// <param name="userId">The user ID to use for token requests</param>
        /// <exception cref="ArgumentNullException">Thrown if userId is null or empty</exception>
        public void SetUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentNullException(nameof(userId));
            }

            var previousUserId = _userId;
            _userId = userId;

            // Clear cache for previous user if different
            if (previousUserId != null && previousUserId != userId)
            {
                var previousCacheKey = GetCacheKey(previousUserId);
                _cache.Remove(previousCacheKey);
                _logger?.LogInformation("Cleared cached token for previous user {PreviousUserId}", previousUserId);
            }

            _logger?.LogInformation("User ID updated to {UserId}", userId);
        }        /// <summary>
                 /// Retrieves a resource token from cache or HTTP token service
                 /// </summary>
                 /// <exception cref="InvalidOperationException">Thrown if no user ID has been set</exception>
        public async Task<string> GetResourceTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(_userId))
            {
                throw new InvalidOperationException("User ID must be set before requesting a resource token. Call SetUserId first.");
            }

            var cacheKey = GetCacheKey(_userId);

            // Try to get from cache first
            if (_cache.TryGetValue(cacheKey, out string? cachedToken))
            {
                _logger?.LogDebug("Using cached token for user {UserId}", _userId);
                return cachedToken!;
            }            // Cache miss - fetch token and expiry information
            var (token, tokenExpiry) = await FetchTokenWithExpiryAsync();

            // Fixed buffer time before token expiry (5 minutes)
            TimeSpan cacheBuffer = TimeSpan.FromMinutes(5);

            // Cache with absolute expiration based on token expiry
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = tokenExpiry - cacheBuffer,
                Priority = CacheItemPriority.Normal
            };


            _cache.Set(cacheKey, token, cacheOptions);

            _logger?.LogInformation("Successfully retrieved and cached resource token for user {UserId} until {ExpiryTime}",
                _userId, tokenExpiry - cacheBuffer);
            return token;
        }


        private string GetCacheKey(string userId) => $"cosmos_token_{userId}";

        /// <summary>
        /// Fetches a token and its expiry time from the token service
        /// </summary>
        /// <returns>Tuple containing the token and its expiry time</returns>
        private async Task<(string token, DateTime tokenExpiry)> FetchTokenWithExpiryAsync()
        {
            _logger?.LogInformation("Requesting resource token from {TokenEndpoint} for user {UserId}", _tokenEndpoint, _userId);

            var requestUrl = $"{_tokenEndpoint}?userId={Uri.EscapeDataString(_userId!)}";
            var response = await _httpClient.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogError("Token service returned error {StatusCode}: {ErrorContent}", response.StatusCode, errorContent);
                throw new HttpRequestException($"Token service returned {response.StatusCode}: {errorContent}");
            }

            // Use ReadFromJsonAsync for deserialization
            var permissionDto = await response.Content.ReadFromJsonAsync<PermissionDto>();

            if (permissionDto == null || permissionDto.Token == null)
            {
                throw new InvalidOperationException("Token service returned null or invalid token in JSON response");
            }

            return (permissionDto.Token, permissionDto.ExpiryDateTime);
        }

    }

    /// <summary>
    /// DTO for token response from Azure Function
    /// </summary>
    public class PermissionDto
    {
        public string? Token { get; set; }
        public DateTime ExpiryDateTime { get; set; }
    }
}