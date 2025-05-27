# AD Summary Report – Decisions and Implications

<a name="sodaerror0"></a>**Summary of Architectural Decisions** 

27/05/2025

# <a name="sodaerror47"></a>**Architectural Decision (AD) – Mobile App Cosmos DB Access Pattern**

## ***Status:*** 
Accepted

## ***Context:*** 
We needed to decide how our mobile application should access Azure Cosmos DB for offline-first data synchronization. The mobile app requires robust online/offline capabilities using a local-first architecture with Last Write Wins conflict resolution. The app is specifically designed to handle only synchronization operations - pure data sync with no business logic, schema evolution, caching, or complex processing beyond basic CRUD sync operations.

Two primary approaches were considered:
1. **Direct Cosmos DB Access with Resource Tokens** - Mobile app connects directly to Cosmos DB using secure resource tokens
2. **API Layer with Database Access** - Mobile app connects through a custom Web API that handles all Cosmos DB interactions

## ***Decision***
After thorough analysis and evaluation, we have decided to proceed with **Direct Cosmos DB Access with Resource Tokens** for our mobile application's data synchronization needs. The mobile app will connect directly to Azure Cosmos DB using resource tokens obtained from an Azure Function, implementing local SQLite storage for offline functionality with Last Write Wins conflict resolution using last modified timestamps.

## ***Rationale***
The primary reason for this decision is the optimal alignment with our local-first, offline-focused architecture requirements while maintaining enterprise-grade security and performance.

**Key advantages that drove this decision include:**

**Local-First Performance**: Direct access with local SQLite provides immediate user responses for all operations, while an API layer would require network calls for each operation, degrading user experience.

**Last Write Wins Optimization**: Direct access to Cosmos DB timestamps enables efficient conflict resolution using last modified dates without server-side processing complexity.

**Offline-First Priority**: Direct access with local SQLite perfectly supports offline-first architecture, while an API layer would still require complex offline logic plus additional API dependency management.

**Security Equivalence with Better Performance**: Resource tokens provide the same enterprise-grade security as an API layer, but without the performance penalty of additional network hops.

**Cost Effectiveness**: Zero additional hosting costs compared to API hosting, load balancing, and scaling infrastructure requirements.

**Development Velocity**: Single codebase focus versus requirement to build, test, and maintain both mobile app AND API layer simultaneously.

**Native Database Capabilities**: Full access to Cosmos DB SDK optimizations (batch operations, query optimization, change feed) versus limited API endpoints.

**Operational Simplicity**: One service to monitor and maintain versus multiple services, reducing operational overhead and potential points of failure.

## ***Implications***

**Positive Implications:**
- **Fast Development**: Rapid prototyping and implementation of offline sync functionality without parallel API infrastructure overhead
- **Optimal Performance**: Best possible latency for sync operations, eliminating 50% performance penalty of additional network hops
- **Strong Security**: Resource tokens provide time-limited, scoped access without credential exposure
- **True Offline Capability**: Local SQLite ensures app works completely offline with seamless sync when connectivity returns
- **Lower Infrastructure Costs**: No additional API hosting requirements, saving hosting, scaling, and maintenance costs

**Challenges to Manage:**
- **Client-Side Complexity**: Mobile app responsible for all sync logic and Last Write Wins conflict resolution
- **Distributed Monitoring**: Sync patterns and errors monitored on individual clients rather than centralized (mitigated by Sentry integration for error tracking and performance monitoring)

## ***Future Considerations***
- **Multi-User Support**: Will require enhanced resource token management when scaling beyond single-user model
- **Advanced Analytics**: Beyond Sentry monitoring, may need custom telemetry for business intelligence on sync patterns

---

**Document Version:** 1.0  
**Last Updated:** 27/05/2025  
**Next Review:** 27/08/2025

---

# <a name="sodaerror48"></a>**Architectural Decision (AD) – API Layer Approach**

## ***Status:*** 
Rejected

## ***Context:*** 
As part of our mobile app data access architecture evaluation, we considered implementing an API Layer approach where the mobile application would connect to a custom Web API that handles all Cosmos DB interactions. This approach would provide RESTful endpoints for sync operations and handle synchronization logic and conflict resolution server-side. The API layer would act as an intermediary between the mobile app and Azure Cosmos DB, centralizing data access logic and potentially providing better monitoring and security isolation.

## ***Decision***
After thorough analysis and comparison with direct Cosmos DB access, we have decided to **reject the API Layer approach** for our mobile application's data synchronization needs. The API layer would introduce unnecessary complexity and latency for a pure sync-only system with simple Last Write Wins resolution.

## ***Rationale***
The primary reason for rejecting this approach is that it introduces unnecessary overhead and complexity that doesn't align with our local-first, offline-focused architecture requirements.

**Key disadvantages that led to this rejection:**

**Increased Latency**: Additional network hop for every sync operation, requiring mobile→API→Cosmos routing instead of direct mobile→Cosmos communication.

**More Complexity**: Additional deployment, hosting, and maintenance overhead for API infrastructure that doesn't add functional value for sync-only operations.

**Single Point of Failure**: API becomes critical dependency for mobile app functionality, introducing additional failure points.

**Development Overhead**: Requires building, testing, and maintaining both mobile app AND API layer simultaneously, doubling development effort.

**Higher Costs**: Additional hosting and compute costs for API layer, load balancing, and scaling infrastructure.

**Offline Challenges**: Still requires local storage and sync logic in mobile app, making the API layer redundant for offline scenarios.

**Over-Engineering**: Unnecessary complexity for pure synchronization operations that don't require business logic processing.

**Local-First Impedance**: Would require API calls for each operation, degrading user experience compared to immediate local responses.

## ***Implications***

**Avoided Negative Implications:**
- **Performance Penalty**: Eliminated 50% additional network calls that would degrade sync performance
- **Infrastructure Costs**: Avoided hosting, scaling, and maintenance costs for unnecessary API infrastructure
- **Development Complexity**: Avoided need to build and maintain parallel API codebase alongside mobile app
- **Deployment Overhead**: Avoided multi-service coordination and potential deployment failure points
- **Operational Burden**: Avoided monitoring and maintaining multiple services for simple sync operations

**Decision Reinforcement:**
- **Validates Direct Access**: Rejection of API layer strengthens the rationale for direct Cosmos DB access
- **Confirms Local-First Priority**: Reinforces commitment to immediate local operations with background sync
- **Supports Simplicity**: Aligns with development speed priority and sync-only operational focus

## ***Future Considerations***
- **Multi-Tenant Scenarios**: Complex multi-user requirements might justify API layer overhead in future iterations
- **Business Logic Evolution**: If the app scope expands beyond pure sync operations, API layer benefits might outweigh costs

---
