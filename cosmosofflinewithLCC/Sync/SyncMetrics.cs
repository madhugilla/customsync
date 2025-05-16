using System;
using System.Collections.Generic;

namespace cosmosofflinewithLCC.Sync
{
    /// <summary>
    /// Provides detailed metrics for synchronization operations
    /// </summary>
    public class SyncMetrics
    {
        /// <summary>
        /// The time when the sync started
        /// </summary>
        public DateTime SyncStartTime { get; private set; }

        /// <summary>
        /// The time when the sync ended
        /// </summary>
        public DateTime? SyncEndTime { get; private set; }

        /// <summary>
        /// The total duration of the sync operation
        /// </summary>
        public TimeSpan? TotalDuration => SyncEndTime.HasValue ? SyncEndTime - SyncStartTime : null;

        /// <summary>
        /// Number of items pushed to the remote store
        /// </summary>
        public int ItemsPushed { get; private set; }

        /// <summary>
        /// Number of items pulled from the remote store
        /// </summary>
        public int ItemsPulled { get; private set; }

        /// <summary>
        /// Number of items skipped (unchanged)
        /// </summary>
        public int ItemsSkipped { get; private set; }

        /// <summary>
        /// Indicates whether the sync completed successfully
        /// </summary>
        public bool IsSuccess { get; private set; }

        /// <summary>
        /// The user ID for which the sync was performed
        /// </summary>
        public string UserId { get; private set; }

        /// <summary>
        /// The document type that was synced
        /// </summary>
        public string DocType { get; private set; }

        /// <summary>
        /// Any errors that occurred during synchronization
        /// </summary>
        public Exception? Error { get; private set; }

        /// <summary>
        /// Detailed timing information for each phase of synchronization
        /// </summary>
        public Dictionary<string, TimeSpan> PhaseTiming { get; private set; } = new Dictionary<string, TimeSpan>();

        /// <summary>
        /// Creates a new instance of SyncMetrics
        /// </summary>
        /// <param name="userId">The user ID for which the sync was performed</param>
        /// <param name="docType">The document type that was synced</param>
        public SyncMetrics(string userId, string docType)
        {
            SyncStartTime = DateTime.UtcNow;
            UserId = userId;
            DocType = docType;
            IsSuccess = false;
        }

        /// <summary>
        /// Records the time taken for a specific phase of synchronization
        /// </summary>
        /// <param name="phaseName">The name of the phase</param>
        /// <param name="duration">The duration of the phase</param>
        public void RecordPhaseTime(string phaseName, TimeSpan duration)
        {
            PhaseTiming[phaseName] = duration;
        }

        /// <summary>
        /// Records push metrics
        /// </summary>
        /// <param name="itemsPushed">Number of items pushed</param>
        /// <param name="itemsSkipped">Number of items skipped</param>
        public void RecordPushMetrics(int itemsPushed, int itemsSkipped = 0)
        {
            ItemsPushed = itemsPushed;
            ItemsSkipped += itemsSkipped;
        }

        /// <summary>
        /// Records pull metrics
        /// </summary>
        /// <param name="itemsPulled">Number of items pulled</param>
        /// <param name="itemsSkipped">Number of items skipped</param>
        public void RecordPullMetrics(int itemsPulled, int itemsSkipped = 0)
        {
            ItemsPulled = itemsPulled;
            ItemsSkipped += itemsSkipped;
        }

        /// <summary>
        /// Records an error that occurred during synchronization
        /// </summary>
        /// <param name="ex">The exception that was thrown</param>
        public void RecordError(Exception ex)
        {
            Error = ex;
            IsSuccess = false;
            SyncEndTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the synchronization as completed successfully
        /// </summary>
        public void MarkAsCompleted()
        {
            IsSuccess = true;
            SyncEndTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets a string representation of the sync metrics
        /// </summary>
        /// <returns>A string containing the sync metrics</returns>
        public override string ToString()
        {
            var status = IsSuccess ? "Succeeded" : "Failed";
            var duration = TotalDuration.HasValue ? $"{TotalDuration.Value.TotalMilliseconds:F2} ms" : "In Progress";

            return $"Sync ({status}) - User: {UserId}, Type: {DocType}, Duration: {duration}, " +
                   $"Items Pushed: {ItemsPushed}, Items Pulled: {ItemsPulled}, Items Skipped: {ItemsSkipped}";
        }
    }
}
