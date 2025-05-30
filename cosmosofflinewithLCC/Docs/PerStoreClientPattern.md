# Per-Store Client Pattern for Cosmos DB Access

## Overview

This document outlines the architecture decision to implement a per-store client pattern for Cosmos DB access in the MAUI offline-first application. Each `CosmosDbStore<T>` instance maintains its own `CosmosClient` instance rather than sharing a centralized `CosmosDbClientManager`.

## Architectural Decision

### Previous Pattern: Centralized CosmosDbClientManager

In the previous architecture, a single `CosmosDbClientManager` was responsible for:
- Creating and managing a CosmosClient instance
- Refreshing authorization tokens using AzureKeyCredential
- Caching container references
- Serving multiple CosmosDbStore instances

### New Pattern: Per-Store Clients

In the current architecture, each `CosmosDbStore<T>` instance:
- Maintains its own CosmosClient instance
- Handles its own token refreshing via AzureKeyCredential
- Caches its own container reference
- Is completely self-contained and independent

## Rationale

### Advantages of the Per-Store Pattern

1. **Simpler Code Organization**
   - Each store is self-contained and not dependent on an external manager
   - Dependencies are more explicit and easier to understand

2. **Isolation Between Stores**
   - Operations in one store don't affect others
   - Different store configurations can be applied if needed

3. **Reduced Coupling**
   - Stores can be deployed or instantiated independently
   - Changes to one store's client configuration don't affect others

### Trade-offs

1. **Resource Consumption**
   - Multiple CosmosClient instances consume more memory and resources
   - May be a concern on resource-constrained devices

2. **Token Management Duplication**
   - Each store implements the same token refresh logic
   - Mitigation: The token provider (`ICosmosTokenProvider`) remains centralized

## Implementation Details

Each `CosmosDbStore<T>` now includes:

1. **Internal Client Management**
   ```csharp
   private CosmosClient? _client;
   private AzureKeyCredential? _keyCredential;
   ```

2. **Token Refresh Logic**
   ```csharp
   private async Task RefreshTokenIfNeededAsync()
   {
       if (_keyCredential == null) return;
       var token = await _tokenProvider.GetResourceTokenAsync();
       _keyCredential.Update(token);
   }
   ```

3. **Container Caching**
   ```csharp
   private Container? _container;
   
   private async Task<Container> GetContainerAsync()
   {
       if (_container != null)
       {
           await RefreshTokenIfNeededAsync();
           return _container;
       }
       
       var client = await GetClientAsync();
       _container = client.GetContainer(_databaseId, _containerId);
       return _container;
   }
   ```

## Best Practices

- Use the `InvalidateCacheAndRefreshToken()` method when experiencing authorization issues
- Consider the resource implications when creating many store instances
- To optimize resource usage, consider creating short-lived stores as needed rather than long-lived singletons

## Future Considerations

- Monitor memory usage to ensure this approach is appropriate for the target devices
- Consider a hybrid approach if resource constraints become an issue
- Evaluate token refresh strategies to minimize unnecessary refreshes
