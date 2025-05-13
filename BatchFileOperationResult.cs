using System.Collections.Generic;
using System.Threading;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Result of a batch file operation
    /// </summary>
    public class BatchFileOperationResult
    {
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> FailedFiles { get; set; } = new List<string>();
        public bool WasCancelled { get; set; }

        // Thread-safe increment fields
        private int _tempSuccessCount;
        private int _tempFailedCount;
        private int _tempSkippedCount;

        public void IncrementSuccessCount()
        {
            int newValue = Interlocked.Increment(ref _tempSuccessCount);
            SuccessCount = newValue;
        }

        public void IncrementFailedCount()
        {
            int newValue = Interlocked.Increment(ref _tempFailedCount);
            FailedCount = newValue;
        }

        public void IncrementSkippedCount()
        {
            int newValue = Interlocked.Increment(ref _tempSkippedCount);
            SkippedCount = newValue;
        }
    }
}
