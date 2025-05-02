using RunLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigManager;
using System.ComponentModel;

namespace AaTurpin.SnapshotManager
{
    public static class SnapshotManager
    {
        private static Logger _logger = Log.Logger;
        private static readonly TimeSpan _validationInterval = TimeSpan.FromMinutes(15);
        private static readonly ConcurrentDictionary<string, DateTime> _pathValidationTimes = new ConcurrentDictionary<string, DateTime>();
        // Network operation timeout settings
        private static TimeSpan _networkReadTimeout = TimeSpan.FromSeconds(30);
        private static TimeSpan _networkWriteTimeout = TimeSpan.FromSeconds(60);
        private static TimeSpan _networkConnectTimeout = TimeSpan.FromSeconds(15);
        // Snapshot history tracking
        private static readonly ConcurrentDictionary<string, int> _previousSuccessfulSnapshotCounts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, DirectorySnapshot> _lastSuccessfulSnapshots = new ConcurrentDictionary<string, DirectorySnapshot>();
        private static readonly int _maxSnapshotHistory = 5;
        private static readonly ConcurrentDictionary<string, Queue<DirectorySnapshot>> _snapshotHistory = new ConcurrentDictionary<string, Queue<DirectorySnapshot>>();

        /// <summary>
        /// Sets a custom logger for the SnapshotManager.
        /// </summary>
        /// <param name="logger">The logger instance to use.</param>
        public static void SetLogger(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.Information("SnapshotManager logger has been configured.");
        }

        /// <summary>
        /// Helper class for recovering from network issues during snapshot operations
        /// </summary>
        public static class SnapshotRecovery
        {
            private static Logger _logger = Log.Logger;

            /// <summary>
            /// Sets a custom logger for the SnapshotRecovery.
            /// </summary>
            /// <param name="logger">The logger instance to use.</param>
            public static void SetLogger(Logger logger)
            {
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
                _logger.Information("SnapshotRecovery logger has been configured.");
            }

            /// <summary>
            /// Attempts to recover a snapshot by retrying failed paths
            /// </summary>
            /// <param name="snapshot">The incomplete snapshot to recover</param>
            /// <param name="maxParallelOperations">Maximum number of parallel operations</param>
            /// <param name="token">Cancellation token</param>
            /// <returns>A potentially more complete snapshot</returns>
            public static async Task<DirectorySnapshot> RecoverSnapshotAsync(
                DirectorySnapshot snapshot,
                int maxParallelOperations = 4,
                CancellationToken token = default)
            {
                if (snapshot == null || snapshot.IsComplete || snapshot.FailedPaths.Count == 0)
                {
                    return snapshot;
                }

                _logger.Information("Attempting to recover snapshot for {RootPath} by retrying {Count} failed paths",
                    snapshot.RootPath, snapshot.FailedPaths.Count);

                // Create a new snapshot with existing data
                var recoveredSnapshot = new DirectorySnapshot
                {
                    RootPath = snapshot.RootPath,
                    IsComplete = true
                };

                // Copy existing files
                foreach (var file in snapshot.Files.Values)
                {
                    recoveredSnapshot.Files[file.RelativePath] = file;
                }

                // Group failed paths by common parent to avoid redundant scans
                var pathsToRetry = OptimizeFailedPaths(snapshot.FailedPaths);

                _logger.Debug("Optimized {OriginalCount} failed paths down to {OptimizedCount} paths to retry",
                    snapshot.FailedPaths.Count, pathsToRetry.Count);

                // Process each failed path
                foreach (var path in pathsToRetry)
                {
                    if (token.IsCancellationRequested)
                        break;

                    try
                    {
                        _logger.Debug("Retrying path: {Path}", path);

                        if (!Directory.Exists(path))
                        {
                            _logger.Warning("Path no longer exists: {Path}", path);
                            recoveredSnapshot.FailedPaths.Add(path);
                            recoveredSnapshot.IsComplete = false;
                            continue;
                        }

                        // Create a mini-snapshot just for this path
                        var partialSnapshot = await ScanSinglePathAsync(path, snapshot.RootPath, maxParallelOperations, token);

                        // If partial scan succeeded
                        if (partialSnapshot.IsComplete)
                        {
                            // Add files to recovered snapshot
                            foreach (var file in partialSnapshot.Files.Values)
                            {
                                recoveredSnapshot.Files[file.RelativePath] = file;
                            }
                        }
                        else
                        {
                            // Add failed paths to recovered snapshot
                            recoveredSnapshot.FailedPaths.AddRange(partialSnapshot.FailedPaths);
                            recoveredSnapshot.IsComplete = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error retrying path: {Path}", path);
                        recoveredSnapshot.FailedPaths.Add(path);
                        recoveredSnapshot.IsComplete = false;
                    }
                }

                _logger.Information("Recovery attempt complete. Original: {OriginalCount} files, Recovered: {RecoveredCount} files, Complete: {IsComplete}",
                    snapshot.Files.Count, recoveredSnapshot.Files.Count, recoveredSnapshot.IsComplete);

                return recoveredSnapshot;
            }

            /// <summary>
            /// Optimizes the list of failed paths by removing redundant child paths
            /// </summary>
            /// <param name="failedPaths">List of paths that failed during snapshot</param>
            /// <returns>Optimized list with redundant paths removed</returns>
            private static List<string> OptimizeFailedPaths(List<string> failedPaths)
            {
                if (failedPaths == null || failedPaths.Count <= 1)
                    return new List<string>(failedPaths ?? new List<string>());

                var sortedPaths = failedPaths
                    .OrderBy(p => p.Length)
                    .ToList();

                var optimizedPaths = new List<string>();
                var coveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in sortedPaths)
                {
                    // Skip if this path is already covered by a parent
                    if (coveredPaths.Contains(path))
                        continue;

                    // Add this path
                    optimizedPaths.Add(path);

                    // Mark all child paths as covered
                    foreach (var otherPath in sortedPaths)
                    {
                        if (otherPath != path && otherPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                        {
                            coveredPaths.Add(otherPath);
                        }
                    }
                }

                return optimizedPaths;
            }

            /// <summary>
            /// Scans a single path for files
            /// </summary>
            /// <param name="path">The path to scan</param>
            /// <param name="rootPath">The root path for the snapshot</param>
            /// <param name="maxParallelOperations">Maximum number of parallel operations</param>
            /// <param name="token">Cancellation token</param>
            /// <returns>A snapshot for the specified path</returns>
            private static async Task<DirectorySnapshot> ScanSinglePathAsync(
                string path,
                string rootPath,
                int maxParallelOperations,
                CancellationToken token)
            {
                var snapshot = new DirectorySnapshot { RootPath = rootPath };

                try
                {
                    // Get all files in the path
                    var filesList = new ConcurrentBag<string>();
                    var failedPaths = new ConcurrentBag<string>();

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(path))
                            {
                                // Use a simplified version of EnumerateAllFiles
                                EnumeratePathFiles(path, filesList, failedPaths, null, token);
                            }
                            else
                            {
                                failedPaths.Add(path);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error enumerating files in path: {Path}", path);
                            failedPaths.Add(path);
                        }
                    }, token);

