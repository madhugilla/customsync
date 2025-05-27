# ADR-002: Mobile App Cosmos DB Access Pattern

**Date:** 2025-05-27

**Status:** Accepted

**Deciders:** Development Team, Solution Architect

**Technical Story:** Mobile app requires robust online/offline data synchronization with Cosmos DB using local-first architecture with Last Write Wins conflict resolution. **The app performs only data synchronization operations with no business logic, schema evolution, or caching requirements.** Decision required on whether to access Cosmos DB directly or through an intermediary API layer for optimal sync performance.

## Context and Problem Statement

The mobile application requires seamless online/offline data synchronization with Azure Cosmos DB to support offline-first functionality using a local-first architecture. **The app is specifically designed to handle only synchronization operations - pure data sync with no business logic, schema evolution, caching, or complex processing beyond basic CRUD sync operations.** The system implements Last Write Wins (LWW) conflict resolution for simplicity and performance. We need to decide whether the mobile app should connect directly to Cosmos DB or access data through an intermediary API layer. This decision impacts sync performance, security, and maintainability of the solution.

### Forces

- **Security**: Direct database access from mobile apps requires secure credential management
- **Sync Performance**: Direct access eliminates network hops while API layer may introduce latency
- **Offline Capability**: Mobile apps need robust offline functionality and seamless sync when connectivity returns
- **Development Complexity**: Direct access is simpler for sync-only operations vs. API layer overhead
- **Sync Reliability**: System must handle intermittent connectivity and conflict resolution
- **Pure Synchronization Focus**: System performs only data synchronization without additional processing
- **Runtime Security**: Endpoint details and credentials should be obtained at runtime, not embedded in app
- **Local-First Architecture**: Data operations must work immediately on local storage with background sync
- **Conflict Resolution Simplicity**: Last Write Wins strategy minimizes complexity while ensuring eventual consistency

## Decision Drivers

- **Security Requirements**: Minimize exposure of database credentials and implement proper authentication
- **Offline-First Architecture**: Mobile app must function without network connectivity
- **Sync Performance**: Minimize latency for synchronization operations while maintaining security
- **Sync Reliability**: Solution must handle intermittent connectivity and ensure data consistency
- **Development Speed**: Quick implementation to validate offline synchronization concepts
- **Resource Token Implementation**: Leverage Cosmos DB resource tokens for secure, scoped access

## Considered Options

### Option 1: Direct Cosmos DB Access with Resource Tokens

**Description:** Mobile app connects directly to Cosmos DB using resource tokens obtained from an Azure Function. **Cosmos DB provides a native HTTP REST API that can be accessed directly from any HTTP client, making it well-suited for direct mobile app integration.** App implements local SQLite store for offline functionality with local-first data operations and syncs directly with Cosmos DB when online using Last Write Wins conflict resolution. **The app performs only synchronization operations - no business logic or data processing beyond basic CRUD sync.**

**Pros:**

- **Reduced Latency**: Eliminates API layer network hop
- **Simplified Architecture**: Fewer components to deploy and maintain
- **Resource Token Security**: Leverages Cosmos DB's built-in security with time-limited, scoped tokens
- **Runtime Endpoint Discovery**: Cosmos DB endpoint details are downloaded only after successful authentication, not embedded in the app
- **Offline-First Design**: Local SQLite enables full offline functionality with immediate local operations
- **Direct SDK Benefits**: Full access to Cosmos DB SDK features (batch operations, query optimization)
- **Native HTTP API**: Cosmos DB's built-in HTTP REST API enables direct communication without intermediary layers
- **Cost Effective**: No additional API hosting costs
- **Development Speed**: Faster initial implementation and prototyping
- **Perfect Sync Match**: Direct access ideal for sync-only operations without business logic
- **LWW Compatibility**: Direct access to Cosmos DB timestamps enables efficient Last Write Wins conflict resolution using last modified dates

**Cons:**

- **Client Complexity**: Mobile app handles all sync logic and Last Write Wins conflict resolution
- **Distributed Monitoring**: Sync patterns monitored on client-side rather than centralized (addressed by Sentry integration)

### Option 2: API Layer with Database Access

**Description:** Mobile app connects to a custom Web API that handles all Cosmos DB interactions. API provides RESTful endpoints for sync operations and handles synchronization logic and conflict resolution server-side. **For a pure sync-only system with simple Last Write Wins resolution, this adds unnecessary complexity and latency.**

**Pros:**

- **Better Security**: Database credentials never exposed to mobile clients
- **Centralized Sync Logic**: Synchronization rules and conflict resolution in one place
- **Better Monitoring**: Centralized logging and analytics for sync operations

**Cons:**

- **Increased Latency**: Additional network hop for every sync operation
- **More Complexity**: Additional deployment, hosting, and maintenance overhead
- **Single Point of Failure**: API becomes critical dependency for mobile app
- **Development Overhead**: More components to build, test, and deploy
- **Higher Costs**: Additional hosting and compute costs for API layer
- **Offline Challenges**: Still need local storage and sync logic in mobile app
- **Over-Engineering**: Unnecessary complexity for pure synchronization operations

