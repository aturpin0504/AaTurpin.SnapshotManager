using RunLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Helper class for network file operations such as copying or moving files across network locations.
    /// </summary>
    public static class NetworkFileManager
    {
        private static Logger _logger = Log.Logger;
        private static Logger _deletionLogger = Log.Logger;
        public const int DefaultMaxRetries = 3;
        public const int DefaultRetryDelayMs = 500;
        public const int DefaultMaxParallelOps = 4;

        /// <summary>
        /// Sets the logger instances to be used by NetworkFileManager.
        /// </summary>
        /// <param name="logger">The main logger instance for general logging.</param>
        /// <param name="deletionLogger">The logger instance for tracking file deletions.</param>
        /// <exception cref="ArgumentNullException">Thrown when either logger is null.</exception>
        public static void SetLoggers(Logger logger, Logger deletionLogger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deletionLogger = deletionLogger ?? throw new ArgumentNullException(nameof(deletionLogger));
            _logger.Information("NetworkFileManager loggers have been configured.");
        }

        #region Public Methods

        /// <summary>
        /// Copies files from network to a destination directory with preserved structure based on snapshot comparison
        /// </summary>
        /// <param name="comparisonResult">The snapshot comparison result containing files to process</param>
        /// <param name="destinationDir">Base destination directory where files will be copied</param>
        /// <param name="options">Options to customize the file operation</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Result of the batch operation</returns>
        public static async Task<BatchFileOperationResult> CopyNetworkFilesAsync(
            SnapshotComparisonResult comparisonResult,
            string destinationDir,
            FileOperationOptions options = null,
            CancellationToken token = default)
        {
            if (comparisonResult == null)
                throw new ArgumentNullException(nameof(comparisonResult));

            if (string.IsNullOrEmpty(destinationDir))
                throw new ArgumentException("Destination directory cannot be null or empty", nameof(destinationDir));

            options = options ?? new FileOperationOptions();

            // Create file items for modified and added files
            var fileItems = new List<FileItem>();
            fileItems.AddRange(comparisonResult.ModifiedFiles.Select(f => CreateFileItemWithPreservedStructure(f.FullPath)));
            fileItems.AddRange(comparisonResult.AddedFiles.Select(f => CreateFileItemWithPreservedStructure(f.FullPath)));

            _logger.Information($"Copying {fileItems.Count} files to {destinationDir}");

            return await ProcessFilesAsync(fileItems, destinationDir, FileOperation.Copy, options, token);
        }

        /// <summary>
        /// Restores files from preserved structure back to their original network locations
        /// </summary>
        /// <param name="sourceDir">Source directory containing the preserved file structure</param>
        /// <param name="options">Options to customize the file operation</param>
        /// <param name="searchPattern">File search pattern</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Result of the batch operation</returns>
        public static async Task<BatchFileOperationResult> RestoreFilesToNetworkAsync(
            string sourceDir,
            FileOperationOptions options = null,
            string searchPattern = "*.*",
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(sourceDir))
                throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDir));

            if (!Directory.Exists(sourceDir))
            {
                _logger.Error($"Source directory does not exist: {sourceDir}");
                return new BatchFileOperationResult();
            }

            options = options ?? new FileOperationOptions();

            // Find all files in the source directory
            _logger.Information($"Scanning files in {sourceDir} to restore to network locations");
            var filePaths = Directory.GetFiles(sourceDir, searchPattern, SearchOption.AllDirectories);
            _logger.Information($"Found {filePaths.Length} files to restore");

            if (filePaths.Length == 0)
                return new BatchFileOperationResult();

            // Create file items with reconstructed original paths
            var fileItems = new List<FileItem>();
            foreach (var path in filePaths)
            {
                var relativePath = path.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var networkPath = ReconstructNetworkPath(relativePath);

                fileItems.Add(new FileItem(path) { CustomDestinationPath = networkPath });
            }

            return await ProcessFilesAsync(fileItems, string.Empty, FileOperation.Move, options, token);
        }

        /// <summary>
        /// Deletes files from network locations based on paths in a log file
        /// </summary>
        /// <param name="logFilePath">Path to the log file containing deletion entries</param>
        /// <param name="options">Options to customize the file operation</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Result of the batch operation</returns>
        public static async Task<BatchFileOperationResult> DeleteNetworkFilesFromLogAsync(
            string logFilePath,
            FileOperationOptions options = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath))
            {
                _logger.Error($"Log file does not exist: {logFilePath}");
                return new BatchFileOperationResult();
            }

            options = options ?? new FileOperationOptions();
            var filesToDelete = new List<FileItem>();

            // Extract file paths from log entries
            var logPattern = new Regex(@"File deleted: (.+)$", RegexOptions.Compiled);

            foreach (var line in File.ReadAllLines(logFilePath))
            {
                if (token.IsCancellationRequested)
                    break;

                var match = logPattern.Match(line);
                if (match.Success)
                {
                    var filePath = match.Groups[1].Value.Trim();
                    if (File.Exists(filePath))
                        filesToDelete.Add(new FileItem(filePath));
                }
            }

            _logger.Information($"Found {filesToDelete.Count} files to delete from log file");

            if (filesToDelete.Count == 0)
                return new BatchFileOperationResult();

            return await ProcessFilesAsync(filesToDelete, string.Empty, FileOperation.Delete, options, token);
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Creates a FileItem with preserved directory structure
        /// </summary>
        private static FileItem CreateFileItemWithPreservedStructure(string fullPath)
        {
            var driveLetter = Path.GetPathRoot(fullPath).TrimEnd('\\', ':');
            var pathWithoutDrive = fullPath.Substring(Path.GetPathRoot(fullPath).Length);
            var preservedPath = Path.Combine(driveLetter, pathWithoutDrive);

            return new FileItem(fullPath) { RelativePath = preservedPath };
        }

        /// <summary>
        /// Reconstructs the original network path from a preserved path
        /// </summary>
        private static string ReconstructNetworkPath(string preservedPath)
        {
            if (string.IsNullOrEmpty(preservedPath))
                return string.Empty;

            var parts = preservedPath.Split(Path.DirectorySeparatorChar);
            if (parts.Length == 0)
                return preservedPath;

            // Extract drive letter component
            var driveLetter = parts[0];
            if (driveLetter.Length == 1 && char.IsLetter(driveLetter[0]))
            {
                // Convert to drive letter format with colon
                parts[0] = driveLetter + ":";
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        /// <summary>
        /// Core method for processing files with retry logic
        /// </summary>
        private static async Task<BatchFileOperationResult> ProcessFilesAsync(
            IEnumerable<FileItem> files,
            string destinationDir,
            FileOperation operation,
            FileOperationOptions options,
            CancellationToken token)
        {
            var result = new BatchFileOperationResult();
            var filesList = files.ToList();

            if (filesList.Count == 0)
            {
                _logger.Warning("No files to process");
                return result;
            }

            // Prepare destination directory for Copy and Move operations
            if (operation != FileOperation.Delete && !string.IsNullOrEmpty(destinationDir))
            {
                if (!Directory.Exists(destinationDir))
                {
                    try
                    {
                        if (!options.TestModeEnabled)
                        {
                            Directory.CreateDirectory(destinationDir);
                        }
                        else
                        {
                            _logger.Debug($"TEST MODE: Would create destination directory: {destinationDir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to create destination directory: {destinationDir}");
                        return result;
                    }
                }
            }

            // Pre-create destination directories if needed
            if (options.PreCreateDirectories && operation != FileOperation.Delete)
            {
                if (!options.TestModeEnabled)
                {
                    await PreCreateDestinationDirectoriesAsync(filesList, destinationDir, options, token);
                }
                else
                {
                    _logger.Debug($"TEST MODE: Would pre-create destination directories for {filesList.Count} files");
                }
            }

            if (options.TestModeEnabled)
            {
                _logger.Information($"TEST MODE: Starting {operation} operation for {filesList.Count} files (simulation only)");
            }
            else
            {
                _logger.Information($"Starting {operation} operation for {filesList.Count} files");
            }

            // Process files in parallel with throttling
            using (var throttler = new SemaphoreSlim(options.MaxParallelOperations))
            {
                var tasks = filesList.Select(file => ProcessFileAsync(
                    file, destinationDir, operation, options, throttler, result, token)).ToList();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                    result.WasCancelled = true;
                    _logger.Warning("Operation was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing files");
                }
            }

            // Clean up empty directories if needed
            if (options.DeleteEmptySourceDirectories &&
                (operation == FileOperation.Move || operation == FileOperation.Delete) &&
                !token.IsCancellationRequested)
            {
                if (!options.TestModeEnabled)
                {
                    CleanupEmptyDirectories(filesList, result);
                }
                else
                {
                    _logger.Debug($"TEST MODE: Would clean up empty source directories after {operation}");
                }
            }

            if (options.TestModeEnabled)
            {
                _logger.Information($"TEST MODE: {operation} operation simulation completed. Success: {result.SuccessCount}, " +
                    $"Failed: {result.FailedCount}, Skipped: {result.SkippedCount}");
            }
            else
            {
                _logger.Information($"{operation} operation completed. Success: {result.SuccessCount}, " +
                    $"Failed: {result.FailedCount}, Skipped: {result.SkippedCount}");
            }

            return result;
        }

        /// <summary>
        /// Process a single file with retry logic
        /// </summary>
        private static async Task ProcessFileAsync(
            FileItem file,
            string destinationDir,
            FileOperation operation,
            FileOperationOptions options,
            SemaphoreSlim throttler,
            BatchFileOperationResult result,
            CancellationToken token)
        {
            try
            {
                await throttler.WaitAsync(token);

                try
                {
                    if (token.IsCancellationRequested)
                        return;

                    string destinationPath = GetDestinationPath(file, destinationDir, options);
                    FileOperationResult operationResult;

                    switch (operation)
                    {
                        case FileOperation.Copy:
                            if (!options.TestModeEnabled)
                            {
                                EnsureDirectoryExists(Path.GetDirectoryName(destinationPath));
                            }
                            else
                            {
                                _logger.Debug($"TEST MODE: Would ensure directory exists: {Path.GetDirectoryName(destinationPath)}");
                            }
                            operationResult = await ExecuteWithRetryAsync(
                                () => CopyFile(file.SourcePath, destinationPath, options.Overwrite, options.TestModeEnabled),
                                options.MaxRetries,
                                options.RetryDelayMilliseconds,
                                token);
                            if (operationResult == FileOperationResult.Success && file.Attributes.HasValue && !options.TestModeEnabled)
                            {
                                try { File.SetAttributes(destinationPath, file.Attributes.Value); }
                                catch (Exception ex) { _logger.Error(ex, $"Failed to set attributes on {destinationPath}"); }
                            }
                            else if (operationResult == FileOperationResult.Success && file.Attributes.HasValue && options.TestModeEnabled)
                            {
                                _logger.Debug($"TEST MODE: Would set attributes on {destinationPath}");
                            }
                            break;

                        case FileOperation.Move:
                            if (!options.TestModeEnabled)
                            {
                                EnsureDirectoryExists(Path.GetDirectoryName(destinationPath));
                            }
                            else
                            {
                                _logger.Debug($"TEST MODE: Would ensure directory exists: {Path.GetDirectoryName(destinationPath)}");
                            }
                            operationResult = await ExecuteWithRetryAsync(
                                () => MoveFile(file.SourcePath, destinationPath, options.Overwrite, options.TestModeEnabled),
                                options.MaxRetries,
                                options.RetryDelayMilliseconds,
                                token);
                            if (operationResult == FileOperationResult.Success && file.Attributes.HasValue && !options.TestModeEnabled)
                            {
                                try { File.SetAttributes(destinationPath, file.Attributes.Value); }
                                catch (Exception ex) { _logger.Error(ex, $"Failed to set attributes on {destinationPath}"); }
                            }
                            else if (operationResult == FileOperationResult.Success && file.Attributes.HasValue && options.TestModeEnabled)
                            {
                                _logger.Debug($"TEST MODE: Would set attributes on {destinationPath}");
                            }
                            break;

                        case FileOperation.Delete:
                            operationResult = await ExecuteWithRetryAsync(
                                () => DeleteFile(file.SourcePath, options.TestModeEnabled),
                                options.MaxRetries,
                                options.RetryDelayMilliseconds,
                                token);
                            break;

                        default:
                            _logger.Error($"Unknown operation: {operation}");
                            operationResult = FileOperationResult.Failed;
                            break;
                    }

                    // Update result counts
                    switch (operationResult)
                    {
                        case FileOperationResult.Success:
                            result.IncrementSuccessCount();
                            break;
                        case FileOperationResult.Skipped:
                            result.IncrementSkippedCount();
                            break;
                        case FileOperationResult.Failed:
                            result.IncrementFailedCount();
                            lock (result.FailedFiles) { result.FailedFiles.Add(file.SourcePath); }
                            break;
                    }
                }
                finally
                {
                    throttler.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unhandled exception processing file: {file.SourcePath}");
                result.IncrementFailedCount();
                lock (result.FailedFiles) { result.FailedFiles.Add(file.SourcePath); }
            }
        }

        /// <summary>
        /// Gets the destination path for a file
        /// </summary>
        private static string GetDestinationPath(FileItem file, string destinationDir, FileOperationOptions options)
        {
            // Use custom destination if provided
            if (!string.IsNullOrEmpty(file.CustomDestinationPath))
                return file.CustomDestinationPath;

            // Use relative path if provided
            if (!string.IsNullOrEmpty(file.RelativePath))
                return Path.Combine(destinationDir, file.RelativePath);

            // Use source path with preserved structure
            if (options.PreserveDirectoryStructure && !string.IsNullOrEmpty(options.SourceBaseDir))
            {
                var relativePath = GetRelativePath(file.SourcePath, options.SourceBaseDir);
                return Path.Combine(destinationDir, relativePath);
            }

            // Default: just use the filename
            return Path.Combine(destinationDir, Path.GetFileName(file.SourcePath));
        }

        /// <summary>
        /// Gets the relative path of a file from a base directory
        /// </summary>
        private static string GetRelativePath(string fullPath, string basePath)
        {
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(basePath.Length);

            // For paths with different drive letters
            var driveLetter = Path.GetPathRoot(fullPath).TrimEnd('\\', ':');
            var pathWithoutDrive = fullPath.Substring(Path.GetPathRoot(fullPath).Length);
            return Path.Combine(driveLetter, pathWithoutDrive);
        }

        /// <summary>
        /// Pre-creates destination directories for a batch operation
        /// </summary>
        private static async Task PreCreateDestinationDirectoriesAsync(
    List<FileItem> files,
    string destinationDir,
    FileOperationOptions options,
    CancellationToken token)
        {
            if (!options.PreCreateDirectories || options.TestModeEnabled)
            {
                if (options.TestModeEnabled)
                {
                    _logger.Debug($"TEST MODE: Would pre-create destination directories");
                }
                return;
            }

            var directories = new HashSet<string>();

            foreach (var file in files)
            {
                if (token.IsCancellationRequested)
                    return;

                string destinationPath = GetDestinationPath(file, destinationDir, options);
                string directory = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrEmpty(directory))
                    directories.Add(directory);
            }

            _logger.Debug($"Pre-creating {directories.Count} destination directories");

            foreach (var dir in directories)
            {
                if (token.IsCancellationRequested)
                    return;

                EnsureDirectoryExists(dir);

                if (directories.Count > 100)
                    await Task.Delay(1, token);
            }
        }

        /// <summary>
        /// Ensures a directory exists, creating it if needed
        /// </summary>
        private static void EnsureDirectoryExists(string directory)
        {
            if (string.IsNullOrEmpty(directory) || Directory.Exists(directory))
                return;

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create directory: {directory}");
            }
        }

        /// <summary>
        /// Cleans up empty directories after a file operation
        /// </summary>
        private static void CleanupEmptyDirectories(List<FileItem> files, BatchFileOperationResult result, bool testMode = false)
        {
            if (result.FailedCount > 0)
                return;

            try
            {
                var sourceDirectories = files
                    .Select(f => Path.GetDirectoryName(f.SourcePath))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in sourceDirectories)
                {
                    int deletedCount = DeleteEmptyDirectories(dir, true, testMode);
                    if (deletedCount > 0)
                        _logger.Debug($"{(testMode ? "TEST MODE: Would remove" : "Removed")} {deletedCount} empty directories starting from {dir}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error cleaning up empty directories");
            }
        }

        /// <summary>
        /// Recursively deletes empty directories
        /// </summary>
        public static int DeleteEmptyDirectories(string path, bool preserveRoot = true, bool testMode = false)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return 0;

            int deletedCount = 0;

            try
            {
                // First process all subdirectories
                foreach (var directory in Directory.GetDirectories(path))
                {
                    deletedCount += DeleteEmptyDirectories(directory, false, testMode);
                }

                // Then check if this directory is empty
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    if (!preserveRoot)
                    {
                        if (!testMode)
                        {
                            _logger.Debug($"Deleting empty directory: {path}");
                            Directory.Delete(path);
                        }
                        else
                        {
                            _logger.Debug($"TEST MODE: Would delete empty directory: {path}");
                        }
                        deletedCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error deleting empty directory: {path}");
            }

            return deletedCount;
        }

        /// <summary>
        /// Executes an operation with retry logic for network operations
        /// </summary>
        private static async Task<FileOperationResult> ExecuteWithRetryAsync(
            Func<FileOperationResult> operation,
            int maxRetries,
            int retryDelayMs,
            CancellationToken token)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (token.IsCancellationRequested)
                    return FileOperationResult.Failed;

                try
                {
                    return operation();
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableException(ex))
                {
                    _logger.Warning($"Error in file operation (attempt {attempt}/{maxRetries}): {ex.Message}");
                    await Task.Delay(retryDelayMs * attempt, token);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Error in file operation after {attempt} attempts");
                    return FileOperationResult.Failed;
                }
            }

            return FileOperationResult.Failed;
        }

        /// <summary>
        /// Copies a file with overwrite option
        /// </summary>
        private static FileOperationResult CopyFile(string sourceFile, string destinationFile, bool overwrite, bool testMode = false)
        {
            if (!File.Exists(sourceFile))
            {
                _logger.Warning($"Source file does not exist: {sourceFile}");
                return FileOperationResult.Failed;
            }

            if (File.Exists(destinationFile))
            {
                if (overwrite)
                {
                    try
                    {
                        if (!testMode)
                        {
                            File.Delete(destinationFile);
                        }
                        else
                        {
                            _logger.Debug($"TEST MODE: Would delete existing file: {destinationFile}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to delete existing file: {destinationFile}");
                        return FileOperationResult.Failed;
                    }
                }
                else
                {
                    _logger.Debug($"Skipping file {sourceFile} because destination already exists");
                    return FileOperationResult.Skipped;
                }
            }

            if (!testMode)
            {
                File.Copy(sourceFile, destinationFile, true);
                _logger.Debug($"Copied file: {sourceFile} to {destinationFile}");
            }
            else
            {
                _logger.Debug($"TEST MODE: Would copy file: {sourceFile} to {destinationFile}");
            }
            return FileOperationResult.Success;
        }

        /// <summary>
        /// Moves a file with overwrite option
        /// </summary>
        private static FileOperationResult MoveFile(string sourceFile, string destinationFile, bool overwrite, bool testMode = false)
        {
            if (!File.Exists(sourceFile))
            {
                _logger.Warning($"Source file does not exist: {sourceFile}");
                return FileOperationResult.Failed;
            }

            if (File.Exists(destinationFile))
            {
                if (overwrite)
                {
                    try
                    {
                        if (!testMode)
                        {
                            File.Delete(destinationFile);
                        }
                        else
                        {
                            _logger.Debug($"TEST MODE: Would delete existing file: {destinationFile}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to delete existing file: {destinationFile}");
                        return FileOperationResult.Failed;
                    }
                }
                else
                {
                    _logger.Debug($"Skipping file {sourceFile} because destination already exists");
                    return FileOperationResult.Skipped;
                }
            }

            // Check if source and destination are on the same volume
            string sourceDrive = Path.GetPathRoot(sourceFile);
            string destDrive = Path.GetPathRoot(destinationFile);

            if (!testMode)
            {
                if (string.Equals(sourceDrive, destDrive, StringComparison.OrdinalIgnoreCase))
                {
                    // Same volume, use Move (more efficient)
                    File.Move(sourceFile, destinationFile);
                }
                else
                {
                    // Different volumes, use Copy + Delete
                    File.Copy(sourceFile, destinationFile, true);
                    File.Delete(sourceFile);
                }
                _logger.Debug($"Moved file: {sourceFile} to {destinationFile}");
            }
            else
            {
                if (string.Equals(sourceDrive, destDrive, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Debug($"TEST MODE: Would move file: {sourceFile} to {destinationFile}");
                }
                else
                {
                    _logger.Debug($"TEST MODE: Would copy file: {sourceFile} to {destinationFile} and delete source");
                }
            }
            return FileOperationResult.Success;
        }

        /// <summary>
        /// Deletes a file
        /// </summary>
        private static FileOperationResult DeleteFile(string filePath, bool testMode = false)
        {
            if (!File.Exists(filePath))
            {
                _logger.Debug($"File does not exist, skipping deletion: {filePath}");
                return FileOperationResult.Skipped;
            }

            if (!testMode)
            {
                File.Delete(filePath);
                _deletionLogger.Information($"Deleted file: {filePath}");
                _logger.Debug($"Deleted file: {filePath}");
            }
            else
            {
                _logger.Debug($"TEST MODE: Would delete file: {filePath}");
            }
            return FileOperationResult.Success;
        }

        /// <summary>
        /// Determines if an exception is retryable (network issues, file in use, etc.)
        /// </summary>
        private static bool IsRetryableException(Exception ex)
        {
            if (ex is IOException || ex is UnauthorizedAccessException)
                return true;

            string message = ex.Message.ToLowerInvariant();
            return message.Contains("access denied") ||
                   message.Contains("being used by another process") ||
                   message.Contains("network") ||
                   message.Contains("another process");
        }

        #endregion
    }
}
