# AaTurpin.SnapshotManager

A robust .NET Framework 4.7.2 library for creating, comparing, and managing directory snapshots with advanced network reliability and error recovery capabilities.

## Overview

SnapshotManager is a powerful utility for tracking file changes across directories and network drives with a focus on enterprise-grade reliability. It provides functionality to:

- Create comprehensive snapshots of directory trees with detailed file metadata
- Compare snapshots to identify added, modified, and deleted files with precision
- Perform reliable file operations (copy, move, delete) across network locations
- Handle network path errors with automatic retry mechanisms and fallback options
- Process files in parallel with configurable throttling for optimal performance
- Track and gracefully handle inaccessible directories and subdirectories
- Provide detailed logging with customizable levels and structured output

## Key Dependencies

This library depends on the following NuGet packages:
- **AaTurpin.ConfigManager (v1.2.0)**: Configuration management with XML support
- **RunLog (v3.3.0)**: Advanced logging framework with file rotation and console output

## Key Features

### Network-First Design
- **Intelligent Network Handling**: Automatic detection and handling of network interruptions
- **Smart Retry Logic**: Configurable retry mechanisms with exponential backoff
- **Fallback Strategies**: Multiple approaches for handling inaccessible paths
- **Drive Mapping Integration**: Seamless integration with network drive mappings

### Performance Optimization
- **Parallel Processing**: Configurable parallel operations with intelligent throttling
- **Buffered File Operations**: Optional buffering with background processing for improved performance
- **Efficient Memory Usage**: Streaming operations to handle large directory structures
- **Selective Processing**: Advanced exclusion patterns to skip unnecessary files

### Enterprise Features
- **Comprehensive Logging**: Multiple log levels with structured output
- **Configuration Management**: Flexible XML-based configuration with runtime updates
- **Test Mode**: Safe validation of operations without actual file modifications
- **Snapshot Retention**: Automatic cleanup of old snapshots with configurable policies

## Quick Start

### 1. Create Your First Snapshot

```csharp
using AaTurpin.SnapshotManager;
using ConfigManager;
using RunLog;
using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        // Configure logging with multiple outputs
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(LogLevel.Information)
            .WriteTo.File("snapshots.log", LogLevel.Debug, 
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Define directories to monitor
        var directories = new List<DirectoryConfig>
        {
            new DirectoryConfig 
            { 
                Path = @"C:\ImportantFiles",
                Exclusions = new List<string> { "temp", "cache", "logs" }
            },
            new DirectoryConfig { Path = @"D:\Documents" }
        };

        // Create snapshot with network-optimized settings
        var snapshot = SnapshotManager.CreateSnapshot(directories, maxParallelOperations: 6);

        Console.WriteLine($"Snapshot created:");
        Console.WriteLine($"  Files: {snapshot.TotalFileCount}");
        Console.WriteLine($"  Directories: {snapshot.Directories.Count}");
        Console.WriteLine($"  Inaccessible: {snapshot.InaccessibleDirectories.Count}");

        // Save snapshot with timestamp
        string snapshotFile = SnapshotManager.SaveSnapshot(snapshot, @"C:\Snapshots");
        Console.WriteLine($"Saved to: {snapshotFile}");
    }
}
```

### 2. Compare Snapshots and Backup Changes

```csharp
using AaTurpin.SnapshotManager;
using RunLog;
using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        // Set up dual logging - main log and deletion tracking
        var mainLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File("operations.log", LogLevel.Debug)
            .CreateLogger();
            
        var deletionLogger = new LoggerConfiguration()
            .WriteTo.File("deletions.log", LogLevel.Information)
            .CreateLogger();

        // Configure all components
        Log.Logger = mainLogger;
        SnapshotManager.SetLoggers(mainLogger, deletionLogger);
        NetworkFileManager.SetLoggers(mainLogger, deletionLogger);

        // Load snapshots
        var oldSnapshot = SnapshotManager.LoadSnapshot(@"C:\Snapshots\before.json");
        var newSnapshot = SnapshotManager.LoadSnapshot(@"C:\Snapshots\after.json");

        // Compare and identify changes
        var comparison = SnapshotManager.CompareSnapshots(oldSnapshot, newSnapshot);
        
        Console.WriteLine($"Changes detected:");
        Console.WriteLine($"  Added: {comparison.AddedFiles.Count}");
        Console.WriteLine($"  Modified: {comparison.ModifiedFiles.Count}");
        Console.WriteLine($"  Deleted: {comparison.DeletedFiles.Count}");

        if (comparison.TotalChanges > 0)
        {
            // Configure backup operation
            var options = new FileOperationOptions
            {
                MaxParallelOperations = 8,
                Overwrite = true,
                PreserveDirectoryStructure = true,
                MaxRetries = 3,
                RetryDelayMilliseconds = 1000,
                TestModeEnabled = false // Set to true for dry run
            };

            // Backup changed files
            var result = await NetworkFileManager.CopyNetworkFilesAsync(
                comparison,
                @"E:\Backups\Incremental",
                options,
                CancellationToken.None);

            Console.WriteLine($"Backup completed: {result.SuccessCount} files");
        }
    }
}
```