                    // Update failure status
                    foreach (var failedPath in failedPaths)
                    {
                        snapshot.FailedPaths.Add(failedPath);
                    }

                    if (failedPaths.Count > 0)
                    {
                        snapshot.IsComplete = false;
                    }

                    // Process files in parallel
                    using (var semaphore = new SemaphoreSlim(maxParallelOperations))
                    {
                        var tasks = new List<Task>();

                        foreach (string filePath in filesList)
                        {
                            if (token.IsCancellationRequested)
                                break;

                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    await semaphore.WaitAsync(token);

                                    try
                                    {
                                        // Create file snapshot with error handling
                                        try
                                        {
                                            if (File.Exists(filePath))
                                            {
                                                var fileInfo = new FileInfo(filePath);

                                                // Calculate relative path from root
                                                string relativePath;
                                                if (filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    relativePath = filePath.Substring(rootPath.Length).TrimStart('\\', '/');
                                                }
                                                else
                                                {
                                                    // Get path relative to drive root
                                                    string driveRoot = Path.GetPathRoot(filePath);
                                                    relativePath = filePath.Substring(driveRoot.Length);
                                                }

                                                var fileSnapshot = new FileSnapshot
                                                {
                                                    MappedPath = filePath,
                                                    FileSize = fileInfo.Length,
                                                    LastWriteTime = fileInfo.LastWriteTime,
                                                    Attributes = fileInfo.Attributes,
                                                    SnapshotTime = DateTime.Now
                                                };

                                                lock (snapshot.Files)
                                                {
                                                    snapshot.Files[relativePath] = fileSnapshot;
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Warning(ex, "Error creating file snapshot: {FilePath}", filePath);
                                        }
                                    }
                                    finally
                                    {
                                        semaphore.Release();
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    // Expected when cancellation requested
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error(ex, "Unhandled error processing file: {FilePath}", filePath);
                                }
                            }, token));
                        }

                        await Task.WhenAll(tasks);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error scanning path: {Path}", path);
                    snapshot.FailedPaths.Add(path);
                    snapshot.IsComplete = false;
                }

                return snapshot;
            }

