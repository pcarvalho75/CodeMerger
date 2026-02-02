using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages .bak backup file cleanup and statistics.
    /// </summary>
    public class BackupCleanupService
    {
        public class BackupStatistics
        {
            public int TotalCount { get; set; }
            public long TotalSize { get; set; }
            public DateTime? OldestBackup { get; set; }
            public Dictionary<string, (int Count, long Size)> ByDirectory { get; set; } = new();

            public double TotalSizeMB => TotalSize / (1024.0 * 1024.0);
        }

        /// <summary>
        /// Get statistics about .bak files in the given directories.
        /// </summary>
        public BackupStatistics GetStatistics(List<string> directories)
        {
            var stats = new BackupStatistics();

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    var bakFiles = Directory.GetFiles(dir, "*.bak", SearchOption.AllDirectories);
                    int dirCount = bakFiles.Length;
                    long dirSize = 0;

                    foreach (var bakFile in bakFiles)
                    {
                        var fi = new FileInfo(bakFile);
                        dirSize += fi.Length;

                        if (stats.OldestBackup == null || fi.LastWriteTime < stats.OldestBackup)
                            stats.OldestBackup = fi.LastWriteTime;
                    }

                    stats.TotalCount += dirCount;
                    stats.TotalSize += dirSize;

                    if (dirCount > 0)
                    {
                        var dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                        stats.ByDirectory[dirName] = (dirCount, dirSize);
                    }
                }
                catch { }
            }

            return stats;
        }

        /// <summary>
        /// Delete all .bak files in the given directories.
        /// Returns (deleted count, bytes freed).
        /// </summary>
        public (int Deleted, long BytesFreed) CleanupAll(List<string> directories)
        {
            int deleted = 0;
            long bytesFreed = 0;

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var bakFile in Directory.GetFiles(dir, "*.bak", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(bakFile);
                            bytesFreed += fi.Length;
                            File.Delete(bakFile);
                            deleted++;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return (deleted, bytesFreed);
        }

        /// <summary>
        /// Delete .bak files older than the specified number of hours.
        /// Returns (deleted count, bytes freed).
        /// </summary>
        public (int Deleted, long BytesFreed) CleanupOldBackups(List<string> directories, int maxAgeHours)
        {
            int deleted = 0;
            long bytesFreed = 0;
            var cutoff = DateTime.Now.AddHours(-maxAgeHours);

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var bakFile in Directory.GetFiles(dir, "*.bak", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(bakFile);
                            if (fi.LastWriteTime < cutoff)
                            {
                                bytesFreed += fi.Length;
                                File.Delete(bakFile);
                                deleted++;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return (deleted, bytesFreed);
        }

        /// <summary>
        /// Keep only the N most recent .bak files per original file.
        /// Returns (deleted count, bytes freed).
        /// </summary>
        public (int Deleted, long BytesFreed) EnforceMaxBackupsPerFile(List<string> directories, int maxBackups)
        {
            int deleted = 0;
            long bytesFreed = 0;

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    // Group .bak files by their original file (strip .bak extension)
                    var bakFiles = Directory.GetFiles(dir, "*.bak", SearchOption.AllDirectories);
                    var grouped = bakFiles
                        .Select(f => new FileInfo(f))
                        .GroupBy(fi => fi.FullName.Substring(0, fi.FullName.Length - 4)); // Remove .bak

                    foreach (var group in grouped)
                    {
                        var excess = group.OrderByDescending(fi => fi.LastWriteTime)
                            .Skip(maxBackups)
                            .ToList();

                        foreach (var fi in excess)
                        {
                            try
                            {
                                bytesFreed += fi.Length;
                                fi.Delete();
                                deleted++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return (deleted, bytesFreed);
        }

        /// <summary>
        /// Run auto-cleanup based on workspace settings.
        /// Combines age-based and max-per-file cleanup.
        /// Returns total (deleted count, bytes freed).
        /// </summary>
        public (int Deleted, long BytesFreed) RunAutoCleanup(List<string> directories, WorkspaceSettings settings)
        {
            int totalDeleted = 0;
            long totalFreed = 0;

            // Age-based cleanup
            var (ageDeleted, ageFreed) = CleanupOldBackups(directories, settings.BackupRetentionHours);
            totalDeleted += ageDeleted;
            totalFreed += ageFreed;

            // Max-per-file cleanup
            var (maxDeleted, maxFreed) = EnforceMaxBackupsPerFile(directories, settings.MaxBackupsPerFile);
            totalDeleted += maxDeleted;
            totalFreed += maxFreed;

            return (totalDeleted, totalFreed);
        }
    }
}
