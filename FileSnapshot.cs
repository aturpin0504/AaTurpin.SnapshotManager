using System;
using System.IO;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Represents a file in a snapshot including its path, size, last write time, and attributes
    /// </summary>
    [Serializable]
    public class FileSnapshot
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public FileAttributes Attributes { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is FileSnapshot other)
            {
                return string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                       Size == other.Size &&
                       LastWriteTime == other.LastWriteTime &&
                       Attributes == other.Attributes;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (RelativePath != null) ? RelativePath.GetHashCode() : 0;
        }
    }
}
