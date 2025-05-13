using RunLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigManager;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Manages the creation and comparison of directory snapshots
    /// </summary>
    public static class SnapshotManager
    {
        #region Fields
        private static Logger _logger = Log.Logger;
        private static Logger _deletionLogger = Log.Logger;
        private static readonly ConcurrentDictionary<string, DateTime> _pathValidationTimes = new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan _validationInterval = TimeSpan.FromMinutes(30);
        #endregion

        /// <summary>
        /// Sets the logger instances to be used by SnapshotManager.
        /// </summary>
        /// <param name="mainLogger">The main logger instance for general logging.</param>
        /// <param name="deletionLogger">The logger instance for tracking file deletions.</param>
        /// <exception cref="ArgumentNullException">Thrown when either logger is null.</exception>
        public static void SetLoggers(Logger mainLogger, Logger deletionLogger)
        {
            _logger = mainLogger ?? throw new ArgumentNullException(nameof(mainLogger));
            _deletionLogger = deletionLogger ?? throw new ArgumentNullException(nameof(deletionLogger));
            _logger.Information("SnapshotManager loggers have been configured.");
        }

        #region Public Methods
        /// <summary>
        /// Creates a snapshot of the specified directories
        /// </summary>
        /// <param name="directories">The directories to snapshot</param>
        /// <param name="maxParallelOperations">Maximum number of parallel directory operations</param>
        /// <returns>A DirectorySnapshot containing information about the directories</returns>
        public static DirectorySnapshot CreateSnapshot(List<DirectoryConfig> directories, int maxParallelOperations = 8)
        {
            _logger.Information("Starting snapshot creation with max {0} parallel operations", maxParallelOperations);

            var snapshot = new DirectorySnapshot();

            if (directories == null || directories.Count == 0)
            {
                _logger.Warning("No directories found in configuration");
                return snapshot;
            }

            // Store excluded paths for each directory
            StoreExcludedPaths(directories, snapshot);

            // Process directories in parallel with throttling
            ProcessDirectoriesInParallel(directories, snapshot, maxParallelOperations);

            _logger.Information("Snapshot creation complete. Total directories: {0}, Total files: {1}, Inaccessible directories: {2}",
                snapshot.Directories.Count, snapshot.TotalFileCount, snapshot.InaccessibleDirectories.Count);

            return snapshot;
        }

        /// <summary>
        /// Compares two snapshots to identify added, modified, and deleted files
        /// </summary>
        /// <param name="oldSnapshot">The baseline snapshot</param>
        /// <param name="newSnapshot">The new snapshot to compare against the baseline</param>
        /// <param name="logChanges">Whether to log the changes</param>
        /// <returns>A SnapshotComparisonResult containing the differences</returns>
        public static SnapshotComparisonResult CompareSnapshots(
            DirectorySnapshot oldSnapshot,
            DirectorySnapshot newSnapshot,
            bool logChanges = true)
        {
            var result = new SnapshotComparisonResult();

            if (oldSnapshot == null || newSnapshot == null)
            {
                _logger.Error("Cannot compare snapshots: one or both snapshots are null");
                return result;
            }

            _logger.Information("Comparing snapshots. Old snapshot: {0} files, New snapshot: {1} files",
                oldSnapshot.TotalFileCount, newSnapshot.TotalFileCount);

            // Create a combined set of inaccessible directories
            var combinedInaccessibleDirs = CombineInaccessibleDirectories(oldSnapshot, newSnapshot);

            // Create lookup dictionaries for efficient comparison
            var oldFiles = CreateFileDictionary(oldSnapshot, combinedInaccessibleDirs);
            var newFiles = CreateFileDictionary(newSnapshot, combinedInaccessibleDirs);

            _logger.Information("After filtering inaccessible directories: comparing {0} files in old snapshot to {1} files in new snapshot",
                oldFiles.Count, newFiles.Count);

            // Find deleted files (in old but not in new)
            FindDeletedFiles(oldFiles, newFiles, result, logChanges);

            // Find added and modified files
            FindAddedAndModifiedFiles(oldFiles, newFiles, result, logChanges);

            _logger.Information("Snapshot comparison complete: {0}", result);
            return result;
        }

        /// <summary>
        /// Saves a directory snapshot to a JSON file
        /// </summary>
        /// <param name="snapshot">The snapshot to save</param>
        /// <param name="directory">The directory where to save the snapshot (will be created if it doesn't exist)</param>
        /// <param name="filenamePrefix">Optional prefix for the filename</param>
        /// <returns>The full path of the saved snapshot file</returns>
        public static string SaveSnapshot(DirectorySnapshot snapshot, string directory, string filenamePrefix = "Snapshot")
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Directory path cannot be empty", nameof(directory));

            try
            {
                // Ensure directory exists
                if (!Directory.Exists(directory))
                {
                    _logger.Information("Creating snapshot directory: {0}", directory);
                    Directory.CreateDirectory(directory);
                }

                // Generate a unique filename with timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string snapshotFilePath = Path.Combine(directory, $"{filenamePrefix}_{timestamp}.json");

                string json = SimpleJsonSerializer.Serialize(snapshot);
                File.WriteAllText(snapshotFilePath, json);

                _logger.Information($"Snapshot saved to {snapshotFilePath} with {snapshot.TotalFileCount} files across {snapshot.Directories.Count} directories");

                return snapshotFilePath;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error saving snapshot to file");
                throw; // Re-throw to allow caller to handle
            }
        }

        /// <summary>
        /// Loads a snapshot from a JSON file
        /// </summary>
        /// <param name="filePath">The path to the snapshot file</param>
        /// <returns>The loaded DirectorySnapshot</returns>
        public static DirectorySnapshot LoadSnapshot(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _logger.Error("Snapshot file does not exist: {0}", filePath);
                throw new FileNotFoundException("Snapshot file not found", filePath);
            }

            try
            {
                _logger.Information("Loading snapshot from file: {0}", filePath);

                string json = File.ReadAllText(filePath);
                var snapshot = SimpleJsonSerializer.Deserialize<DirectorySnapshot>(json);

                if (snapshot != null)
                {
                    _logger.Information("Successfully loaded snapshot with {0} files across {1} directories",
                        snapshot.TotalFileCount, snapshot.Directories.Count);
                }
                else
                {
                    _logger.Warning("Loaded snapshot is null");
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error loading snapshot from file: {0}", filePath);
                throw; // Re-throw to allow caller to handle
            }
        }

        /// <summary>
        /// Takes a snapshot of directories specified in the configuration and saves it.
        /// </summary>
        /// <param name="directories">The directories to snapshot</param>
        /// <param name="testMode">Whether test mode is enabled</param>
        /// <param name="maxParallelOperations">Maximum number of parallel operations to use</param>
        /// <param name="snapshotsDirectory">Directory where snapshots will be saved</param>
        /// <param name="logger">Logger to use for recording operations</param>
        /// <returns>The path to the saved snapshot file if successful, null otherwise</returns>
        public static string TakeAndSaveSnapshot(List<DirectoryConfig> directories, bool testMode, int maxParallelOperations, string snapshotsDirectory, Logger logger)
        {
            try
            {
                logger?.Information("Taking a new snapshot");

                if (directories.Count == 0)
                {
                    logger?.Warning("No directories found in configuration. Nothing to process.");
                    return null;
                }

                // Take snapshot
                logger?.Information($"Taking snapshot of {directories.Count} configured directories...");

                if (testMode)
                {
                    logger?.Information($"TEST MODE: Would create a snapshot of {directories.Count} directories");
                    return null;
                }

                var snapshot = CreateSnapshot(directories, maxParallelOperations);

                logger?.Information($"Snapshot created with {snapshot.TotalFileCount} files across {snapshot.Directories.Count} directories");

                if (snapshot.InaccessibleDirectories.Count > 0)
                {
                    logger?.Warning($"{snapshot.InaccessibleDirectories.Count} directories were inaccessible during snapshot");

                    // Log the first few inaccessible directories
                    int logCount = Math.Min(5, snapshot.InaccessibleDirectories.Count);
                    int count = 0;
                    foreach (var dir in snapshot.InaccessibleDirectories)
                    {
                        logger?.Warning($"Inaccessible directory: {dir}");
                        count++;
                        if (count >= logCount) break;
                    }

                    if (snapshot.InaccessibleDirectories.Count > logCount)
                    {
                        logger?.Warning($"...and {snapshot.InaccessibleDirectories.Count - logCount} more inaccessible directories");
                    }
                }

                // Save the snapshot
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string snapshotFilePath = SaveSnapshot(snapshot, snapshotsDirectory, $"Snapshot_{timestamp}");
                logger?.Information($"Snapshot saved to {snapshotFilePath}");

                return snapshotFilePath;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "Error taking snapshot");
                return null;
            }
        }

        /// <summary>
        /// Gets a list of existing snapshot files from the specified directory, ordered by last write time (newest first).
        /// </summary>
        /// <param name="snapshotsDirectory">Directory containing snapshot files</param>
        /// <param name="logger">Logger to use for recording operations</param>
        /// <returns>List of snapshot file paths</returns>
        public static List<string> GetExistingSnapshotFiles(string snapshotsDirectory, Logger logger)
        {
            try
            {
                // Make sure snapshots directory exists
                if (!Directory.Exists(snapshotsDirectory))
                {
                    Directory.CreateDirectory(snapshotsDirectory);
                    return new List<string>();
                }

                // Get all .json files in the snapshots directory
                var files = Directory.GetFiles(snapshotsDirectory, "*.json")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                return files;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "Error getting existing snapshot files");
                return new List<string>();
            }
        }

        /// <summary>
        /// Deletes all snapshot files from the specified directory.
        /// </summary>
        /// <param name="snapshotFiles">List of snapshot file paths to delete</param>
        /// <param name="testMode">Whether test mode is enabled</param>
        /// <param name="logger">Logger to use for recording operations</param>
        /// <returns>The number of files successfully deleted</returns>
        public static int DeleteAllSnapshots(List<string> snapshotFiles, bool testMode, Logger logger)
        {
            int deletedCount = 0;

            if (snapshotFiles == null || snapshotFiles.Count == 0)
            {
                logger?.Warning("No snapshot files to delete");
                return 0;
            }

            foreach (var file in snapshotFiles)
            {
                try
                {
                    if (!testMode)
                    {
                        File.Delete(file);
                        deletedCount++;
                        logger?.Information($"Deleted snapshot file: {file}");
                    }
                    else
                    {
                        logger?.Information($"TEST MODE: Would delete snapshot file: {file}");
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, $"Error deleting snapshot file: {file}");
                }
            }

            string verb = testMode ? "Would delete" : "Deleted";
            logger?.Information($"{verb} {deletedCount} out of {snapshotFiles.Count} snapshot files");

            return deletedCount;
        }

        /// <summary>
        /// Deletes snapshot files older than the specified date.
        /// </summary>
        /// <param name="snapshotFiles">List of snapshot file paths</param>
        /// <param name="cutoffDate">Date before which files should be deleted</param>
        /// <param name="testMode">Whether test mode is enabled</param>
        /// <param name="logger">Logger to use for recording operations</param>
        /// <returns>A tuple containing the number of files deleted and the number of files kept</returns>
        public static (int deleted, int kept) DeleteSnapshotsOlderThan(List<string> snapshotFiles, DateTime cutoffDate, bool testMode, Logger logger)
        {
            int deletedCount = 0;
            int skippedCount = 0;

            if (snapshotFiles == null || snapshotFiles.Count == 0)
            {
                logger?.Warning("No snapshot files to process");
                return (0, 0);
            }

            logger?.Information($"Deleting snapshots older than {cutoffDate:yyyy-MM-dd}");

            foreach (var file in snapshotFiles)
            {
                try
                {
                    DateTime fileDate = File.GetLastWriteTime(file);
                    if (fileDate < cutoffDate)
                    {
                        if (!testMode)
                        {
                            File.Delete(file);
                            logger?.Information($"Deleted snapshot file: {file} (Date: {fileDate:yyyy-MM-dd})");
                            deletedCount++;
                        }
                        else
                        {
                            logger?.Information($"TEST MODE: Would delete snapshot file: {file} (Date: {fileDate:yyyy-MM-dd})");
                            deletedCount++;
                        }
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, $"Error deleting snapshot file: {file}");
                }
            }

            string verb = testMode ? "Would delete" : "Deleted";
            logger?.Information($"{verb} {deletedCount} snapshot files, kept {skippedCount} files");

            return (deletedCount, skippedCount);
        }

        /// <summary>
        /// Keeps only the specified number of most recent snapshots and deletes the rest.
        /// </summary>
        /// <param name="snapshotFiles">List of snapshot file paths</param>
        /// <param name="keepCount">Number of most recent snapshots to keep</param>
        /// <param name="testMode">Whether test mode is enabled</param>
        /// <param name="logger">Logger to use for recording operations</param>
        /// <returns>The number of files successfully deleted</returns>
        public static int KeepRecentSnapshots(List<string> snapshotFiles, int keepCount, bool testMode, Logger logger)
        {
            if (snapshotFiles == null || snapshotFiles.Count == 0)
            {
                logger?.Warning("No snapshot files to process");
                return 0;
            }

            if (keepCount <= 0)
            {
                logger?.Error("keepCount must be greater than 0");
                return 0;
            }

            if (keepCount >= snapshotFiles.Count)
            {
                logger?.Information($"There are only {snapshotFiles.Count} snapshots, which is fewer than {keepCount}. Nothing to delete.");
                return 0;
            }

            // Sort files by last write time (most recent first)
            var sortedFiles = snapshotFiles.OrderByDescending(f => File.GetLastWriteTime(f)).ToList();
            var filesToDelete = sortedFiles.Skip(keepCount).ToList();

            logger?.Information($"Keeping {keepCount} most recent snapshots, deleting {filesToDelete.Count} older snapshots");
            int deletedCount = 0;

            foreach (var file in filesToDelete)
            {
                try
                {
                    if (!testMode)
                    {
                        File.Delete(file);
                        logger?.Information($"Deleted snapshot file: {file}");
                        deletedCount++;
                    }
                    else
                    {
                        logger?.Information($"TEST MODE: Would delete snapshot file: {file}");
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, $"Error deleting snapshot file: {file}");
                }
            }

            string verb = testMode ? "Would delete" : "Deleted";
            logger?.Information($"{verb} {deletedCount} out of {filesToDelete.Count} snapshot files");

            return deletedCount;
        }
        #endregion

        #region Private Helper Methods
        /// <summary>
        /// Stores excluded paths in the snapshot
        /// </summary>
        private static void StoreExcludedPaths(List<DirectoryConfig> directories, DirectorySnapshot snapshot)
        {
            foreach (var dir in directories.Where(d => d.Exclusions.Count > 0))
            {
                snapshot.ExcludedPaths[dir.Path] = new HashSet<string>(dir.Exclusions, StringComparer.OrdinalIgnoreCase);
            }

            _logger.Debug("Processing {0} directories with {1} exclusions in total",
                directories.Count,
                snapshot.ExcludedPaths.Sum(ep => ep.Value.Count));
        }

        /// <summary>
        /// Processes directories in parallel with throttling
        /// </summary>
        private static void ProcessDirectoriesInParallel(
            List<DirectoryConfig> directories,
            DirectorySnapshot snapshot,
            int maxParallelOperations)
        {
            using (var throttler = new SemaphoreSlim(maxParallelOperations))
            {
                var tasks = new List<Task>();

                foreach (var dir in directories)
                {
                    string directory = dir.Path;
                    var excludedSubdirs = dir.Exclusions;

                    // Validate path first
                    if (!ValidatePath(directory))
                    {
                        _logger.Warning("Skipping invalid or inaccessible directory: {0}", directory);

                        lock (snapshot.InaccessibleDirectories)
                        {
                            snapshot.InaccessibleDirectories.Add(directory);
                        }
                        continue;
                    }

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await throttler.WaitAsync();
                            ProcessSingleDirectory(directory, excludedSubdirs, snapshot);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error creating snapshot for directory: {0}", directory);

                            lock (snapshot.InaccessibleDirectories)
                            {
                                snapshot.InaccessibleDirectories.Add(directory);
                            }
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }));
                }

                Task.WaitAll(tasks.ToArray());
            }
        }

        /// <summary>
        /// Validates if a path exists and is accessible
        /// </summary>
        private static bool ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Check if we've validated this path recently
            if (_pathValidationTimes.TryGetValue(path, out DateTime lastValidationTime))
            {
                if (DateTime.Now - lastValidationTime < _validationInterval)
                {
                    return true; // Path was recently validated
                }
            }

            try
            {
                if (Directory.Exists(path))
                {
                    // Update validation time
                    _pathValidationTimes[path] = DateTime.Now;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error validating path: {0}", path);
                return false;
            }
        }

        /// <summary>
        /// Processes a single directory to add to the snapshot
        /// </summary>
        private static void ProcessSingleDirectory(
            string directory,
            List<string> excludedSubdirs,
            DirectorySnapshot snapshot)
        {
            _logger.Debug("Creating snapshot for directory: {0}", directory);
            var result = CreateDirectorySnapshot(directory, excludedSubdirs);

            if (result.InaccessibleSubdirs.Count > 0)
            {
                _logger.Warning("Found {0} inaccessible subdirectories in {1}",
                    result.InaccessibleSubdirs.Count, directory);

                lock (snapshot.InaccessibleDirectories)
                {
                    foreach (var inaccessibleDir in result.InaccessibleSubdirs)
                    {
                        snapshot.InaccessibleDirectories.Add(inaccessibleDir);
                    }
                }
            }

            if (result.FileSnapshots.Count > 0)
            {
                lock (snapshot.Directories)
                {
                    snapshot.Directories[directory] = result.FileSnapshots;
                }
                _logger.Debug("Directory {0} snapshot complete with {1} files",
                    directory, result.FileSnapshots.Count);
            }
            else
            {
                _logger.Warning("No files found in directory: {0}", directory);
            }
        }

        /// <summary>
        /// Result object for directory snapshot creation that includes inaccessible subdirectories
        /// </summary>
        private class DirectorySnapshotResult
        {
            public List<FileSnapshot> FileSnapshots { get; set; } = new List<FileSnapshot>();
            public HashSet<string> InaccessibleSubdirs { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a snapshot of a single directory with exclusions and tracks inaccessible subdirectories
        /// </summary>
        private static DirectorySnapshotResult CreateDirectorySnapshot(string directoryPath, List<string> excludedSubdirs)
        {
            var result = new DirectorySnapshotResult();
            var directoryLength = directoryPath.Length;

            if (directoryPath[directoryPath.Length - 1] != Path.DirectorySeparatorChar)
                directoryLength++; // Account for separator

            // Convert excluded subdirs to full paths
            var excludedFullPaths = excludedSubdirs
                .Select(subdir => Path.Combine(directoryPath, subdir))
                .ToList();

            // Network path retry parameters
            const int maxRetries = 3;
            const int retryDelayMs = 1000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(directoryPath);
                    if (!dirInfo.Exists)
                    {
                        _logger.Warning("Directory does not exist: {0}", directoryPath);
                        result.InaccessibleSubdirs.Add(directoryPath);
                        return result;
                    }

                    // Use a safer approach to enumerate files
                    EnumerateFilesWithFallback(dirInfo, result, directoryLength, excludedFullPaths);
                    break; // If we got here, enumeration was successful - exit the retry loop
                }
                catch (IOException ex) when (IsNetworkPathError(ex) && attempt < maxRetries)
                {
                    _logger.Warning(ex, "Network path error on attempt {0}/{1} for {2}. Retrying in {3}ms...",
                        attempt, maxRetries, directoryPath, retryDelayMs);

                    Thread.Sleep(retryDelayMs * attempt);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error creating snapshot for directory: {0} after {1} attempts",
                        directoryPath, attempt);
                    result.InaccessibleSubdirs.Add(directoryPath);
                    return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Determines if an exception is related to a network path error
        /// </summary>
        private static bool IsNetworkPathError(Exception ex)
        {
            if (ex == null)
                return false;

            if (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                string message = ex.Message.ToLowerInvariant();
                return message.Contains("network path was not found") ||
                       message.Contains("network name is no longer available") ||
                       message.Contains("network path cannot be accessed") ||
                       message.Contains("not connected") ||
                       message.Contains("path is unavailable") ||
                       message.Contains("currently unavailable") ||
                       message.Contains("could not find") ||
                       message.Contains("broken pipe") ||
                       message.Contains("unable to connect");
            }
            return false;
        }

        /// <summary>
        /// Enumerates files with a fallback strategy for better reliability and tracks inaccessible subdirectories
        /// </summary>
        private static void EnumerateFilesWithFallback(
            DirectoryInfo dirInfo,
            DirectorySnapshotResult result,
            int directoryLength,
            List<string> excludedFullPaths)
        {
            try
            {
                // First try regular enumeration
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    ProcessFile(file, result.FileSnapshots, directoryLength, excludedFullPaths);
                }
            }
            catch (IOException)
            {
                // Fallback: try a more cautious approach with manual recursion
                _logger.Warning("Primary enumeration failed, trying directory-by-directory approach for {0}", dirInfo.FullName);
                EnumerateFilesManually(dirInfo, result, directoryLength, excludedFullPaths);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Add the directory to inaccessible list and try manual enumeration
                _logger.Warning(ex, "Access denied for directory: {0}, will try subdirectories individually", dirInfo.FullName);
                result.InaccessibleSubdirs.Add(dirInfo.FullName);
                EnumerateFilesManually(dirInfo, result, directoryLength, excludedFullPaths);
            }
        }

        /// <summary>
        /// Recursively enumerates files manually when standard enumeration fails
        /// </summary>
        private static void EnumerateFilesManually(
            DirectoryInfo dirInfo,
            DirectorySnapshotResult result,
            int directoryLength,
            List<string> excludedFullPaths)
        {
            try
            {
                // First get files from the current directory
                EnumerateFilesInDirectory(dirInfo, result, directoryLength, excludedFullPaths);

                // Then recurse into subdirectories
                EnumerateSubdirectories(dirInfo, result, directoryLength, excludedFullPaths);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error in manual enumeration for directory: {0}", dirInfo.FullName);
                result.InaccessibleSubdirs.Add(dirInfo.FullName);
            }
        }

        /// <summary>
        /// Enumerates and processes all files in the current directory (non-recursive)
        /// </summary>
        private static void EnumerateFilesInDirectory(
            DirectoryInfo dirInfo,
            DirectorySnapshotResult result,
            int directoryLength,
            List<string> excludedFullPaths)
        {
            try
            {
                foreach (var file in dirInfo.GetFiles())
                {
                    ProcessFile(file, result.FileSnapshots, directoryLength, excludedFullPaths);
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Warning("Access denied to files in directory: {0}", dirInfo.FullName);
                result.InaccessibleSubdirs.Add(dirInfo.FullName);
            }
            catch (IOException ex)
            {
                _logger.Warning(ex, "IO error accessing files in directory: {0}", dirInfo.FullName);
                result.InaccessibleSubdirs.Add(dirInfo.FullName);
            }
        }

        /// <summary>
        /// Recursively enumerates subdirectories
        /// </summary>
        private static void EnumerateSubdirectories(
            DirectoryInfo dirInfo,
            DirectorySnapshotResult result,
            int directoryLength,
            List<string> excludedFullPaths)
        {
            try
            {
                DirectoryInfo[] subDirs = null;

                try
                {
                    subDirs = dirInfo.GetDirectories();
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.Warning("Access denied when getting subdirectories of: {0}", dirInfo.FullName);
                    result.InaccessibleSubdirs.Add(dirInfo.FullName);
                    return;
                }
                catch (IOException ex)
                {
                    _logger.Warning(ex, "IO error getting subdirectories of: {0}", dirInfo.FullName);
                    result.InaccessibleSubdirs.Add(dirInfo.FullName);
                    return;
                }

                foreach (var subDir in subDirs)
                {
                    // Skip excluded directories
                    if (IsDirectoryExcluded(subDir.FullName, excludedFullPaths))
                    {
                        continue;
                    }

                    try
                    {
                        EnumerateFilesManually(subDir, result, directoryLength, excludedFullPaths);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.Warning("Access denied to subdirectory: {0}", subDir.FullName);
                        result.InaccessibleSubdirs.Add(subDir.FullName);
                    }
                    catch (Exception ex)
                    {
                        // Log but continue with other directories
                        _logger.Warning(ex, "Error processing subdirectory: {0}", subDir.FullName);
                        result.InaccessibleSubdirs.Add(subDir.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error processing subdirectories of: {0}", dirInfo.FullName);
                result.InaccessibleSubdirs.Add(dirInfo.FullName);
            }
        }

        /// <summary>
        /// Checks if a directory is in the excluded list
        /// </summary>
        private static bool IsDirectoryExcluded(string dirPath, List<string> excludedFullPaths)
        {
            return excludedFullPaths.Any(excluded =>
                dirPath.StartsWith(excluded, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Processes a single file for inclusion in the snapshot
        /// </summary>
        private static void ProcessFile(
            FileInfo file,
            List<FileSnapshot> fileSnapshots,
            int directoryLength,
            List<string> excludedFullPaths)
        {
            try
            {
                // Check if file is in an excluded directory
                string fileDirPath = file.DirectoryName;
                if (IsDirectoryExcluded(fileDirPath, excludedFullPaths))
                {
                    return;
                }

                var snapshot = new FileSnapshot
                {
                    FullPath = file.FullName,
                    RelativePath = file.FullName.Substring(directoryLength),
                    Size = file.Length,
                    LastWriteTime = file.LastWriteTime,
                    Attributes = file.Attributes
                };

                fileSnapshots.Add(snapshot);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error processing file: {0}", file.FullName);
            }
        }

        /// <summary>
        /// Creates a combined set of inaccessible directories from two snapshots
        /// </summary>
        private static HashSet<string> CombineInaccessibleDirectories(DirectorySnapshot oldSnapshot, DirectorySnapshot newSnapshot)
        {
            var combinedInaccessibleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in oldSnapshot.InaccessibleDirectories)
                combinedInaccessibleDirs.Add(dir);

            foreach (var dir in newSnapshot.InaccessibleDirectories)
                combinedInaccessibleDirs.Add(dir);

            if (combinedInaccessibleDirs.Count > 0)
            {
                _logger.Information("Found {0} directories that were inaccessible in one or both snapshots - these will be excluded from comparison",
                    combinedInaccessibleDirs.Count);
            }

            return combinedInaccessibleDirs;
        }

        /// <summary>
        /// Creates a dictionary of files from a snapshot, filtering out files in inaccessible directories
        /// </summary>
        private static Dictionary<string, FileSnapshot> CreateFileDictionary(
            DirectorySnapshot snapshot,
            HashSet<string> inaccessibleDirs)
        {
            var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var dirEntry in snapshot.Directories)
            {
                string baseDir = dirEntry.Key;

                // Skip this directory if it was inaccessible in either snapshot
                if (inaccessibleDirs.Contains(baseDir))
                {
                    _logger.Debug("Skipping directory {0} in snapshot because it was inaccessible", baseDir);
                    continue;
                }

                foreach (var file in dirEntry.Value)
                {
                    // Skip files in inaccessible subdirectories
                    string filePath = Path.Combine(baseDir, file.RelativePath);
                    string fileDirectory = Path.GetDirectoryName(filePath);

                    if (IsInInaccessibleDirectory(fileDirectory, inaccessibleDirs))
                    {
                        _logger.Debug("Skipping file {0} in snapshot because it's in an inaccessible directory", filePath);
                        continue;
                    }

                    string fullPath = Path.Combine(baseDir, file.RelativePath).ToLowerInvariant();
                    files[fullPath] = file;
                }
            }

            return files;
        }

        /// <summary>
        /// Finds deleted files between two snapshots
        /// </summary>
        private static void FindDeletedFiles(
            Dictionary<string, FileSnapshot> oldFiles,
            Dictionary<string, FileSnapshot> newFiles,
            SnapshotComparisonResult result,
            bool logChanges)
        {
            foreach (var oldFile in oldFiles)
            {
                if (!newFiles.ContainsKey(oldFile.Key))
                {
                    result.DeletedFiles.Add(oldFile.Value);
                    if (logChanges)
                    {
                        _deletionLogger.Information("File deleted: {0}", oldFile.Value.FullPath);
                    }
                }
            }
        }

        /// <summary>
        /// Finds added and modified files between two snapshots
        /// </summary>
        private static void FindAddedAndModifiedFiles(
            Dictionary<string, FileSnapshot> oldFiles,
            Dictionary<string, FileSnapshot> newFiles,
            SnapshotComparisonResult result,
            bool logChanges)
        {
            foreach (var newFile in newFiles)
            {
                if (!oldFiles.ContainsKey(newFile.Key))
                {
                    result.AddedFiles.Add(newFile.Value);
                    if (logChanges)
                    {
                        _logger.Information("File added: {0}", newFile.Value.FullPath);
                    }
                }
                else
                {
                    var oldFile = oldFiles[newFile.Key];

                    // Compare file attributes (size, last write time, etc.)
                    if (oldFile.Size != newFile.Value.Size ||
                        oldFile.LastWriteTime != newFile.Value.LastWriteTime)
                    {
                        result.ModifiedFiles.Add(newFile.Value);
                        if (logChanges)
                        {
                            _logger.Information("File modified: {0}", newFile.Value.FullPath);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determines if a file path is within an inaccessible directory or any of its parent directories
        /// </summary>
        private static bool IsInInaccessibleDirectory(string path, HashSet<string> inaccessibleDirectories)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Check if the path itself is in the list
            if (inaccessibleDirectories.Contains(path))
                return true;

            // Check if any parent directory is in the list
            foreach (var inaccessibleDir in inaccessibleDirectories)
            {
                if (path.StartsWith(inaccessibleDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(inaccessibleDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        #endregion
    }
}