namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Options for file operations
    /// </summary>
    public class FileOperationOptions
    {
        public int MaxParallelOperations { get; set; } = NetworkFileManager.DefaultMaxParallelOps;
        public bool Overwrite { get; set; } = true;
        public bool PreserveDirectoryStructure { get; set; } = true;
        public string SourceBaseDir { get; set; }
        public bool DeleteEmptySourceDirectories { get; set; } = false;
        public bool PreCreateDirectories { get; set; } = true;
        public int MaxRetries { get; set; } = NetworkFileManager.DefaultMaxRetries;
        public int RetryDelayMilliseconds { get; set; } = NetworkFileManager.DefaultRetryDelayMs;
        public bool TestModeEnabled { get; set; } = false;
    }
}