## Configuration Management

### XML Configuration (App.config)

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <configSections>
    <section name="directories" type="DummyType, DummyAssembly" />
    <section name="driveMappings" type="DummyType, DummyAssembly" />
  </configSections>
  
  <appSettings>
    <add key="SnapshotDirectory" value="C:\Snapshots" />
    <add key="BackupDirectory" value="E:\Backups" />
    <add key="MaxParallelOperations" value="8" />
    <add key="RetainedSnapshotCount" value="30" />
  </appSettings>
  
  <directories>
    <directory path="C:\ImportantFiles">
      <exclude>temp</exclude>
      <exclude>cache</exclude>
      <exclude>*.tmp</exclude>
    </directory>
    <directory path="\\server\share\documents">
      <exclude>draft</exclude>
    </directory>
  </directories>
  
  <driveMappings>
    <mapping driveLetter="V:" uncPath="\\fileserver\data" />
    <mapping driveLetter="W:" uncPath="\\backup-server\archives" />
  </driveMappings>
</configuration>
```

### Reading Configuration

```csharp
using ConfigManager;

// Set logger for config operations
ConfigHelper.SetLogger(Log.Logger);

// Read typed values with defaults
int maxOps = ConfigHelper.GetValue<int>("MaxParallelOperations", 4);
string backupDir = ConfigHelper.GetValue<string>("BackupDirectory", @"C:\Backups");

// Get directory configurations
var directories = ConfigHelper.GetDirectories();
var driveMappings = ConfigHelper.GetDriveMappings();

// Add new directory at runtime
var newDir = new DirectoryConfig
{
    Path = @"D:\NewArea",
    Exclusions = new List<string> { "temp", "logs" }
};
ConfigHelper.AddDirectory(newDir);
```

## Advanced Features

### Test Mode Operation

Validate operations without making actual changes:

```csharp
var options = new FileOperationOptions
{
    TestModeEnabled = true,  // No actual file operations
    MaxParallelOperations = 4,
    Overwrite = true
};

// This will log all operations that would be performed
var result = await NetworkFileManager.CopyNetworkFilesAsync(
    comparison, 
    @"E:\Backups\Test", 
    options,
    token);

Console.WriteLine($"Test mode: Would process {result.SuccessCount} files");
```

### Custom JSON Serialization

Built-in JSON serialization without external dependencies:

```csharp
// Serialize custom objects
string json = SimpleJsonSerializer.Serialize(snapshot, indented: true);

// Deserialize with type safety
var loadedSnapshot = SimpleJsonSerializer.Deserialize<DirectorySnapshot>(json);
```

### Smart Directory Processing

Handle complex directory structures:

```csharp
// Process with exclusions and error recovery
var snapshot = SnapshotManager.CreateSnapshot(directories);

// Check for inaccessible areas
foreach (var inaccessibleDir in snapshot.InaccessibleDirectories)
{
    Console.WriteLine($"Could not access: {inaccessibleDir}");
}

// Intelligent retry for network issues
var options = new FileOperationOptions
{
    MaxRetries = 5,
    RetryDelayMilliseconds = 2000,
    MaxParallelOperations = 4
};
```

### Parallel Processing Control

Fine-tune performance for your environment:

```csharp
// Create snapshot with controlled parallelism
var snapshot = SnapshotManager.CreateSnapshot(directories, maxParallelOperations: 8);

// Configure file operations parallelism
var options = new FileOperationOptions
{
    MaxParallelOperations = 6,  // Limit concurrent operations
    PreCreateDirectories = true,  // Batch directory creation
    DeleteEmptySourceDirectories = true  // Clean up after moves
};
```

## Logging Configuration

### Multiple Output Targets

```csharp
// Configure comprehensive logging
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(LogLevel.Information)
    .WriteTo.File("app.log", LogLevel.Debug, 
        rollingInterval: RollingInterval.Hour,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        retainedFileCountLimit: 24)
    .WriteTo.File("errors.log", LogLevel.Error)
    .Enrich("Component", "SnapshotManager");

Log.Logger = logConfig.CreateLogger();
```

### Structured Logging

```csharp
// Use structured logging with properties
Log.Information("Snapshot created with {FileCount} files in {DirectoryCount} directories", 
    snapshot.TotalFileCount, snapshot.Directories.Count);

