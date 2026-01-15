using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Result of a file scan operation.
    /// </summary>
    public class ScanResult
    {
        public List<string> Files { get; set; } = new();
        public int EstimatedTokens { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Scans directories for source files based on extension and ignore filters.
    /// </summary>
    public class FileScannerService
    {
        /// <summary>
        /// Fired during scanning with status messages.
        /// </summary>
        public event Action<string>? OnProgress;

        /// <summary>
        /// Parse extension filter string into list.
        /// </summary>
        public static List<string> ParseExtensions(string extensionsText)
        {
            return extensionsText
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => ext.Trim())
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToList();
        }

        /// <summary>
        /// Parse ignored directories string into set.
        /// </summary>
        public static HashSet<string> ParseIgnoredDirs(string ignoredDirsText)
        {
            var input = ignoredDirsText + ",.git";
            return input
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => dir.Trim().ToLowerInvariant())
                .ToHashSet();
        }

        /// <summary>
        /// Scan directories and repositories for matching files.
        /// </summary>
        public async Task<ScanResult> ScanAsync(
            IEnumerable<string> directories,
            IEnumerable<ExternalRepository>? repositories,
            GitService? gitService,
            string extensionsText,
            string ignoredDirsText,
            CancellationToken ct = default)
        {
            var extensions = ParseExtensions(extensionsText);
            var ignoredDirs = ParseIgnoredDirs(ignoredDirsText);

            var allFiles = new List<string>();

            await Task.Run(() =>
            {
                // Scan local directories
                foreach (var dir in directories)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!Directory.Exists(dir)) continue;

                    OnProgress?.Invoke($"Scanning {Path.GetFileName(dir)}...");

                    try
                    {
                        var files = ScanDirectory(dir, extensions, ignoredDirs);
                        allFiles.AddRange(files);
                    }
                    catch (Exception ex)
                    {
                        OnProgress?.Invoke($"Error scanning {dir}: {ex.Message}");
                    }
                }

                // Scan git repositories
                if (gitService != null && repositories != null)
                {
                    foreach (var repo in repositories.Where(r => r.IsEnabled))
                    {
                        if (ct.IsCancellationRequested) break;

                        OnProgress?.Invoke($"Scanning {repo.Name}...");

                        try
                        {
                            var repoFiles = gitService.GetRepositoryFiles(repo, extensionsText, ignoredDirsText);
                            allFiles.AddRange(repoFiles);
                        }
                        catch (Exception ex)
                        {
                            OnProgress?.Invoke($"Error scanning {repo.Name}: {ex.Message}");
                        }
                    }
                }
            }, ct);

            // Deduplicate and sort
            var distinctFiles = allFiles.Distinct().OrderBy(f => f).ToList();

            // Calculate size
            long totalBytes = 0;
            foreach (var file in distinctFiles)
            {
                try { totalBytes += new FileInfo(file).Length; }
                catch { }
            }

            return new ScanResult
            {
                Files = distinctFiles,
                TotalBytes = totalBytes,
                EstimatedTokens = (int)(totalBytes / 4)
            };
        }

        /// <summary>
        /// Scan a single directory for matching files.
        /// </summary>
        public IEnumerable<string> ScanDirectory(string directory, List<string> extensions, HashSet<string> ignoredDirs)
        {
            if (!Directory.Exists(directory))
                return Enumerable.Empty<string>();

            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file =>
                {
                    // Check ignored directories
                    var pathParts = file.Split(Path.DirectorySeparatorChar);
                    if (pathParts.Any(part => ignoredDirs.Contains(part.ToLowerInvariant())))
                        return false;

                    // Check extension
                    var fileExtension = Path.GetExtension(file);
                    if (extensions.Count == 0 || extensions.Contains("*.*"))
                        return true;

                    return extensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
                });
        }
    }
}
