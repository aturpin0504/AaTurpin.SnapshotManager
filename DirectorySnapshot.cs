using System;
using System.Collections.Generic;
using System.Linq;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Represents a collection of file snapshots for a set of directories with improved
    /// handling of inaccessible directories
    /// </summary>
    [Serializable]
    public class DirectorySnapshot
    {
        public DateTime SnapshotTime { get; set; }
        public Dictionary<string, List<FileSnapshot>> Directories { get; set; }
        public Dictionary<string, HashSet<string>> ExcludedPaths { get; set; }

        // Property to track directories that were inaccessible during snapshot
        public HashSet<string> InaccessibleDirectories { get; set; }

        public int TotalFileCount => Directories.Values.Sum(list => list.Count);

        public DirectorySnapshot()
        {
            SnapshotTime = DateTime.Now;
            Directories = new Dictionary<string, List<FileSnapshot>>(StringComparer.OrdinalIgnoreCase);
            ExcludedPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            InaccessibleDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
