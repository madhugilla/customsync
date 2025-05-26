# Cosmos DB Token-Based Authentication Implementation

This implementation migrates from key-based authentication to token-based authentication using Cosmos DB resource tokens with a Factory Pattern approach.

## Overview

The Factory Pattern creates fresh CosmosClient instances on-demand with current resource tokens, eliminating the complexity of token refresh and client recreation.

## Architecture Components

### 1. Core Interfaces

#### `ICosmosTokenProvider`
- Provides resource tokens for Cosmos DB authentication
- Implement this interface with your specific token source

#### `ICosmosClientFactory`
- Creates CosmosClient instances with fresh tokens
- Returns Container instances for operations

### 2. Implementation Classes

#### `CosmosClientFactory`
- Core factory implementation
- Creates CosmosClient with fresh tokens from provider
- Returns Container instances for database operations

#### `CosmosDbStore<T>`
- Updated to use factory pattern instead of shared Container
- Each operation gets a fresh Container with current token
- No complex retry logic needed - token expiry handled naturally

## Token Provider Options

### 1. SampleTokenProvider (Template)
```csharp
// Replace this with your actual implementation
services.AddSingleton<ICosmosTokenProvider>(provider => 
    new SampleTokenProvider(tokenEndpoint));
```

### 2. HttpTokenProvider (Available but not configured)
- HTTP client-based token retrieval
- Calls REST API to get resource tokens
- Requires additional HTTP client configuration

### 3. CachedTokenProvider (Available but not configured)
- Wraps any token provider with caching
- Reduces calls to token service
- Configurable refresh buffer time

## Configuration

### Environment Variables
```
COSMOS_ENDPOINT=https://your-cosmos.documents.azure.com:443/
TOKEN_ENDPOINT=https://your-token-service/api/token
```

### DI Registration (Program.cs)
```csharp
// Token provider (replace with your implementation)
services.AddSingleton<ICosmosTokenProvider>(provider => 
    new SampleTokenProvider(tokenEndpoint));

// Client factory
services.AddSingleton<ICosmosClientFactory>(provider =>
{
    var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
    return new CosmosClientFactory(tokenProvider, cosmosEndpoint, clientOptions);
});

// Stores (updated to use factory)
services.AddSingleton<CosmosDbStore<Item>>(provider =>
{
    var clientFactory = provider.GetRequiredService<ICosmosClientFactory>();
    return new CosmosDbStore<Item>(clientFactory, databaseId, containerId);
});
```

## Usage Pattern

### Store Operations
```csharp
public async Task<List<T>> GetAllAsync()
{
    // Factory creates fresh client with current token
    var container = await _clientFactory.GetContainerAsync(_databaseId, _containerId);
    
    // Use container for operations
    var query = container.GetItemQueryIterator<T>();
    // ... rest of operation
}
```

## Token Expiry Behavior

### Between Operations (Normal Case)
- Each operation gets a fresh token
- Clean transition with no errors
- No retry logic needed

### During Long Operations
- Operation may fail with 401 Unauthorized
- Next operation automatically gets fresh token
- Application should handle partial failures in batch operations

## Error Handling

### Token Provider Failure
- Operations fail immediately if token cannot be obtained
- Check token service availability and configuration

### Token Expiry During Operation
- CosmosException with StatusCode.Unauthorized
- Retry logic can be added at application level if needed

### Network Issues
- Standard Cosmos SDK retry policies apply
- Token-related errors are separate from network issues

## Testing

### Unit Tests
- Mock `ICosmosTokenProvider` for predictable token values
- Mock `ICosmosClientFactory` for testing store logic
- Test token expiry scenarios by controlling provider behavior

### Integration Tests
- Use test token provider with short-lived tokens
- Verify behavior across token expiration boundaries
- Test error handling with invalid tokens

## Migration Steps

1. **✅ Implemented**: Core interfaces and factory classes
2. **✅ Implemented**: Updated CosmosDbStore to use factory pattern
3. **✅ Implemented**: Updated DI registration in Program.cs
4. **🔄 In Progress**: Replace SampleTokenProvider with your actual implementation
5. **📋 Next**: Update integration tests
6. **📋 Next**: Deploy and monitor token refresh patterns

## Next Steps

1. **Implement Token Provider**: Replace `SampleTokenProvider` with your actual token source
2. **Add Caching**: Consider using `CachedTokenProvider` to reduce token service calls
3. **Update Tests**: Modify integration tests to work with token-based authentication
4. **Monitor**: Add logging and metrics for token refresh frequency
5. **Optimize**: Consider adding retry logic for transient token failures if needed

## Performance Considerations

- **Token Provider Caching**: Use `CachedTokenProvider` to reduce token service calls
- **Client Creation Overhead**: ~10-50ms per operation (acceptable for most scenarios)
- **Connection Pooling**: Each CosmosClient maintains its own connection pool
- **Memory Usage**: Short-lived clients are garbage collected automatically

## Security Benefits

- **No Stored Secrets**: Resource tokens are short-lived and scoped
- **Automatic Expiry**: Tokens expire without manual intervention
- **Fine-Grained Permissions**: Resource tokens can be scoped to specific containers/operations
- **Audit Trail**: Token requests can be logged and monitored

## CosmosClientFactory Configuration Options

The `CosmosClientFactory` now encapsulates all Cosmos DB client configuration, providing multiple ways to configure the client based on your needs:

### Option 1: Default Configuration (Recommended)

Uses factory-provided optimal settings for token-based authentication:

```csharp
services.AddSingleton<ICosmosClientFactory>(provider =>
{
    var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
    return new CosmosClientFactory(tokenProvider, cosmosEndpoint);
});
```

**Default settings include:**

- Environment-aware connection mode (Gateway for dev, Direct for production)
- Optimized retry policies for token scenarios
- Conservative timeouts suitable for resource tokens
- Bulk operations disabled (can cause issues with resource tokens)
- CamelCase JSON serialization

### Option 2: Environment-Specific Configuration

Allows explicit environment configuration:

```csharp
services.AddSingleton<ICosmosClientFactory>(provider =>
{
    var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
    var isDevelopment = Environment.GetEnvironmentVariable("ENVIRONMENT") == "Development";
    return new CosmosClientFactory(tokenProvider, cosmosEndpoint, isDevelopment);
});
```

### Option 3: Custom Configuration

For advanced scenarios requiring specific options:

```csharp
services.AddSingleton<ICosmosClientFactory>(provider =>
{
    var tokenProvider = provider.GetRequiredService<ICosmosTokenProvider>();
    var customOptions = new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Direct,
        MaxRetryAttemptsOnRateLimitedRequests = 5,
        RequestTimeout = TimeSpan.FromSeconds(90)
    };
    return new CosmosClientFactory(tokenProvider, cosmosEndpoint, customOptions);
});
```

## Benefits of Factory-Based Configuration

1. **Encapsulation**: All client configuration is contained within the factory
2. **Consistency**: Same configuration approach across all environments
3. **Simplicity**: Sensible defaults reduce configuration complexity
4. **Flexibility**: Override capability for special requirements
5. **Token Optimization**: Settings optimized for resource token scenarios
