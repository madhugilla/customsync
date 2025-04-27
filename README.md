# Cosmos DB Offline Synchronization with LCC

This project demonstrates an offline-first synchronization solution between Azure Cosmos DB and SQLite using Last-Changed-Continues (LCC) conflict resolution strategy.

## Overview

The solution provides a bidirectional sync mechanism allowing applications to work offline with a local SQLite database and synchronize with Azure Cosmos DB when connectivity is available. It uses last-modified timestamps to manage conflict resolution.

### Key Components

- **SyncEngine**: Core synchronization logic with LCC conflict resolution strategy
- **CosmosDbStore**: Repository implementation for Azure Cosmos DB
- **SqliteStore**: Repository implementation for local SQLite database
- **Unit and Integration Tests**: Comprehensive test suite

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Azure Cosmos DB Emulator](https://learn.microsoft.com/en-us/azure/cosmos-db/local-emulator) for local development
- [Visual Studio Code](https://code.visualstudio.com/) with C# extension (optional)

## Getting Started

### Installation

1. Clone the repository
2. Navigate to the project directory
3. Restore dependencies:
   ```
   dotnet restore
   ```

### Running the Cosmos DB Emulator

Before running the application, start the Azure Cosmos DB Emulator:

```
"C:\Program Files\Azure Cosmos DB Emulator\CosmosDB.Emulator.exe" /NoUI /NoExplorer /AllowNetworkAccess
```

Alternatively, if you're using VS Code, you can use the provided task:
- Press `Ctrl+Shift+P`
- Type "Tasks: Run Task"
- Select "Start Cosmos DB Emulator"

### Running the Application

Execute from the command line:
```
cd cosmosofflinewithLCC
dotnet run
```

Or if using VS Code:
- Open the project in VS Code
- Press F5 to build and run with debugger attached

### Environment Variables

The application uses the following environment variables, which are automatically set in the VS Code launch configuration:

- `COSMOS_ENDPOINT`: The Cosmos DB endpoint (defaults to the emulator endpoint: https://localhost:8081/)
- `COSMOS_KEY`: The Cosmos DB key (defaults to the emulator key)

## Project Structure

- **cosmosofflinewithLCC/**: Main application project
  - **Data/**: Data access layer with store implementations
  - **Models/**: Domain model classes
  - **Sync/**: Synchronization engine 
- **tests/**: Testing projects
  - **cosmosofflinewithLCC.Tests/**: Unit tests
  - **cosmosofflinewithLCC.IntegrationTests/**: Integration tests

## Implementation Details

### Sync Engine

The SyncEngine implements a bidirectional synchronization strategy:

1. **Push Phase**: Pushes pending local changes to the remote Cosmos DB
2. **Pull Phase**: Pulls remote changes to the local SQLite database

Conflict resolution uses the Last-Changed-Continues (LCC) strategy where:
- If local timestamp > remote timestamp: local version wins
- If remote timestamp > local timestamp: remote version wins

### Data Storage

- **SqliteStore**: Uses SQLite for local offline storage
- **CosmosDbStore**: Communicates with Azure Cosmos DB

### Models

Models must implement:
- A unique ID property
- A LastModified timestamp property for conflict resolution

## Testing

Run the unit tests:
```
dotnet test tests/cosmosofflinewithLCC.Tests
```

Run the integration tests (requires the Cosmos DB Emulator to be running):
```
dotnet test tests/cosmosofflinewithLCC.IntegrationTests
```

## Best Practices Implemented

- **Error Handling**: Comprehensive error logging
- **Bulk Operations**: Batch processing for efficient synchronization
- **Connection Pooling**: Optimized connection management
- **Retry Logic**: Exponential backoff for transient failures
- **Flexible Configuration**: Environment-based configuration

## Security Considerations

- Credentials are never hardcoded
- Environment variables for sensitive information
- In production, use Key Vault for storing secrets
- Consider using Managed Identity in Azure deployments

## Limitations

- Soft deletes not yet implemented (see TODO in SyncEngine)
- Partial document updates not supported

## License

[MIT License](LICENSE)