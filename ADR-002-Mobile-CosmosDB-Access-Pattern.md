# ADR-002: Mobile App Cosmos DB Access Pattern

**Date:** 2025-05-27

**Status:** Accepted

**Deciders:** Development Team, Solution Architect

**Technical Story:** Mobile app needs to synchronize data with Cosmos DB for offline-first functionality. **The app performs only data synchronization operations with no business logic beyond sync functionality.** Decision required on whether to access Cosmos DB directly or through an intermediary API layer.

## Context and Problem Statement

The mobile application requires data synchronization with Azure Cosmos DB to support offline-first functionality. **The app is specifically designed to handle only synchronization operations - no business logic, data validation, or complex processing beyond basic CRUD sync operations.** We need to decide whether the mobile app should connect directly to Cosmos DB or access data through an intermediary API layer. This decision impacts security, performance, maintainability, and scalability of the solution.

### Forces

- **Security**: Direct database access from mobile apps exposes connection strings and increases attack surface
- **Performance**: Direct access eliminates network hops but API layer enables caching and optimization
- **Offline Capability**: Mobile apps need to work offline and sync when connectivity returns
- **Development Complexity**: Direct access is simpler initially but API layer provides more flexibility
- **Data Governance**: API layer enables better control over data access patterns and validation
- **Sync-Only Focus**: System performs only data synchronization without additional business processing
- **Runtime Security**: Endpoint details and credentials should be obtained at runtime, not embedded in app

## Decision Drivers

- **Security Requirements**: Minimize exposure of database credentials and implement proper authentication
- **Offline-First Architecture**: Mobile app must function without network connectivity
- **Performance**: Minimize latency for data operations while maintaining security
- **Scalability**: Solution must support multiple mobile clients efficiently
- **Development Speed**: Quick implementation to validate offline synchronization concepts
- **Resource Token Implementation**: Leverage Cosmos DB resource tokens for secure, scoped access

## Considered Options

### Option 1: Direct Cosmos DB Access with Resource Tokens

**Description:** Mobile app connects directly to Cosmos DB using resource tokens obtained from an Azure Function. **Cosmos DB provides a native HTTP REST API that can be accessed directly from any HTTP client, making it well-suited for direct mobile app integration.** App implements local SQLite store for offline functionality and syncs directly with Cosmos DB when online. **The app performs only synchronization operations - no business logic or data processing beyond basic CRUD sync.**

**Pros:**

- **Reduced Latency**: Eliminates API layer network hop
- **Simplified Architecture**: Fewer components to deploy and maintain
- **Resource Token Security**: Leverages Cosmos DB's built-in security with time-limited, scoped tokens
- **Runtime Endpoint Discovery**: Cosmos DB endpoint details are downloaded only after successful authentication, not embedded in the app
- **Offline-First Design**: Local SQLite enables full offline functionality
- **Direct SDK Benefits**: Full access to Cosmos DB SDK features (batch operations, query optimization)
- **Native HTTP API**: Cosmos DB's built-in HTTP REST API enables direct communication without intermediary layers
- **Cost Effective**: No additional API hosting costs
- **Development Speed**: Faster initial implementation and prototyping
- **Perfect Sync Match**: Direct access ideal for sync-only operations without business logic

**Cons:**

- **Limited Business Logic**: No centralized place for complex business rules (not needed for sync-only system)
- **Client Complexity**: Mobile app handles all data logic and synchronization
- **Harder to Evolve**: Schema changes require mobile app updates
- **Limited Caching**: No server-side caching opportunities
- **Monitoring Challenges**: More difficult to monitor and log data access patterns

### Option 2: API Layer with Database Access

**Description:** Mobile app connects to a custom Web API that handles all Cosmos DB interactions. API provides RESTful endpoints for data operations and implements business logic server-side. **For a sync-only system, this adds unnecessary complexity.**

**Pros:**

- **Centralized Logic**: Business rules and validation in one place (not needed for sync-only operations)
- **Better Security**: Database credentials never exposed to mobile clients
- **Easier Evolution**: API versioning allows schema changes without mobile updates
- **Server-Side Caching**: Can implement caching strategies to improve performance
- **Better Monitoring**: Centralized logging and analytics for data access
- **Data Transformation**: Can modify data format without client changes
- **Rate Limiting**: Built-in protection against abuse

**Cons:**

- **Increased Latency**: Additional network hop for every operation
- **More Complexity**: Additional deployment, hosting, and maintenance overhead
- **Single Point of Failure**: API becomes critical dependency for mobile app
- **Development Overhead**: More components to build, test, and deploy
- **Higher Costs**: Additional hosting and compute costs for API layer
- **Offline Challenges**: Still need local storage and sync logic in mobile app
- **Over-Engineering**: Unnecessary complexity for pure synchronization operations

## Decision Outcome

**Chosen option:** "Option 1: Direct Cosmos DB Access with Resource Tokens", because it best aligns with our current requirements and constraints.

### Justification

1. **Offline-First Priority**: Direct access with local SQLite perfectly supports offline-first architecture, while Option 2 still requires complex offline logic plus API dependency management

2. **Security Equivalence with Better Performance**: Resource tokens provide the same enterprise-grade security as Option 2's API layer, but without the performance penalty of additional network hops

3. **Reduced Attack Surface**: Contrary to traditional concerns, our runtime endpoint discovery approach means no database credentials are embedded in the app, matching Option 2's security while eliminating the API layer as an additional attack vector

4. **Performance Advantage**: Eliminates 50% of network calls compared to Option 2 (direct to Cosmos vs. mobile→API→Cosmos), crucial for mobile environments with limited bandwidth

