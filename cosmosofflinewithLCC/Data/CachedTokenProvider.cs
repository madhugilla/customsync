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
        /// Sets or updates the user ID at runtime
        /// </summary>
        /// <param name="userId">The user ID to use for token requests</param>
        public void SetUserId(string userId)
        {
            // Forward to the inner provider
            _innerProvider.SetUserId(userId);
                
            // Clear the cache since we have a new user ID
            _logger?.LogInformation("User ID changed, invalidating token cache");
            InvalidateCache();
        }
        
        /// <summary>
        /// Invalidates the current token cache, forcing a fresh token on next request
        /// </summary>
        public void InvalidateCache()
        {
            _cachedToken = null;
            _tokenExpiration = DateTime.MinValue;
        }
        
        /// <summary>
        /// Gets a cached token or retrieves a fresh one if the cache is expired
        /// </summary>
        public async Task<string> GetResourceTokenAsync()
        {
            // Fast path - return cached token if it's still valid
            if (!string.IsNullOrEmpty(_cachedToken) && _tokenExpiration > DateTime.UtcNow.Add(_refreshBuffer))
            {
                _logger?.LogDebug("Using cached token valid until {Expiration}", _tokenExpiration);
                return _cachedToken;
            }

            // Slow path - refresh the token
            await _refreshSemaphore.WaitAsync();
            try
            {
                // Double check after acquiring the semaphore
                // Another thread might have refreshed while we were waiting
                if (!string.IsNullOrEmpty(_cachedToken) && _tokenExpiration > DateTime.UtcNow.Add(_refreshBuffer))
                {
                    _logger?.LogDebug("Using cached token valid until {Expiration} (after semaphore check)", _tokenExpiration);
                    return _cachedToken;
                }

                // Actually refresh the token
                _logger?.LogInformation("Refreshing token, current expires at {Expiration}", _tokenExpiration);
                _cachedToken = await _innerProvider.GetResourceTokenAsync();

                // Estimate token expiration (typically resource tokens last 1 hour)
                // The actual expiration time might depend on your token service configuration
                _tokenExpiration = DateTime.UtcNow.AddHours(1);
                _logger?.LogInformation("Token refreshed, new expiration at {Expiration}", _tokenExpiration);

                return _cachedToken;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }
    }
}