# CustomSync - Offline-First Data Synchronization

## Project Overview

CustomSync is a robust .NET solution that enables **offline-first data synchronization** between local SQLite databases and Azure Cosmos DB. The project demonstrates how to build applications that work seamlessly both online and offline, with automatic conflict resolution when connectivity is restored.

## Key Features

🔄 **Bidirectional Sync** - Data flows seamlessly between local SQLite and remote Cosmos DB  
📱 **Offline-First** - Applications continue working without internet connectivity  
⚡ **Last-Write-Wins** - Simple, effective timestamp-based conflict resolution  
👥 **Multi-User Support** - User-specific data filtering and synchronization  
🧪 **Comprehensive Testing** - Full unit and integration test coverage  
🏗️ **Clean Architecture** - Modular design with clear separation of concerns  

## Technology Stack

- **.NET 9.0** - Modern C# application framework
- **Azure Cosmos DB** - Globally distributed, multi-model database service
- **SQLite** - Lightweight, embedded database for local storage
- **xUnit** - Testing framework with Moq for mocking

## Quick Start

1. **Prerequisites**: .NET 9.0 SDK and Azure Cosmos DB Emulator
2. **Clone** the repository
3. **Restore** dependencies: `dotnet restore`
4. **Run** the application: `cd cosmosofflinewithLCC && dotnet run`

## Architecture Highlights

The solution implements a **three-layer architecture**:

- **Client Layer**: SQLite-based local storage with change tracking
- **Sync Engine**: Bidirectional synchronization with conflict resolution
- **Cloud Layer**: Azure Cosmos DB for persistent, distributed storage

All layers implement a common `IDocumentStore` interface, enabling consistent data access patterns and easy extensibility to other storage providers.

## Use Cases

This pattern is ideal for:
- **Mobile applications** that need offline functionality
- **Field service applications** with intermittent connectivity
- **Distributed systems** requiring eventual consistency
- **Multi-device scenarios** with user-specific data synchronization

## Learn More

For detailed implementation details, API documentation, architecture diagrams, and step-by-step setup instructions, see the [complete README.md](README.md).

---

*CustomSync demonstrates best practices for building resilient, offline-capable applications with modern .NET and Azure technologies.*