5. **Cost Effectiveness**: Zero additional hosting costs vs. Option 2's requirement for API hosting, load balancing, and scaling infrastructure

6. **Simplified Deployment Pipeline**: Single-component deployment vs. Option 2's multi-service coordination, reducing deployment complexity and potential failure points

7. **Native Database Capabilities**: Full access to Cosmos DB SDK optimizations (batch operations, query optimization, change feed) vs. Option 2's limited API endpoints

8. **Purpose-Built Architecture**: Cosmos DB's HTTP REST API is specifically designed for direct client access, making Option 2's custom API layer redundant infrastructure

9. **Runtime Security Model**: Endpoint discovery after authentication provides the same credential protection as Option 2 without the architectural overhead

10. **Sync-Optimized Design**: For pure synchronization operations, direct database access eliminates unnecessary abstraction layers that add complexity without functional benefit

11. **Development Velocity**: Single codebase focus vs. Option 2's requirement to build, test, and maintain both mobile app AND API layer simultaneously

12. **Operational Simplicity**: One service to monitor and maintain vs. Option 2's multiple services, reducing operational overhead and potential points of failure

### Positive Consequences

- **Fast Development**: Rapid prototyping and implementation of offline sync functionality without the overhead of building parallel API infrastructure
- **Optimal Performance**: Direct database access provides best possible latency for sync operations, eliminating the 50% performance penalty of Option 2's additional network hop
- **Strong Security**: Resource tokens provide time-limited, scoped access without credential exposure, matching Option 2's security without the complexity
- **True Offline Capability**: Local SQLite ensures app works completely offline, while Option 2 would still require the same offline infrastructure plus API dependency management
- **Lower Infrastructure Costs**: No additional API hosting requirements, saving hosting, scaling, and maintenance costs compared to Option 2
- **Simplified Deployment**: Fewer components to deploy and maintain, reducing deployment complexity and potential points of failure compared to Option 2's multi-service architecture
- **Native SDK Benefits**: Full access to Cosmos DB's optimized batch operations, change feed, and query capabilities that would be limited or unavailable through Option 2's custom API endpoints

### Negative Consequences

- **Limited Business Logic Centralization**: Business rules must be implemented in mobile app (not applicable for sync-only system)
- **Client-Side Complexity**: Mobile app responsible for all data handling logic (appropriate for sync-only operations)
- **Harder Schema Evolution**: Database schema changes require mobile app updates (vs. Option 2's API versioning)
- **Monitoring Challenges**: More difficult to implement centralized logging and analytics compared to Option 2's API layer
- **No Server-Side Caching**: Cannot implement server-side performance optimizations that Option 2's API layer could provide

## Implementation Plan

### Immediate Actions

1. **Implement Resource Token Provider** (Week 1)
   - Create Azure Function for token generation
   - Implement `ICosmosTokenProvider` interface with HTTP-based token retrieval

2. **Direct Cosmos Client Integration** (Week 1-2)
   - Implement `CosmosClientFactory` with resource token authentication
   - Create `CosmosDbStore` implementing `IDocumentStore` interface

3. **Offline Sync Engine** (Week 2-3)
   - Implement bidirectional sync between SQLite and Cosmos DB
   - Add Last-Write-Wins conflict resolution strategy
   - Implement pending changes tracking

### Dependencies

- **Azure Function Deployment**: Token generation service must be deployed first
- **Cosmos DB Setup**: Database and container configuration completed
- **SQLite Integration**: Local storage implementation for offline functionality
- **Resource Token Configuration**: Proper permissions and expiry settings

### Success Metrics

- **Sync Performance**: Full sync completes in < 5 seconds for typical dataset
- **Offline Functionality**: App operates fully offline for extended periods
- **Security Validation**: No master keys or long-lived credentials stored on device
- **Token Refresh Success**: 99.9% success rate for token generation and refresh

## Validation and Review

### Validation Plan

- **Performance Testing**: Measure sync times with varying data volumes
- **Security Audit**: Verify no sensitive credentials exposed in mobile app
- **Offline Testing**: Validate app functionality during extended offline periods
- **Load Testing**: Test token generation under concurrent user load

### Review Date

**Next Review:** 2025-08-27 (3 months after implementation)

**Review Triggers:**

- **Performance Issues**: Sync times exceed acceptable thresholds
- **Security Concerns**: Any security vulnerabilities identified
- **Scale Requirements**: Need to support significantly more users
- **Business Logic Complexity**: Requirements for centralized business rules emerge (unlikely for sync-only system)

## Links and References

- [ADR-001: Architecture Decision Record Template](./ADR-001-Architecture-Decision-Record.md)
- [Cosmos DB Resource Tokens Documentation](https://docs.microsoft.com/en-us/azure/cosmos-db/secure-access-to-data)
- [README.md: Resource Token Implementation](./README.md#cosmos-db-resource-tokens)
- [Azure Function Token Generator](./RemotComsosTokenGenerator/)
- [Cosmos Client Factory Implementation](./cosmosofflinewithLCC/Data/CosmosClientFactory.cs)

---

## Notes

### Key Assumptions

- **Single User Model**: Current implementation supports single user for simplicity
- **Development Priority**: Speed of development prioritized over architectural flexibility
- **Resource Token Reliability**: Azure Functions provide sufficient reliability for token generation
- **Network Patterns**: Mobile app has intermittent connectivity requiring robust offline support
- **Sync-Only Operations**: System performs only data synchronization with no business logic or processing

### Future Considerations

- **Multi-User Support**: Will require enhanced resource token management
- **Business Logic Growth**: Complex business rules may necessitate API layer (unlikely for sync-only system)
- **Monitoring Requirements**: May need to implement custom telemetry solution
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
