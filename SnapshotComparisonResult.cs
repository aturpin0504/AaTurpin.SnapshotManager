using System.Collections.Generic;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Represents the comparison result between two snapshots
    /// </summary>
    public class SnapshotComparisonResult
    {
        public List<FileSnapshot> AddedFiles { get; set; } = new List<FileSnapshot>();
        public List<FileSnapshot> ModifiedFiles { get; set; } = new List<FileSnapshot>();
        public List<FileSnapshot> DeletedFiles { get; set; } = new List<FileSnapshot>();
        public int TotalChanges => AddedFiles.Count + ModifiedFiles.Count + DeletedFiles.Count;

        public override string ToString()
        {
            return $"Added: {AddedFiles.Count}, Modified: {ModifiedFiles.Count}, Deleted: {DeletedFiles.Count}";
        }
    }
}
