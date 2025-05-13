using System;
using System.IO;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Represents a file to be processed in file operations
    /// </summary>
    public class FileItem
    {
        public string SourcePath { get; set; }
        public string RelativePath { get; set; }
        public string CustomDestinationPath { get; set; }
        public FileAttributes? Attributes { get; set; }

        public FileItem(string sourcePath)
        {
            SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        }

        public FileItem(string sourcePath, string relativePath)
            : this(sourcePath)
        {
            RelativePath = relativePath;
        }
    }
}
