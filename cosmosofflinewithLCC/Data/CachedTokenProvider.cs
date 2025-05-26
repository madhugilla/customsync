using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Data
{
    /// <summary>
    /// Cached token provider that wraps another token provider and caches tokens until near expiration
    /// This reduces calls to the underlying token service
    /// </summary>
    public class CachedTokenProvider : ICosmosTokenProvider
    {
        private readonly ICosmosTokenProvider _innerProvider;
        private readonly ILogger<CachedTokenProvider>? _logger;
        private readonly TimeSpan _refreshBuffer;
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

        private string? _cachedToken;
        private DateTime _tokenExpiration = DateTime.MinValue;

        public CachedTokenProvider(
            ICosmosTokenProvider innerProvider,
            TimeSpan? refreshBuffer = null,
            ILogger<CachedTokenProvider>? logger = null)
        {
            _innerProvider = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
            _refreshBuffer = refreshBuffer ?? TimeSpan.FromMinutes(5); // Refresh 5 minutes before expiry by default
            _logger = logger;
        }

        /// <summary>
        /// Gets a cached token or retrieves a fresh one if the cache is expired
        /// </summary>
        public async Task<string> GetResourceTokenAsync()
        {
            // Check if we have a valid cached token
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiration.Subtract(_refreshBuffer))
            {
                _logger?.LogDebug("Using cached resource token (expires at {TokenExpiration})", _tokenExpiration);
                return _cachedToken;
            }

            // Need to refresh the token
            await _refreshSemaphore.WaitAsync();
            try
            {
                // Double-check pattern - another thread might have refreshed while we were waiting
                if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiration.Subtract(_refreshBuffer))
                {
                    _logger?.LogDebug("Using cached resource token (refreshed by another thread)");
                    return _cachedToken;
                }

                _logger?.LogInformation("Refreshing resource token (previous token expires at {TokenExpiration})", _tokenExpiration);

                var newToken = await _innerProvider.GetResourceTokenAsync();

                // Assume token is valid for 1 hour (typical Cosmos resource token lifetime)
                // In a real implementation, you might parse the token to get actual expiration
                _tokenExpiration = DateTime.UtcNow.AddHours(1);
                _cachedToken = newToken;

                _logger?.LogInformation("Successfully refreshed resource token (new expiration: {TokenExpiration})", _tokenExpiration);
                return newToken;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        /// <summary>
        /// Clears the cached token to force a refresh on next request
        /// </summary>
        public void InvalidateCache()
        {
            _logger?.LogInformation("Invalidating cached token");
            _cachedToken = null;
            _tokenExpiration = DateTime.MinValue;
        }
    }
}