## Decision Outcome

**Chosen option:** "Option 1: Direct Cosmos DB Access with Resource Tokens", because it best aligns with our current requirements and constraints.

### Justification

1. **Local-First Performance**: Direct access with local SQLite provides immediate user responses for all operations, while Option 2 would require API calls for each operation, degrading user experience

2. **LWW Optimization**: Direct access to Cosmos DB timestamps enables efficient Last Write Wins conflict resolution using last modified dates without server-side processing, while Option 2 would require custom API logic for the same simple conflict resolution

3. **Offline-First Priority**: Direct access with local SQLite perfectly supports offline-first architecture, while Option 2 still requires complex offline logic plus API dependency management

4. **Security Equivalence with Better Performance**: Resource tokens provide the same enterprise-grade security as Option 2's API layer, but without the performance penalty of additional network hops

5. **Reduced Attack Surface**: Contrary to traditional concerns, our runtime endpoint discovery approach means no database credentials are embedded in the app, matching Option 2's security while eliminating the API layer as an additional attack vector

6. **Performance Advantage**: Eliminates 50% of network calls compared to Option 2 (direct to Cosmos vs. mobile→API→Cosmos), crucial for mobile environments with limited bandwidth

7. **Cost Effectiveness**: Zero additional hosting costs vs. Option 2's requirement for API hosting, load balancing, and scaling infrastructure

8. **Simplified Deployment Pipeline**: Single-component deployment vs. Option 2's multi-service coordination, reducing deployment complexity and potential failure points

9. **Native Database Capabilities**: Full access to Cosmos DB SDK optimizations (batch operations, query optimization, change feed) vs. Option 2's limited API endpoints

10. **Purpose-Built Architecture**: Cosmos DB's HTTP REST API is specifically designed for direct client access, making Option 2's custom API layer redundant infrastructure

11. **Runtime Security Model**: Endpoint discovery after authentication provides the same credential protection as Option 2 without the architectural overhead

12. **Sync-Optimized Design**: For pure synchronization operations, direct database access eliminates unnecessary abstraction layers that add complexity without functional benefit

13. **Development Velocity**: Single codebase focus vs. Option 2's requirement to build, test, and maintain both mobile app AND API layer simultaneously

14. **Operational Simplicity**: One service to monitor and maintain vs. Option 2's multiple services, reducing operational overhead and potential points of failure

### Positive Consequences

- **Fast Development**: Rapid prototyping and implementation of offline sync functionality without the overhead of building parallel API infrastructure
- **Optimal Performance**: Direct database access provides best possible latency for sync operations, eliminating the 50% performance penalty of Option 2's additional network hop
- **Strong Security**: Resource tokens provide time-limited, scoped access without credential exposure, matching Option 2's security without the complexity
- **True Offline Capability**: Local SQLite ensures app works completely offline, while Option 2 would still require the same offline infrastructure plus API dependency management
- **Lower Infrastructure Costs**: No additional API hosting requirements, saving hosting, scaling, and maintenance costs compared to Option 2
- **Simplified Deployment**: Fewer components to deploy and maintain, reducing deployment complexity and potential points of failure compared to Option 2's multi-service architecture
- **Native SDK Benefits**: Full access to Cosmos DB's optimized batch operations, change feed, and query capabilities that would be limited or unavailable through Option 2's custom API endpoints

### Negative Consequences

- **Client-Side Complexity**: Mobile app responsible for all sync logic and Last Write Wins conflict resolution
- **Distributed Monitoring**: Sync patterns and errors are monitored on individual clients rather than centralized (mitigated by Sentry integration for error tracking and performance monitoring)

---

## Notes

### Key Assumptions

- **Single User Model**: Current implementation supports single user for simplicity
- **Development Priority**: Speed of development prioritized over architectural flexibility
- **Resource Token Reliability**: Azure Functions provide sufficient reliability for token generation
- **Network Patterns**: Mobile app has intermittent connectivity requiring robust offline support
- **Sync-Only Operations**: System performs only data synchronization with no business logic or processing
- **No Schema Evolution**: Database schema is stable with no expected changes requiring versioning
- **No Caching Requirements**: Pure sync operations without server-side caching needs
- **Client-Side Monitoring**: Sentry integration provides adequate error tracking and performance monitoring for distributed sync operations
- **Last Write Wins Acceptability**: Simple LWW conflict resolution is sufficient for the use case, avoiding complex merge logic
- **Local-First Pattern**: All operations work immediately on local data with background synchronization to remote store

### Future Considerations

- **Multi-User Support**: Will require enhanced resource token management
- **Advanced Analytics**: Beyond Sentry monitoring, may need custom telemetry for business intelligence on sync patterns
- **Compliance Needs**: Regulatory requirements might require API-based audit trails

## Changelog

| Date | Change | Author |
|------|--------|--------|
| 2025-05-27 | Initial ADR created | Development Team |
| | | |
| | | |

---

**ADR Template Version:** 1.0  
**Previous ADR:** [ADR-001: Architecture Decision Record Template](./ADR-001-Architecture-Decision-Record.md)  
**Next ADR:** ADR-003-[Next Decision]