// Log exceptions with context
try
{
    var snapshot = SnapshotManager.CreateSnapshot(directories);
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to create snapshot for {DirectoryCount} directories", 
        directories.Count);
}
```

## Windows Service Implementation

Deploy as a Windows Service for automated operations:

```csharp
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

public class SnapshotService : ServiceBase
{
    private CancellationTokenSource _cancellationSource;
    private Task _serviceTask;

    protected override void OnStart(string[] args)
    {
        // Configure logging for service
        var logConfig = new LoggerConfiguration()
            .WriteTo.File(@"C:\Logs\SnapshotService.log", LogLevel.Information,
                rollingInterval: RollingInterval.Day);
        
        Log.Logger = logConfig.CreateLogger();
        
        _cancellationSource = new CancellationTokenSource();
        _serviceTask = RunServiceAsync(_cancellationSource.Token);
    }

    protected override void OnStop()
    {
        _cancellationSource.Cancel();
        _serviceTask.Wait(TimeSpan.FromSeconds(30));
        Log.CloseAndFlush();
    }

    private async Task RunServiceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Get configuration
                var directories = ConfigHelper.GetDirectories();
                
                // Create snapshot
                var snapshot = SnapshotManager.CreateSnapshot(directories);
                var snapshotFile = SnapshotManager.SaveSnapshot(snapshot, 
                    ConfigHelper.GetValue<string>("SnapshotDirectory"));

                Log.Information("Service snapshot completed: {SnapshotFile}", snapshotFile);
                
                // Wait for next cycle
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in service cycle");
                await Task.Delay(TimeSpan.FromMinutes(30), token);
            }
        }
    }
}
```

## Performance Considerations

### Memory Management

- Uses streaming operations to handle large directory structures
- Implements efficient concurrent collections for thread-safe operations
- Automatic buffer management for file operations

### Network Optimization

- Intelligent retry logic with exponential backoff
- Connection pooling for multiple network operations
- Fallback mechanisms for unreliable network connections

### Disk I/O Optimization

- Optional buffering with configurable flush intervals
- Background processing for non-blocking operations
- Efficient directory traversal algorithms

## Error Handling Strategies

### Network Reliability

```csharp
// Automatic retry with smart detection
private static bool IsRetryableException(Exception ex)
{
    if (ex is IOException || ex is UnauthorizedAccessException)
        return true;
        
    string message = ex.Message.ToLowerInvariant();
    return message.Contains("network path was not found") ||
           message.Contains("network name is no longer available") ||
           message.Contains("network path cannot be accessed");
}
```

### Graceful Degradation

- Continue processing when individual directories are inaccessible
- Partial snapshots with clear reporting of limitations
- Smart fallback for different enumeration strategies

## Command Line Examples

### Batch File for Regular Snapshots

```batch
@echo off
rem Daily snapshot batch file

cd /d "C:\Program Files\SnapshotManager"

echo Starting daily snapshot...
SnapshotManager.exe -create -config "snapshot.config" -output "C:\Snapshots\Daily"

if %ERRORLEVEL% EQU 0 (
    echo Snapshot completed successfully
) else (
    echo Error: Snapshot failed with code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)
```

### PowerShell Integration

```powershell
# PowerShell script for automated backups
param(
    [string]$ConfigPath = "C:\Config\snapshot.config",
    [string]$OutputPath = "C:\Snapshots",
    [int]$RetentionDays = 30
)

# Load .NET assemblies
Add-Type -Path "C:\Program Files\SnapshotManager\AaTurpin.SnapshotManager.dll"
Add-Type -Path "C:\Program Files\SnapshotManager\RunLog.dll"

# Configure logging
$logConfig = New-Object RunLog.LoggerConfiguration
$logConfig = $logConfig.WriteTo.File("snapshot.log", [RunLog.LogLevel]::Information)
[RunLog.Log]::Logger = $logConfig.CreateLogger()

# Perform snapshot operation
try {
    Write-Output "Creating snapshot..."
    # Your snapshot logic here
    Write-Output "Snapshot completed successfully"
}
catch {
    Write-Error "Snapshot failed: $($_.Exception.Message)"
    exit 1
}
```

## Troubleshooting Guide

### Common Issues and Solutions

**Issue: Files missing from snapshot**
- Check exclusion patterns in configuration
- Verify directory access permissions
- Review logs for access denied errors
- Ensure files aren't locked during snapshot

**Issue: Network timeouts during operations**
- Increase retry count and delay
- Reduce parallel operations
- Check network stability
- Verify network drive mappings

**Issue: High memory usage**
- Reduce parallel operations count
- Enable buffering with smaller buffer sizes
- Process directories in smaller batches
- Check for memory leaks in custom code

### Diagnostic Information

```csharp
// Get diagnostic information
var diagnostics = new
{
    Version = Assembly.GetAssembly(typeof(SnapshotManager)).GetName().Version,
    Runtime = Environment.Version,
    MachineName = Environment.MachineName,
    UserName = Environment.UserName,
    WorkingSet = Environment.WorkingSet,
    ProcessorCount = Environment.ProcessorCount
};