            /// <summary>
            /// Enumerates files in a directory without recursive calls
            /// </summary>
            private static void EnumeratePathFiles(
                string directoryPath,
                ConcurrentBag<string> filesList,
                ConcurrentBag<string> failedPaths,
                List<string> excludedPaths,
                CancellationToken token)
            {
                var directories = new Queue<string>();
                directories.Enqueue(directoryPath);

                while (directories.Count > 0 && !token.IsCancellationRequested)
                {
                    string currentDir = directories.Dequeue();

                    try
                    {
                        // Skip excluded paths
                        if (excludedPaths != null && excludedPaths.Any(
                            e => currentDir.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        // Get files in current directory
                        foreach (var file in Directory.GetFiles(currentDir))
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Skip excluded files
                            if (excludedPaths != null && excludedPaths.Any(
                                e => file.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            filesList.Add(file);
                        }

                        // Add subdirectories to queue
                        foreach (var subDir in Directory.GetDirectories(currentDir))
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Skip excluded directories
                            if (excludedPaths != null && excludedPaths.Any(
                                e => subDir.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            directories.Enqueue(subDir);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.Warning(ex, "Access denied to directory: {Directory}", currentDir);
                        failedPaths.Add(currentDir);
                    }
                    catch (IOException ex)
                    {
                        _logger.Warning(ex, "IO error in directory: {Directory} - {Error}",
                            currentDir, ex.Message);
                        failedPaths.Add(currentDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Error accessing directory: {Directory}", currentDir);
                        failedPaths.Add(currentDir);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a snapshot of the specified directories
        /// </summary>
        /// <param name="directories">List of directories with their exclusions</param>
        /// <param name="maxParallelOperations">Maximum number of parallel operations</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>A DirectorySnapshot containing file information</returns>
        public static async Task<DirectorySnapshot> CreateSnapshotAsync(
            List<DirectoryConfig> directories,
            int maxParallelOperations = 8,
            CancellationToken token = default)
        {
            _logger.Information("Starting snapshot creation with max {MaxParallel} parallel operations", maxParallelOperations);
            var snapshot = new DirectorySnapshot();

            if (directories == null || directories.Count == 0)
            {
                _logger.Warning("No directories provided for snapshot");
                return snapshot;
            }

            // Process each directory sequentially to avoid network resource issues
            foreach (var dir in directories)
            {
                if (token.IsCancellationRequested)
                    break;

                string directoryPath = dir.Path;

                // Validate path before processing
                if (!await ValidatePathAsync(directoryPath, token))
                {
                    _logger.Warning("Skipping invalid or inaccessible directory: {Directory}", directoryPath);
                    snapshot.FailedPaths.Add(directoryPath);
                    snapshot.IsComplete = false;
                    continue;
                }

                try
                {
                    _logger.Debug("Creating snapshot for directory: {Directory}", directoryPath);

                    // Create a snapshot for the directory
                    DirectorySnapshot dirSnapshot = await ProcessDirectoryAsync(
                        directoryPath,
                        dir.Exclusions,
                        maxParallelOperations,
                        token);

                    // If directory snapshot is incomplete, mark the entire snapshot as incomplete
                    if (!dirSnapshot.IsComplete)
                    {
                        snapshot.IsComplete = false;
                        snapshot.FailedPaths.AddRange(dirSnapshot.FailedPaths);
                    }

                    // If we have exclusions, process them
                    if (dir.Exclusions != null && dir.Exclusions.Count > 0)
                    {
                        _logger.Debug("Processing {Count} exclusions for directory: {Directory}",
                            dir.Exclusions.Count, directoryPath);

                        // Filter out excluded files
                        foreach (var exclusion in dir.Exclusions)
                        {
                            string exclusionPath = Path.Combine(directoryPath, exclusion);

                            // Remove files that match the exclusion path
                            var excludedFiles = dirSnapshot.Files.Values
                                .Where(f => f.MappedPath.StartsWith(exclusionPath, StringComparison.OrdinalIgnoreCase))
                                .Select(f => f.RelativePath)
                                .ToList();

                            foreach (var file in excludedFiles)
                            {
                                dirSnapshot.Files.Remove(file);
                            }

                            _logger.Debug("Excluded {Count} files matching pattern: {Pattern}",
                                excludedFiles.Count, exclusion);
                        }
                    }

                    // Add directory snapshot to the main snapshot
                    snapshot.RootPath = directoryPath;
                    foreach (var file in dirSnapshot.Files.Values)
                    {
                        snapshot.Files[file.RelativePath] = file;
                    }

                    _logger.Debug("Directory {Directory} snapshot complete with {Count} files",
                        directoryPath, dirSnapshot.Files.Count);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error creating snapshot for directory: {Directory}", directoryPath);
                    snapshot.FailedPaths.Add(directoryPath);
                    snapshot.IsComplete = false;
                }
            }

            _logger.Information("Snapshot creation complete. Total files: {FileCount}, Complete: {IsComplete}",
                snapshot.Files.Count, snapshot.IsComplete);

            // Validate the snapshot
            if (ValidateSnapshot(snapshot))
            {
                // Store the snapshot in history if valid
                StoreSuccessfulSnapshot(snapshot);
            }

            return snapshot;
        }

        /// <summary>
        /// Creates a snapshot with retry logic for handling network issues
        /// </summary>
        /// <param name="directories">List of directories with their exclusions</param>
        /// <param name="maxAttempts">Maximum number of retry attempts</param>
        /// <param name="retryDelay">Delay between retry attempts</param>
        /// <param name="maxParallelOperations">Maximum number of parallel operations</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>A DirectorySnapshot containing file information</returns>
        public static async Task<DirectorySnapshot> CreateSnapshotWithRetryAsync(
            List<DirectoryConfig> directories,
            int maxAttempts = 3,
            TimeSpan? retryDelay = null,
            int maxParallelOperations = 8,
            CancellationToken token = default)
        {
            if (retryDelay == null)
                retryDelay = TimeSpan.FromMinutes(1);

            DirectorySnapshot snapshot = null;
            int attemptCount = 0;
            bool snapshotValid = false;

            while (!snapshotValid && attemptCount < maxAttempts && !token.IsCancellationRequested)
            {
                attemptCount++;
                _logger.Information("Creating snapshot, attempt {Attempt} of {MaxAttempts}", attemptCount, maxAttempts);

                snapshot = await CreateSnapshotAsync(directories, maxParallelOperations, token);

                // Validate the snapshot
                snapshotValid = ValidateSnapshot(snapshot);

                if (!snapshotValid && attemptCount < maxAttempts && !token.IsCancellationRequested)
                {
                    _logger.Warning("Snapshot appears incomplete, retrying in {Delay} seconds...",
                        retryDelay.Value.TotalSeconds);
                    await Task.Delay(retryDelay.Value, token);
                }
            }

            if (!snapshotValid && snapshot != null)
            {
                _logger.Warning("Could not create a valid snapshot after {Attempts} attempts", attemptCount);
                snapshot.IsComplete = false;
            }

            return snapshot;
        }

        /// <summary>
        /// Validates a snapshot for completeness based on previous successful snapshots
        /// </summary>
        /// <param name="snapshot">The snapshot to validate</param>
        /// <param name="minimumCompletionPercentage">Minimum completion percentage threshold</param>
        /// <returns>True if the snapshot is valid, false otherwise</returns>
        public static bool ValidateSnapshot(DirectorySnapshot snapshot, double minimumCompletionPercentage = 80.0)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.RootPath))
                return false;

            // If snapshot is already marked as incomplete, it's invalid
            if (!snapshot.IsComplete)
            {
                _logger.Debug("Snapshot for {RootPath} is already marked as incomplete", snapshot.RootPath);
                return false;
            }

            // If we have records from previous successful snapshots
            if (_previousSuccessfulSnapshotCounts.TryGetValue(snapshot.RootPath, out int expectedFileCount) &&
                expectedFileCount > 0)
            {
                // Calculate the percentage of files we managed to capture
                double completionPercentage = snapshot.Files.Count * 100.0 / expectedFileCount;

                if (completionPercentage < minimumCompletionPercentage)
                {
                    _logger.Warning("Snapshot validation failed: Only captured {ActualCount} files ({Percentage:F1}%) " +
                                  "out of approximately {ExpectedCount} expected files",
                                  snapshot.Files.Count, completionPercentage, expectedFileCount);
                    snapshot.IsComplete = false;
                    return false;
                }
            }

            // No previous snapshot or passed validation
            return true;
        }

        /// <summary>
        /// Stores a successful snapshot in history for reference
        /// </summary>
        /// <param name="snapshot">The snapshot to store</param>
        private static void StoreSuccessfulSnapshot(DirectorySnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.RootPath) || !snapshot.IsComplete)
                return;

            string rootPath = snapshot.RootPath;

            // Update the expected file count for future validation
            _previousSuccessfulSnapshotCounts[rootPath] = snapshot.Files.Count;

            // Store the snapshot in history
            if (!_snapshotHistory.TryGetValue(rootPath, out var history))
            {
                history = new Queue<DirectorySnapshot>();
                _snapshotHistory[rootPath] = history;
            }

            // Add to history and maintain maximum size
            history.Enqueue(snapshot);
            while (history.Count > _maxSnapshotHistory)
            {
                history.Dequeue();
            }

            // Update the last successful snapshot
            _lastSuccessfulSnapshots[rootPath] = snapshot;

            _logger.Debug("Stored successful snapshot for {RootPath} with {FileCount} files",
                rootPath, snapshot.Files.Count);
        }

        /// <summary>
        /// Gets the last successful snapshot for a root path
        /// </summary>
        /// <param name="rootPath">The root path</param>
        /// <returns>The last successful snapshot, or null if none exists</returns>
        public static DirectorySnapshot GetLastSuccessfulSnapshot(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
                return null;

            if (_lastSuccessfulSnapshots.TryGetValue(rootPath, out var snapshot))
                return snapshot;

            return null;
        }

        /// <summary>
        /// Processes a single directory to create a snapshot
        /// </summary>
        private static async Task<DirectorySnapshot> ProcessDirectoryAsync(
            string directoryPath,
            List<string> exclusions,
            int maxParallelOperations,
            CancellationToken token)
        {
            var snapshot = new DirectorySnapshot { RootPath = directoryPath };

            // Handle case where directory doesn't exist
            if (!Directory.Exists(directoryPath))
            {
                _logger.Warning("Directory does not exist: {Directory}", directoryPath);
                snapshot.IsComplete = false;
                snapshot.FailedPaths.Add(directoryPath);
                return snapshot;
            }

            // Convert exclusions to full paths
            var excludedPaths = new List<string>();
            if (exclusions != null && exclusions.Count > 0)
            {
                excludedPaths = exclusions
                    .Select(e => Path.Combine(directoryPath, e))
                    .ToList();
            }

            // Get all files in the directory with a controlled approach
            var filesList = new ConcurrentBag<string>();
            var failedPaths = new ConcurrentBag<string>();
            var scanComplete = await Task.Run(() =>
            {
                try
                {
                    EnumerateAllFiles(directoryPath, filesList, failedPaths, excludedPaths, token);
                    return failedPaths.Count == 0;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error enumerating files in directory: {Directory}", directoryPath);
                    failedPaths.Add(directoryPath);
                    return false;
                }
            }, token);

            // Update snapshot with failed paths
            foreach (var path in failedPaths)
            {
                snapshot.FailedPaths.Add(path);
            }

            // Mark snapshot as incomplete if any paths failed
            if (!scanComplete)
            {
                snapshot.IsComplete = false;
                _logger.Warning("Directory scan for {Directory} was incomplete. {FailedCount} paths failed.",
                    directoryPath, failedPaths.Count);
            }

            // Process files in parallel
            using (var semaphore = new SemaphoreSlim(maxParallelOperations))
            {
                var tasks = new List<Task>();
                var fileErrors = new ConcurrentBag<string>();

                foreach (string filePath in filesList)
                {
                    if (token.IsCancellationRequested)
                        break;

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await semaphore.WaitAsync(token);

                            try
                            {
                                // Create file snapshot
                                var fileSnapshot = await CreateFileSnapshotAsync(filePath, directoryPath, token);
                                if (fileSnapshot != null)
                                {
                                    lock (snapshot.Files)
                                    {
                                        snapshot.Files[fileSnapshot.RelativePath] = fileSnapshot;
                                    }
                                }
                                else
                                {
                                    // Track files that failed
                                    fileErrors.Add(filePath);
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Error processing file: {FilePath}", filePath);
                            fileErrors.Add(filePath);
                        }
                    }, token));
                }

                await Task.WhenAll(tasks);

                // Check if any files failed
                if (fileErrors.Count > 0)
                {
                    snapshot.IsComplete = false;
                    _logger.Warning("{Count} files failed during snapshot creation", fileErrors.Count);
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Enumerates all files in a directory and its subdirectories
        /// </summary>
        private static void EnumerateAllFiles(
            string directoryPath,
            ConcurrentBag<string> filesList,
            ConcurrentBag<string> failedPaths,
            List<string> excludedPaths,
            CancellationToken token)
        {
            try
            {
                // Process files in current directory
                string[] files;
                try
                {
                    files = Directory.GetFiles(directoryPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.Warning(ex, "Access denied to directory: {Directory}", directoryPath);
                    failedPaths.Add(directoryPath);
                    return;
                }
                catch (IOException ex)
                {
                    _logger.Warning(ex, "IO error in directory: {Directory} - {Error}",
                        directoryPath, ex.Message);
                    failedPaths.Add(directoryPath);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error accessing directory: {Directory}", directoryPath);
                    failedPaths.Add(directoryPath);
                    return;
                }

                foreach (var file in files)
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Skip excluded files
                    if (excludedPaths != null && excludedPaths.Any(
                        e => file.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    filesList.Add(file);
                }

                // Get subdirectories with error handling
                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(directoryPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.Warning(ex, "Access denied when getting subdirectories: {Directory}", directoryPath);
                    failedPaths.Add(directoryPath);
                    return;
                }
                catch (IOException ex)
                {
                    _logger.Warning(ex, "IO error when getting subdirectories: {Directory} - {Error}",
                        directoryPath, ex.Message);
                    failedPaths.Add(directoryPath);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error accessing subdirectories: {Directory}", directoryPath);
                    failedPaths.Add(directoryPath);
                    return;
                }

                // Recursively process subdirectories
                foreach (var subDir in subDirs)
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Skip excluded directories
                    if (excludedPaths != null && excludedPaths.Any(
                        e => subDir.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    EnumerateAllFiles(subDir, filesList, failedPaths, excludedPaths, token);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error enumerating files in directory: {Directory}", directoryPath);
                failedPaths.Add(directoryPath);
            }
        }

        /// <summary>
        /// Creates a FileSnapshot object for a single file
        /// </summary>
        private static async Task<FileSnapshot> CreateFileSnapshotAsync(
            string filePath,
            string rootPath,
            CancellationToken token)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.Debug("File no longer exists: {FilePath}", filePath);
                    return null;
                }

                // Get file info with timeout handling for network paths
                FileInfo fileInfo;
                bool isNetworkPath = filePath.StartsWith("\\\\") ||
                                   (filePath.Length >= 2 && filePath[1] == ':' &&
                                    Path.GetPathRoot(filePath).TrimEnd('\\', ':').Length == 1);

                if (isNetworkPath)
                {
                    fileInfo = await GetFileInfoWithTimeoutAsync(filePath, token);
                }
                else
                {
                    fileInfo = new FileInfo(filePath);
                }

                if (!fileInfo.Exists)
                {
                    _logger.Debug("File no longer exists when getting info: {FilePath}", filePath);
                    return null;
                }

                // Calculate relative path
                string relativePath = filePath.Substring(rootPath.Length).TrimStart('\\', '/');

                // Create and return the file snapshot
                return new FileSnapshot
                {
                    MappedPath = filePath,
                    FileSize = fileInfo.Length,
                    LastWriteTime = fileInfo.LastWriteTime,
                    Attributes = fileInfo.Attributes
                };
            }
            catch (FileNotFoundException)
            {
                _logger.Debug("File was deleted during snapshot creation: {FilePath}", filePath);
            }
            catch (TimeoutException ex)
            {
                _logger.Warning(ex, "Timeout accessing file: {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error creating file snapshot: {FilePath}", filePath);
            }

            return null;
        }

        /// <summary>
        /// Gets FileInfo with timeout handling for network paths
        /// </summary>
        private static async Task<FileInfo> GetFileInfoWithTimeoutAsync(
            string filePath,
            CancellationToken token)
        {
            bool isNetworkPath = filePath.StartsWith("\\\\");
            TimeSpan timeout = isNetworkPath ? _networkConnectTimeout : TimeSpan.FromSeconds(5);

            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                token, timeoutCts.Token))
            {
                try
                {
                    return await Task.Run(() => new FileInfo(filePath), linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Operation timed out when getting file info: {filePath}");
                }
            }
        }

        /// <summary>
        /// Compares two directory snapshots to find differences
        /// </summary>
        /// <param name="oldSnapshot">Previous snapshot</param>
        /// <param name="newSnapshot">Current snapshot</param>
        /// <param name="ignoreDeletesIfNewSnapshotIncomplete">Whether to ignore deletions if new snapshot is incomplete</param>
        /// <returns>Sync operations needed to bring the target to match the source</returns>
        public static SyncOperations CompareSnapshots(
            DirectorySnapshot oldSnapshot,
            DirectorySnapshot newSnapshot,
            bool ignoreDeletesIfNewSnapshotIncomplete = true)
        {
            var result = new SyncOperations();

            if (oldSnapshot == null || newSnapshot == null)
            {
                _logger.Error("Cannot compare snapshots: one or both snapshots are null");
                return result;
            }

            // Check for incomplete snapshot
            if (!newSnapshot.IsComplete)
            {
                _logger.Warning("New snapshot is incomplete. Comparison results may not be accurate.");
                result.IsReliable = false;
            }

            _logger.Information("Comparing snapshots. Old files: {OldCount}, New files: {NewCount}, New snapshot complete: {IsComplete}",
                oldSnapshot.Files.Count, newSnapshot.Files.Count, newSnapshot.IsComplete);

            // Find files to add or update (in new but not in old, or changed)
            foreach (var newFile in newSnapshot.Files.Values)
            {
                if (oldSnapshot.Files.TryGetValue(newFile.RelativePath, out var oldFile))
                {
                    // File exists in both snapshots - check if changed
                    if (newFile.HasChanged(oldFile))
                    {
                        result.FilesToUpdate.Add(newFile);
                        _logger.Debug("File changed: {FilePath}", newFile.MappedPath);
                    }
                }
                else
                {
                    // File only in new snapshot - needs to be added
                    result.FilesToAdd.Add(newFile);
                    _logger.Debug("File added: {FilePath}", newFile.MappedPath);
                }
            }

            // Handle files to delete with special care if the new snapshot is incomplete
            if (ignoreDeletesIfNewSnapshotIncomplete && !newSnapshot.IsComplete)
            {
                _logger.Information("Skipping deletion detection because the new snapshot is incomplete");
            }
            else
            {
                foreach (var oldFile in oldSnapshot.Files.Values)
                {
                    // Check if any parent directory of this file failed scanning
                    bool inFailedPath = newSnapshot.FailedPaths.Any(path =>
                        oldFile.MappedPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));

                    if (!inFailedPath && !newSnapshot.Files.ContainsKey(oldFile.RelativePath))
                    {
                        result.FilesToDelete.Add(oldFile);
                        _logger.Debug("File deleted: {FilePath}", oldFile.MappedPath);
                    }
                }
            }

            _logger.Information("Comparison complete: {AddCount} to add, {UpdateCount} to update, {DeleteCount} to delete, Reliable: {IsReliable}",
                result.FilesToAdd.Count, result.FilesToUpdate.Count, result.FilesToDelete.Count, result.IsReliable);

            return result;
        }

        /// <summary>
        /// Performs a safe comparison that handles potentially incomplete snapshots
        /// </summary>
        /// <param name="oldSnapshot">Previous snapshot</param>
        /// <param name="newSnapshot">Current snapshot</param>
        /// <param name="previousSnapshot">Optional previous successful snapshot for reference</param>
        /// <returns>Sync operations with appropriate reliability flags</returns>
        public static SyncOperations SafeCompareSnapshots(
            DirectorySnapshot oldSnapshot,
            DirectorySnapshot newSnapshot,
            DirectorySnapshot previousSnapshot = null)
        {
            // If the new snapshot appears incomplete (much smaller than old)
            double completionThreshold = 0.7; // 70%
            if (oldSnapshot != null && newSnapshot != null &&
                oldSnapshot.Files.Count > 0 &&
                newSnapshot.Files.Count < oldSnapshot.Files.Count * completionThreshold)
            {
                _logger.Warning("New snapshot appears incomplete ({NewCount} files vs {OldCount} files) - using conservative comparison mode",
                    newSnapshot.Files.Count, oldSnapshot.Files.Count);

                // Only trust additions and updates, ignore deletions
                var result = new SyncOperations();

                // Process adds and updates from new snapshot
                foreach (var newFile in newSnapshot.Files.Values)
                {
                    if (oldSnapshot.Files.TryGetValue(newFile.RelativePath, out var oldFile))
                    {
                        if (newFile.HasChanged(oldFile))
                            result.FilesToUpdate.Add(newFile);
                    }
                    else
                    {
                        result.FilesToAdd.Add(newFile);
                    }
                }

                result.IsReliable = false;
                return result;
            }

            // Otherwise use normal comparison
            return CompareSnapshots(oldSnapshot, newSnapshot);
        }

        /// <summary>
        /// Validates if a path exists and is accessible
        /// </summary>
        private static async Task<bool> ValidatePathAsync(string path, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    _logger.Error("Cannot validate null or empty path");
                    return false;
                }

                // Check cache to avoid repeated validation of the same path
                if (_pathValidationTimes.TryGetValue(path, out DateTime lastValidation))
                {
                    if ((DateTime.Now - lastValidation) < _validationInterval)
                    {
                        // Path was recently validated, assume it's still valid
                        return true;
                    }
                }

                // Try to access the directory
                return await Task.Run(() =>
                {
                    try
                    {
                        var directoryInfo = new DirectoryInfo(path);
                        if (!directoryInfo.Exists)
                        {
                            _logger.Warning("Path does not exist or is not accessible: {Path}", path);
                            return false;
                        }

                        // Validate access 
                        bool hasAccess = HasDirectoryAccess(directoryInfo);

                        if (hasAccess)
                        {
                            // Update validation cache
                            _pathValidationTimes[path] = DateTime.Now;
                        }

                        return hasAccess;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error validating path: {Path}", path);
                        return false;
                    }
                }, token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in ValidatePathAsync for: {Path}", path);
                return false;
            }
        }

        /// <summary>
        /// Checks if a directory is accessible
        /// </summary>
        private static bool HasDirectoryAccess(DirectoryInfo directory)
        {
            try
            {
                // Attempt to get access by listing the first entry
                directory.GetFileSystemInfos("*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Configures network timeout settings
        /// </summary>
        /// <param name="readTimeoutSeconds">Read operation timeout in seconds</param>
        /// <param name="writeTimeoutSeconds">Write operation timeout in seconds</param>
        /// <param name="connectTimeoutSeconds">Connection timeout in seconds</param>
        public static void ConfigureNetworkTimeouts(int readTimeoutSeconds = 30, int writeTimeoutSeconds = 60, int connectTimeoutSeconds = 15)
        {
            _networkReadTimeout = TimeSpan.FromSeconds(readTimeoutSeconds);
            _networkWriteTimeout = TimeSpan.FromSeconds(writeTimeoutSeconds);
            _networkConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds);

            _logger.Information("Network timeouts configured: Read={ReadTimeout}s, Write={WriteTimeout}s, Connect={ConnectTimeout}s",
                readTimeoutSeconds, writeTimeoutSeconds, connectTimeoutSeconds);
        }
    }

    /// <summary>
    /// Represents a snapshot of a file's metadata for synchronization between network shares
    /// </summary>
    public class FileSnapshot
    {
        /// <summary>
        /// The full mapped path of the file (e.g., V:\aeapps\testfile.txt)
        /// </summary>
        public string MappedPath { get; set; }

        /// <summary>
        /// The size of the file in bytes
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// The last write time of the file
        /// </summary>
        public DateTime LastWriteTime { get; set; }

        /// <summary>
        /// The file attributes
        /// </summary>
        public FileAttributes Attributes { get; set; }

        /// <summary>
        /// Timestamp when the snapshot was created
        /// </summary>
        public DateTime SnapshotTime { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets the relative path from a base directory
        /// </summary>
        public string RelativePath
        {
            get
            {
                // Extract the path after the drive letter (e.g., aeapps\testfile.txt from V:\aeapps\testfile.txt)
                if (!string.IsNullOrEmpty(MappedPath))
                {
                    string rootPath = Path.GetPathRoot(MappedPath);
                    if (!string.IsNullOrEmpty(rootPath) && MappedPath.Length > rootPath.Length)
                    {
                        return MappedPath.Substring(rootPath.Length);
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the file name with extension
        /// </summary>
        public string FileName
        {
            get
            {
                return !string.IsNullOrEmpty(MappedPath) ? Path.GetFileName(MappedPath) : null;
            }
        }

        /// <summary>
        /// Gets the directory path
        /// </summary>
        public string DirectoryPath
        {
            get
            {
                return !string.IsNullOrEmpty(MappedPath) ? Path.GetDirectoryName(MappedPath) : null;
            }
        }

        /// <summary>
        /// Creates a FileSnapshot from an existing file
        /// </summary>
        /// <param name="filePath">The path to the file</param>
        /// <returns>A FileSnapshot with the file's metadata</returns>
        public static FileSnapshot FromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var fileInfo = new FileInfo(filePath);
            return new FileSnapshot
            {
                MappedPath = filePath,
                FileSize = fileInfo.Length,
                LastWriteTime = fileInfo.LastWriteTime,
                Attributes = fileInfo.Attributes,
                SnapshotTime = DateTime.Now
            };
        }

        /// <summary>
        /// Compares this snapshot with another to determine if the file has changed
        /// </summary>
        /// <param name="other">The other FileSnapshot to compare with</param>
        /// <returns>True if the files differ in size, timestamp, or attributes</returns>
        public bool HasChanged(FileSnapshot other)
        {
            if (other == null)
                return true;

            // Compare critical properties
            return FileSize != other.FileSize ||
                   Math.Abs((LastWriteTime - other.LastWriteTime).TotalSeconds) > 1 || // Allow 1 second difference
                   (Attributes & FileAttributes.Archive) != (other.Attributes & FileAttributes.Archive);
        }

        /// <summary>
        /// Converts the FileSnapshot to a FileItem that can be used with NetworkFileManager
        /// </summary>
        /// <returns>A FileItem representing this snapshot</returns>
        public FileItem ToFileItem()
        {
            return new FileItem
            {
                SourcePath = MappedPath,
                RelativePath = RelativePath,
                Attributes = Attributes
            };
        }
    }

    /// <summary>
    /// Represents a collection of file snapshots for a directory and its subdirectories
    /// </summary>
    public class DirectorySnapshot
    {
        /// <summary>
        /// The root directory path for this snapshot
        /// </summary>
        public string RootPath { get; set; }

        /// <summary>
        /// Dictionary of file snapshots keyed by relative path
        /// </summary>
        public Dictionary<string, FileSnapshot> Files { get; } = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Indicates whether the snapshot is complete or was interrupted (e.g., by network errors)
        /// </summary>
        public bool IsComplete { get; set; } = true;

        /// <summary>
        /// List of paths that failed during snapshot creation
        /// </summary>
        public List<string> FailedPaths { get; } = new List<string>();

        /// <summary>
        /// Timestamp when the snapshot was created
        /// </summary>
        public DateTime CreationTime { get; } = DateTime.Now;

        /// <summary>
        /// Creates a snapshot of all files in a directory tree
        /// </summary>
        /// <param name="rootPath">The root directory to snapshot</param>
        /// <param name="searchPattern">File search pattern</param>
        /// <returns>A DirectorySnapshot containing all matching files</returns>
        public static DirectorySnapshot CreateSnapshot(string rootPath, string searchPattern = "*.*")
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

            var snapshot = new DirectorySnapshot { RootPath = rootPath };

            try
            {
                // Get all files recursively
                var files = Directory.GetFiles(rootPath, searchPattern, SearchOption.AllDirectories);

                foreach (var filePath in files)
                {
                    try
                    {
                        var fileSnapshot = FileSnapshot.FromFile(filePath);
                        snapshot.Files[fileSnapshot.RelativePath] = fileSnapshot;
                    }
                    catch (Exception ex)
                    {
                        // Log but continue with other files
                        Log.Logger.Warning(ex, "Error creating snapshot for file: {FilePath}", filePath);
                        snapshot.IsComplete = false;
                        snapshot.FailedPaths.Add(Path.GetDirectoryName(filePath));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error creating directory snapshot: {Directory}", rootPath);
                snapshot.IsComplete = false;
                snapshot.FailedPaths.Add(rootPath);
            }

            return snapshot;
        }

        /// <summary>
        /// Compares this snapshot with another to find differences
        /// </summary>
        /// <param name="other">The snapshot to compare with</param>
        /// <param name="ignoreDeletesIfIncomplete">Whether to ignore deletions if either snapshot is incomplete</param>
        /// <returns>A SyncOperations object containing files that need to be synchronized</returns>
        public SyncOperations CompareWith(DirectorySnapshot other, bool ignoreDeletesIfIncomplete = true)
        {
            var result = new SyncOperations();

            if (other == null)
            {
                result.IsReliable = false;
                result.ReliabilityNote = "Cannot compare with null snapshot";
                return result;
            }

            // Check for incomplete snapshots
            if (!this.IsComplete || !other.IsComplete)
            {
                result.IsReliable = false;
                result.ReliabilityNote = "One or both snapshots are incomplete";
            }

            // Find files to add or update
            foreach (var file in Files.Values)
            {
                if (!other.Files.TryGetValue(file.RelativePath, out var otherFile))
                {
                    // File doesn't exist in other snapshot
                    result.FilesToAdd.Add(file);
                }
                else if (file.HasChanged(otherFile))
                {
                    // File exists but has changed
                    result.FilesToUpdate.Add(file);
                }
            }

            // Find files to delete (files in other that don't exist in this snapshot)
            // Skip deletion detection if either snapshot is incomplete and ignoreDeletesIfIncomplete is true
            if (!(ignoreDeletesIfIncomplete && (!this.IsComplete || !other.IsComplete)))
            {
                foreach (var file in other.Files.Values)
                {
                    // Check if any parent directory of this file failed scanning
                    bool inFailedPath = this.FailedPaths.Any(path =>
                        file.MappedPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));

                    if (!inFailedPath && !Files.ContainsKey(file.RelativePath))
                    {
                        result.FilesToDelete.Add(file);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Merges two snapshots to create a more complete view
        /// </summary>
        /// <param name="other">Another snapshot to merge with</param>
        /// <returns>A new snapshot containing files from both snapshots</returns>
        public DirectorySnapshot MergeWith(DirectorySnapshot other)
        {
            if (other == null || string.IsNullOrEmpty(other.RootPath) || !string.Equals(RootPath, other.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                // Can't merge snapshots from different roots
                return this;
            }

            var merged = new DirectorySnapshot
            {
                RootPath = this.RootPath,
                IsComplete = this.IsComplete && other.IsComplete
            };

            // Copy files from this snapshot
            foreach (var file in this.Files.Values)
            {
                merged.Files[file.RelativePath] = file;
            }

            // Add/overwrite with files from other snapshot
            foreach (var file in other.Files.Values)
            {
                // If the file exists in both snapshots, use the most recent one
                if (merged.Files.TryGetValue(file.RelativePath, out var existingFile))
                {
                    if (file.SnapshotTime > existingFile.SnapshotTime)
                    {
                        merged.Files[file.RelativePath] = file;
                    }
                }
                else
                {
                    merged.Files[file.RelativePath] = file;
                }
            }

            // Merge failed paths
            var allFailedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in this.FailedPaths)
            {
                allFailedPaths.Add(path);
            }
            foreach (var path in other.FailedPaths)
            {
                allFailedPaths.Add(path);
            }
            merged.FailedPaths.AddRange(allFailedPaths);

            return merged;
        }
    }

    /// <summary>
    /// Contains the results of comparing two directory snapshots, focusing only on files that need action
    /// </summary>
    public class SyncOperations
    {
        public List<FileSnapshot> FilesToAdd { get; } = new List<FileSnapshot>();
        public List<FileSnapshot> FilesToUpdate { get; } = new List<FileSnapshot>();
        public List<FileSnapshot> FilesToDelete { get; } = new List<FileSnapshot>();

        /// <summary>
        /// Indicates whether the comparison results are reliable
        /// This will be false if one of the snapshots was incomplete
        /// </summary>
        public bool IsReliable { get; set; } = true;

        /// <summary>
        /// Reason for reliability concerns, if any
        /// </summary>
        public string ReliabilityNote { get; set; }

        /// <summary>
        /// Converts the files to add and update to FileItem objects for use with NetworkFileManager
        /// </summary>
        /// <returns>A list of FileItem objects</returns>
        public List<FileItem> GetFileItemsForSync()
        {
            var items = new List<FileItem>();

            // Add files that need to be added or updated
            foreach (var file in FilesToAdd.Concat(FilesToUpdate))
            {
                items.Add(file.ToFileItem());
            }

            return items;
        }

        /// <summary>
        /// Gets a summary of the operations
        /// </summary>
        public string GetSummary()
        {
            string reliability = IsReliable ? "reliable" : "potentially unreliable";
            string summary = $"Files to add: {FilesToAdd.Count}, " +
                           $"Files to update: {FilesToUpdate.Count}, " +
                           $"Files to delete: {FilesToDelete.Count} " +
                           $"(Comparison is {reliability})";

            if (!IsReliable && !string.IsNullOrEmpty(ReliabilityNote))
            {
                summary += $" - {ReliabilityNote}";
            }

            return summary;
        }

        /// <summary>
        /// Checks if any synchronization is needed
        /// </summary>
        public bool HasChanges => FilesToAdd.Count > 0 || FilesToUpdate.Count > 0 || FilesToDelete.Count > 0;

        /// <summary>
        /// Gets only the reliable operations (for when IsReliable is false)
        /// </summary>
        /// <returns>A SyncOperations object with only reliable operations</returns>
        public SyncOperations GetReliableOperationsOnly()
        {
            if (IsReliable)
                return this;

            var reliableOps = new SyncOperations
            {
                IsReliable = true
            };

            // Additions and updates are typically reliable even with incomplete snapshots
            reliableOps.FilesToAdd.AddRange(FilesToAdd);
            reliableOps.FilesToUpdate.AddRange(FilesToUpdate);

            // Don't include deletions as they are unreliable with incomplete snapshots

            return reliableOps;
        }
    }

    /// <summary>
    /// Represents a file item for processing with NetworkFileManager
    /// </summary>
    public class FileItem
    {
        /// <summary>
        /// The source path of the file
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// The relative path for preserving directory structure
        /// </summary>
        public string RelativePath { get; set; }

        /// <summary>
        /// Custom destination path, if needed
        /// </summary>
        public string CustomDestinationPath { get; set; }

        /// <summary>
        /// File attributes to apply to the destination file
        /// </summary>
        public FileAttributes? Attributes { get; set; }
    }
}