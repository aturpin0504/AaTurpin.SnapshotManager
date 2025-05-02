# SnapshotManager

A robust .NET library for managing file snapshots across network locations with special focus on reliability and error recovery.

## Features

- **Network-Aware File Snapshot Management**: Create snapshots of files and directories optimized for network environments
- **Robust Error Handling**: Automatic recovery from network issues, timeouts, and incomplete snapshots
- **Parallel Processing**: Efficient multi-threaded file operations with configurable concurrency limits
- **Reliable Comparison**: Intelligent delta detection even with incomplete or partially successful snapshots
- **Configurable Timeout Handling**: Fine-tune network operation timeouts for different environments
- **Historical Tracking**: Track successful snapshots over time to validate future snapshot completeness

## Installation

Install via NuGet Package Manager:

```
Install-Package AaTurpin.SnapshotManager
```

Or via .NET CLI:

```
dotnet add package AaTurpin.SnapshotManager
```

## Quick Start

```csharp
using AaTurpin.SnapshotManager;
using ConfigManager;
using System.Threading;
using System.Threading.Tasks;

// Create a list of directories to snapshot
var directories = ConfigHelper.GetDirectories();

// Create a snapshot of the directories
var snapshot = await SnapshotManager.CreateSnapshotAsync(
    directories,
    maxParallelOperations: 8,
    token: CancellationToken.None);

// Store snapshot for later comparison
// ...

// Later, create a new snapshot and compare
var newSnapshot = await SnapshotManager.CreateSnapshotAsync(directories);

// Compare snapshots to find differences
var syncOps = SnapshotManager.CompareSnapshots(
    snapshot, 
    newSnapshot,
    ignoreDeletesIfNewSnapshotIncomplete: true);

// Process sync operations
Console.WriteLine(syncOps.GetSummary());
// Files to add: 10, Files to update: 5, Files to delete: 2 (Comparison is reliable)

// Get file items for synchronization
var filesToSync = syncOps.GetFileItemsForSync();
// Use NetworkFileManager to process these files
```

## Key Components

### SnapshotManager

The main static class that provides methods for creating and comparing snapshots.

```csharp
// Create a snapshot with retry logic
var snapshot = await SnapshotManager.CreateSnapshotWithRetryAsync(
    directories,
    maxAttempts: 3,
    retryDelay: TimeSpan.FromMinutes(1));

// Configure network timeouts
SnapshotManager.ConfigureNetworkTimeouts(
    readTimeoutSeconds: 30,
    writeTimeoutSeconds: 60,
    connectTimeoutSeconds: 15);

// Get last successful snapshot
var lastSnapshot = SnapshotManager.GetLastSuccessfulSnapshot(rootPath);
```

### FileSnapshot

Represents metadata for a single file.

```csharp
// Create from an existing file
var fileSnapshot = FileSnapshot.FromFile(@"C:\path\to\file.txt");

// Access properties
Console.WriteLine($"Path: {fileSnapshot.MappedPath}");
Console.WriteLine($"Size: {fileSnapshot.FileSize} bytes");
Console.WriteLine($"Last Modified: {fileSnapshot.LastWriteTime}");
Console.WriteLine($"Relative Path: {fileSnapshot.RelativePath}");
```

### DirectorySnapshot

Contains collections of file snapshots for entire directories.

```csharp
// Create a snapshot manually
var dirSnapshot = DirectorySnapshot.CreateSnapshot(@"C:\path\to\directory");

// Merge two snapshots
var mergedSnapshot = snapshot1.MergeWith(snapshot2);

// Compare snapshots
var syncOps = snapshot1.CompareWith(snapshot2);
```

### SyncOperations

Stores the results of snapshot comparisons.

```csharp
// Get a summary of sync operations
string summary = syncOps.GetSummary();

// Check if changes exist
if (syncOps.HasChanges)
{
    // Get reliable operations only (for incomplete snapshots)
    var reliableOps = syncOps.GetReliableOperationsOnly();
    
    // Convert to FileItem objects for use with NetworkFileManager
    var fileItems = syncOps.GetFileItemsForSync();
}
```

## Advanced Usage

### Recovery from Network Issues

```csharp
// Attempt to recover an incomplete snapshot
var recoveredSnapshot = await SnapshotManager.SnapshotRecovery.RecoverSnapshotAsync(
    incompleteSnapshot,
    maxParallelOperations: 4);
```

### Safe Comparison with Historical Data

```csharp
// Compare with reference to historical snapshots
var safeCompare = SnapshotManager.SafeCompareSnapshots(
    oldSnapshot,
    newSnapshot,
    previousSuccessfulSnapshot);
```

## Requirements

- .NET Framework 4.7.2 or higher
- Dependencies:
  - AaTurpin.ConfigManager
  - RunLog

## License

[MIT License](LICENSE)