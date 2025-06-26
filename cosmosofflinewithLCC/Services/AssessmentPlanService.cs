using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Services
{
    public class AssessmentPlanService
    {
        private readonly SyncEngine<AssessmentPlan> _syncEngine;
        private readonly IDocumentStore<AssessmentPlan> _localStore;
        private readonly CosmosDbStore<AssessmentPlan> _remoteStore;
        private readonly ILogger<AssessmentPlanService> _logger;
        private readonly string _currentUserId;

        public AssessmentPlanService(
            SyncEngine<AssessmentPlan> syncEngine,
            IDocumentStore<AssessmentPlan> localStore,
            CosmosDbStore<AssessmentPlan> remoteStore,
            ILogger<AssessmentPlanService> logger)
        {
            _syncEngine = syncEngine;
            _localStore = localStore;
            _remoteStore = remoteStore;
            _logger = logger;
            _currentUserId = Environment.GetEnvironmentVariable("CURRENT_USER_ID") ?? "user1"; // Or get from auth service
        }

        public async Task InitialDataPullAsync()
        {
            _logger.LogInformation("Performing initial data pull for AssessmentPlans for user {UserId}...", _currentUserId);
            await _syncEngine.InitialUserDataPullAsync("AssessmentPlan");
            _logger.LogInformation("Initial data pull for AssessmentPlans completed for user {UserId}.", _currentUserId);
        }

        public async Task AddOrUpdateLocalAssessmentPlanAsync(AssessmentPlan plan)
        {
            plan.OIID = _currentUserId; // Ensure OIID is set
            plan.Type = "AssessmentPlan"; // Ensure Type is set
            await _localStore.UpsertAsync(plan);
            _logger.LogInformation("Local AssessmentPlan {PlanId} upserted for user {UserId}.", plan.ID, _currentUserId);
        }

        public async Task AddOrUpdateRemoteAssessmentPlanAsync(AssessmentPlan plan)
        {
            plan.OIID = _currentUserId; // Ensure OIID is set
            plan.Type = "AssessmentPlan"; // Ensure Type is set
            await _remoteStore.UpsertAsync(plan);
            _logger.LogInformation("Remote AssessmentPlan {PlanId} upserted for user {UserId}.", plan.ID, _currentUserId);
        }

        public async Task<AssessmentPlan?> GetLocalAssessmentPlanAsync(string id)
        {
            return await _localStore.GetAsync(id, _currentUserId);
        }

        public async Task<AssessmentPlan?> GetRemoteAssessmentPlanAsync(string id)
        {
            return await _remoteStore.GetAsync(id, _currentUserId);
        }

        public async Task<List<AssessmentPlan>> GetAllLocalAssessmentPlansAsync()
        {
            return await _localStore.GetByUserIdAsync(_currentUserId);
        }

        public async Task<List<AssessmentPlan>> GetAllRemoteAssessmentPlansAsync()
        {
            return await _remoteStore.GetByUserIdAsync(_currentUserId);
        }

        public async Task SyncAssessmentPlansAsync()
        {
            _logger.LogInformation("Starting sync for AssessmentPlans for user {UserId}...", _currentUserId);
            await _syncEngine.SyncAsync();
            _logger.LogInformation("Sync for AssessmentPlans completed for user {UserId}.", _currentUserId);
        }

        public async Task<bool> IsLocalStoreEmptyAsync()
        {
            var plans = await _localStore.GetAllAsync();
            return plans.Count == 0;
        }

        public async Task DeleteLocalAssessmentPlanAsync(AssessmentPlan plan)
        {
            plan.IsDeleted = true;
            await AddOrUpdateLocalAssessmentPlanAsync(plan);
            _logger.LogInformation("Local AssessmentPlan {PlanId} marked as deleted for user {UserId}.", plan.ID, _currentUserId);
        }

        public async Task DeleteRemoteAssessmentPlanAsync(AssessmentPlan plan)
        {
            plan.IsDeleted = true;
            await AddOrUpdateRemoteAssessmentPlanAsync(plan);
            _logger.LogInformation("Remote AssessmentPlan {PlanId} marked as deleted for user {UserId}.", plan.ID, _currentUserId);
        }
    }
}
