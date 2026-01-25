using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Shared file path resolution for MCP tools. Handles merged workspaces,
    /// project prefixes, and provides detailed error messages.
    /// </summary>
    public class FilePathResolver
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly List<string> _inputDirectories;

        public FilePathResolver(WorkspaceAnalysis workspaceAnalysis, List<string> inputDirectories)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _inputDirectories = inputDirectories ?? new List<string>();
        }

        /// <summary>
        /// Finds a file by path with disambiguation when multiple files match.
        /// Supports project-prefixed paths (e.g., "CryptoTraderCore/Exchange/File.cs").
        /// Returns (file, null) on success, (null, errorMessage) on failure.
        /// </summary>
        public (FileAnalysis? file, string? error) FindFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return (null, "Error: Path cannot be empty.");

            var normalizedPath = path.Replace('\\', '/');

            // Strategy 1: Exact match on RelativePath
            var matches = _workspaceAnalysis.AllFiles.Where(f =>
                f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.Replace('\\', '/').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();

            // Strategy 2: Match by filename only (if no path separators)
            if (matches.Count == 0 && !path.Contains('/') && !path.Contains('\\'))
            {
                matches = _workspaceAnalysis.AllFiles.Where(f =>
                    f.FileName.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Strategy 3: Project-prefixed path (e.g., "CryptoTraderCore/Exchange/File.cs")
            if (matches.Count == 0 && (path.Contains("/") || path.Contains("\\")))
            {
                matches = TryMatchProjectPrefixedPath(normalizedPath);
            }

            // Strategy 4: Resolve relative paths with ../
            if (matches.Count == 0 && path.Contains(".."))
            {
                matches = TryResolveRelativePath(path);
            }

            // Handle results
            if (matches.Count == 0)
            {
                return (null, BuildFileNotFoundError(path));
            }

            if (matches.Count > 1)
            {
                return (null, BuildAmbiguousPathError(path, matches));
            }

            return (matches[0], null);
        }

        /// <summary>
        /// Try to match a path that starts with a project/root directory name.
        /// E.g., "CryptoTraderCore/Exchange/File.cs" when CryptoTraderCore is a root.
        /// </summary>
        private List<FileAnalysis> TryMatchProjectPrefixedPath(string normalizedPath)
        {
            var matches = new List<FileAnalysis>();

            // Check if path starts with a known root directory name
            foreach (var dir in _inputDirectories)
            {
                var rootName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                
                if (normalizedPath.StartsWith(rootName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove the root prefix and search for the remaining path
                    var remainingPath = normalizedPath.Substring(rootName.Length + 1);
                    
                    var match = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                        f.RootDirectory != null &&
                        f.RootDirectory.TrimEnd('\\', '/').EndsWith(rootName, StringComparison.OrdinalIgnoreCase) &&
                        (f.RelativePath.Replace('\\', '/').Equals(remainingPath, StringComparison.OrdinalIgnoreCase) ||
                         f.RelativePath.Replace('\\', '/').EndsWith("/" + remainingPath, StringComparison.OrdinalIgnoreCase)));

                    if (match != null)
                    {
                        matches.Add(match);
                        break;
                    }
                }
            }

            // Also check SourceWorkspace for merged workspaces
            if (matches.Count == 0)
            {
                var pathParts = normalizedPath.Split('/');
                if (pathParts.Length > 1)
                {
                    var potentialWorkspace = pathParts[0];
                    var remainingPath = string.Join("/", pathParts.Skip(1));

                    var match = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                        !string.IsNullOrEmpty(f.SourceWorkspace) &&
                        f.SourceWorkspace.Equals(potentialWorkspace, StringComparison.OrdinalIgnoreCase) &&
                        f.RelativePath.Replace('\\', '/').Equals(remainingPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                        matches.Add(match);
                }
            }

            return matches;
        }

        /// <summary>
        /// Try to resolve paths containing ../
        /// </summary>
        private List<FileAnalysis> TryResolveRelativePath(string path)
        {
            var matches = new List<FileAnalysis>();

            foreach (var baseDir in _inputDirectories)
            {
                try
                {
                    var resolvedPath = Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar)));

                    var match = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                        f.FilePath.Equals(resolvedPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        matches.Add(match);
                        break;
                    }
                }
                catch
                {
                    // Invalid path, continue trying other directories
                }
            }

            return matches;
        }

        /// <summary>
        /// Build a helpful error message when file is not found.
        /// Groups suggestions by workspace in merged mode.
        /// </summary>
        private string BuildFileNotFoundError(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"File not found: {path}");
            sb.AppendLine();

            var isMerged = _workspaceAnalysis.AllFiles.Any(f => !string.IsNullOrEmpty(f.SourceWorkspace));

            if (isMerged)
            {
                sb.AppendLine("**Available files by workspace:**");
                sb.AppendLine();

                var byWorkspace = _workspaceAnalysis.AllFiles
                    .GroupBy(f => f.SourceWorkspace ?? "Unknown")
                    .OrderBy(g => g.Key);

                foreach (var group in byWorkspace)
                {
                    sb.AppendLine($"**{group.Key}:**");
                    foreach (var file in group.Take(5))
                    {
                        sb.AppendLine($"- {file.RelativePath}");
                    }
                    if (group.Count() > 5)
                        sb.AppendLine($"- ... and {group.Count() - 5} more");
                    sb.AppendLine();
                }

                sb.AppendLine("**Tip:** Use `ProjectName/path/to/file.cs` format for clarity in merged workspaces.");
            }
            else
            {
                sb.AppendLine("Available files:");
                foreach (var file in _workspaceAnalysis.AllFiles.Take(10))
                {
                    sb.AppendLine($"- {file.RelativePath}");
                }
                if (_workspaceAnalysis.AllFiles.Count > 10)
                    sb.AppendLine($"- ... and {_workspaceAnalysis.AllFiles.Count - 10} more");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Build error message for ambiguous path matches.
        /// </summary>
        private string BuildAmbiguousPathError(string path, List<FileAnalysis> matches)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Error: Ambiguous path '{path}' matches {matches.Count} files:");
            sb.AppendLine();

            foreach (var m in matches)
            {
                var workspace = !string.IsNullOrEmpty(m.SourceWorkspace) ? m.SourceWorkspace : 
                    (!string.IsNullOrEmpty(m.RootDirectory) ? Path.GetFileName(m.RootDirectory.TrimEnd('\\', '/')) : "unknown");
                
                sb.AppendLine($"- `{m.RelativePath}` in **{workspace}**");
            }

            sb.AppendLine();
            sb.AppendLine("**Please disambiguate using one of these formats:**");
            foreach (var m in matches.Take(3))
            {
                var workspace = !string.IsNullOrEmpty(m.SourceWorkspace) ? m.SourceWorkspace :
                    (!string.IsNullOrEmpty(m.RootDirectory) ? Path.GetFileName(m.RootDirectory.TrimEnd('\\', '/')) : null);
                
                if (workspace != null)
                    sb.AppendLine($"- `{workspace}/{m.RelativePath}`");
            }

            return sb.ToString();
        }
    }
}