Log.Information("Diagnostics: {@Diagnostics}", diagnostics);
```

### Debug Logging

Enable verbose logging for troubleshooting:

```csharp
// Maximum verbosity
var debugConfig = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Console(LogLevel.Verbose)
    .WriteTo.File("debug.log", LogLevel.Verbose);

Log.Logger = debugConfig.CreateLogger();
```

## API Reference Summary

### Core Classes

#### SnapshotManager
Static class providing snapshot operations:
- `CreateSnapshot()` - Create directory snapshots
- `CompareSnapshots()` - Compare two snapshots
- `SaveSnapshot()` / `LoadSnapshot()` - Persist snapshots
- `SetLoggers()` - Configure logging

#### NetworkFileManager
Static class for network-aware file operations:
- `CopyNetworkFilesAsync()` - Copy files with retry logic
- `RestoreFilesToNetworkAsync()` - Restore from backups
- `DeleteNetworkFilesFromLogAsync()` - Clean up based on logs
- `SetLoggers()` - Configure logging

#### ConfigHelper
Configuration management utilities:
- `GetValue<T>()` - Type-safe config reading
- `SetValue<T>()` - Update configuration
- `GetDirectories()` / `AddDirectory()` - Manage directory configs
- `GetDriveMappings()` / `AddDriveMapping()` - Manage drive mappings

### Data Classes

#### DirectorySnapshot
Represents a point-in-time snapshot:
- `SnapshotTime` - When snapshot was created
- `Directories` - Dictionary of directories and their files
- `ExcludedPaths` - Patterns excluded from snapshot
- `InaccessibleDirectories` - Directories that couldn't be accessed
- `TotalFileCount` - Total number of files in snapshot

#### SnapshotComparisonResult
Results of comparing two snapshots:
- `AddedFiles` - Files present in new but not old snapshot
- `ModifiedFiles` - Files changed between snapshots
- `DeletedFiles` - Files present in old but not new snapshot
- `TotalChanges` - Sum of all changes

#### FileOperationOptions
Configuration for file operations:
- `MaxParallelOperations` - Concurrency limit
- `Overwrite` - Whether to overwrite existing files
- `MaxRetries` - Number of retry attempts
- `TestModeEnabled` - Run without making changes
- `PreserveDirectoryStructure` - Maintain folder hierarchy

## Best Practices

### Performance
1. **Optimize Parallel Operations**: Start with 4-8 parallel operations and adjust based on system performance
2. **Use Exclusions Wisely**: Exclude temporary and cache directories to improve performance
3. **Enable Buffering**: For large files, enable buffering to improve write performance
4. **Batch Directory Operations**: Use `PreCreateDirectories` option for large copy operations

### Reliability
1. **Always Configure Logging**: Use multiple log targets for different severity levels
2. **Implement Error Handling**: Catch and log exceptions appropriately
3. **Use Test Mode**: Validate complex operations before execution
4. **Monitor Network Health**: Implement network connectivity checks before operations

### Security
1. **Limit Access**: Run with minimal required permissions
2. **Secure Credentials**: Use integrated authentication for network resources
3. **Validate Inputs**: Always validate file paths and configuration values
4. **Log Security Events**: Track access patterns and failures

### Maintenance
1. **Regular Cleanup**: Implement automatic cleanup of old snapshots
2. **Monitor Disk Space**: Ensure adequate space for snapshots and logs
3. **Review Exclusions**: Regularly update exclusion patterns
4. **Backup Configurations**: Include configurations in your backup strategy

## Version History

- **v1.0.0** (Current)
  - Initial release with core snapshot functionality
  - Network-aware file operations with retry logic
  - Comprehensive logging and configuration management
  - Test mode for safe operation validation
  - Built-in JSON serialization
  - Parallel processing with configurable limits

## Support and Contributing

### Getting Help
- Review the API documentation and examples
- Check the troubleshooting guide for common issues
- Enable verbose logging for detailed error information

### Reporting Issues
When reporting issues, please include:
- Version of AaTurpin.SnapshotManager
- .NET Framework version
- Relevant configuration files
- Log files with debug level enabled
- Steps to reproduce the issue

## License

This library is released under the MIT License. See LICENSE file for details.

---

**Note**: This library is designed for .NET Framework 4.7.2 and requires Windows environment for full functionality. Network drive features require appropriate permissions and network access.