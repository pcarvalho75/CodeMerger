using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles maintenance MCP tools for workspace cleanup and code quality analysis.
    /// </summary>
    public class McpMaintenanceToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly List<string> _inputDirectories;
        private readonly Action<string> _sendActivity;

        public McpMaintenanceToolHandler(
            WorkspaceAnalysis workspaceAnalysis,
            List<string> inputDirectories,
            Action<string> sendActivity)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _inputDirectories = inputDirectories;
            _sendActivity = sendActivity;
        }

        /// <summary>
        /// Clean up all .bak backup files from the workspace.
        /// </summary>
        public string CleanBackups(JsonElement arguments)
        {
            var confirm = false;
            if (arguments.TryGetProperty("confirm", out var confirmEl))
                confirm = confirmEl.GetBoolean();

            _sendActivity("Scanning for backup files...");

            var bakFiles = new List<(string Path, long Size)>();

            foreach (var dir in _inputDirectories.Where(Directory.Exists))
            {
                try
                {
                    var files = Directory.EnumerateFiles(dir, "*.bak", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            bakFiles.Add((file, info.Length));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            var sb = new StringBuilder();

            if (bakFiles.Count == 0)
            {
                sb.AppendLine("# Clean Backups");
                sb.AppendLine();
                sb.AppendLine("No .bak files found in the workspace.");
                return sb.ToString();
            }

            var totalSize = bakFiles.Sum(f => f.Size);
            var totalSizeStr = FormatSize(totalSize);

            if (!confirm)
            {
                sb.AppendLine("# Clean Backups - Preview");
                sb.AppendLine();
                sb.AppendLine($"**Found:** {bakFiles.Count} backup files ({totalSizeStr})");
                sb.AppendLine();

                // Show folder breakdown
                var byFolder = bakFiles
                    .GroupBy(f => Path.GetDirectoryName(GetRelativePath(f.Path)) ?? "(root)")
                    .OrderByDescending(g => g.Count())
                    .Take(5);
                
                sb.AppendLine("## By folder:");
                foreach (var folder in byFolder)
                {
                    var folderSize = FormatSize(folder.Sum(f => f.Size));
                    sb.AppendLine($"- `{folder.Key}`: {folder.Count()} files ({folderSize})");
                }
                sb.AppendLine();

                sb.AppendLine("## Files to delete:");
                foreach (var (path, size) in bakFiles.Take(20))
                {
                    var relativePath = GetRelativePath(path);
                    sb.AppendLine($"- `{relativePath}` ({FormatSize(size)})");
                }
                if (bakFiles.Count > 20)
                    sb.AppendLine($"- ... and {bakFiles.Count - 20} more");
                sb.AppendLine();
                sb.AppendLine("*Run with `confirm: true` to delete these files.*");
                return sb.ToString();
            }

            // Actually delete
            _sendActivity($"Deleting {bakFiles.Count} backup files...");

            int deleted = 0;
            int failed = 0;
            var errors = new List<string>();

            foreach (var (path, _) in bakFiles)
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 5)
                        errors.Add($"{GetRelativePath(path)}: {ex.Message}");
                }
            }

            sb.AppendLine("# Clean Backups - Complete");
            sb.AppendLine();
            sb.AppendLine($"**Deleted:** {deleted} files ({totalSizeStr} freed)");
            if (failed > 0)
            {
                sb.AppendLine($"**Failed:** {failed} files");
                sb.AppendLine();
                sb.AppendLine("## Errors:");
                foreach (var err in errors)
                    sb.AppendLine($"- {err}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Find duplicate code blocks across the codebase.
        /// </summary>
        public string FindDuplicates(JsonElement arguments)
        {
            var minLines = 5;
            if (arguments.TryGetProperty("minLines", out var minLinesEl))
                minLines = minLinesEl.GetInt32();

            var minSimilarity = 80;
            if (arguments.TryGetProperty("minSimilarity", out var minSimEl))
                minSimilarity = minSimEl.GetInt32();

            var maxResults = 20;
            if (arguments.TryGetProperty("maxResults", out var maxResEl))
                maxResults = maxResEl.GetInt32();

            _sendActivity("Analyzing code for duplicates...");

            // Extract all method bodies
            var methodBlocks = new List<CodeBlock>();

            foreach (var file in _workspaceAnalysis.AllFiles.Where(f => f.Extension == ".cs"))
            {
                foreach (var type in file.Types)
                {
                    foreach (var member in type.Members.Where(m => m.Kind == CodeMemberKind.Method))
                    {
                        if (string.IsNullOrEmpty(member.Body))
                            continue;

                        var lines = member.Body.Split('\n').Length;
                        if (lines < minLines)
                            continue;

                        methodBlocks.Add(new CodeBlock
                        {
                            FilePath = file.RelativePath,
                            TypeName = type.Name,
                            MemberName = member.Name,
                            StartLine = member.StartLine,
                            EndLine = member.EndLine,
                            Content = member.Body,
                            NormalizedContent = NormalizeCode(member.Body),
                            LineCount = lines
                        });
                    }
                }
            }

            if (methodBlocks.Count == 0)
            {
                return "# Find Duplicates\n\nNo method bodies found to analyze. Make sure the workspace contains C# files with method implementations.";
            }

            _sendActivity($"Comparing {methodBlocks.Count} methods for duplicates...");

            // Find duplicates using normalized content comparison
            var duplicateClusters = new List<DuplicateCluster>();
            var processed = new HashSet<int>();

            // Progress tracking for large codebases
            int comparisons = 0;
            int totalComparisons = (methodBlocks.Count * (methodBlocks.Count - 1)) / 2;
            int lastProgress = 0;

            for (int i = 0; i < methodBlocks.Count; i++)
            {
                if (processed.Contains(i))
                    continue;

                var cluster = new DuplicateCluster { Blocks = new List<CodeBlock> { methodBlocks[i] } };

                for (int j = i + 1; j < methodBlocks.Count; j++)
                {
                    comparisons++;
                    
                    // Update progress every 10%
                    int progress = totalComparisons > 0 ? (comparisons * 100) / totalComparisons : 100;
                    if (progress >= lastProgress + 10)
                    {
                        _sendActivity($"Analyzing duplicates... {progress}%");
                        lastProgress = progress;
                    }

                    if (processed.Contains(j))
                        continue;

                    var similarity = CalculateSimilarity(methodBlocks[i].NormalizedContent, methodBlocks[j].NormalizedContent);

                    if (similarity >= minSimilarity)
                    {
                        cluster.Blocks.Add(methodBlocks[j]);
                        cluster.Similarity = Math.Max(cluster.Similarity, similarity);
                        processed.Add(j);
                    }
                }

                if (cluster.Blocks.Count > 1)
                {
                    cluster.Similarity = cluster.Similarity > 0 ? cluster.Similarity : 100;
                    duplicateClusters.Add(cluster);
                    processed.Add(i);
                }
            }

            // Sort by number of duplicates (most duplicated first), then by similarity
            duplicateClusters = duplicateClusters
                .OrderByDescending(c => c.Blocks.Count)
                .ThenByDescending(c => c.Similarity)
                .Take(maxResults)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# Duplicate Code Analysis");
            sb.AppendLine();
            sb.AppendLine($"**Methods analyzed:** {methodBlocks.Count}");
            sb.AppendLine($"**Duplicate clusters found:** {duplicateClusters.Count}");
            sb.AppendLine($"**Minimum similarity:** {minSimilarity}%");
            sb.AppendLine($"**Minimum lines:** {minLines}");

            if (duplicateClusters.Count > 0)
            {
                // Calculate potential savings
                int totalDuplicateLines = duplicateClusters
                    .Sum(c => c.Blocks.Skip(1).Sum(b => b.LineCount)); // All but first occurrence
                sb.AppendLine($"**Potential savings:** ~{totalDuplicateLines} lines (if refactored to shared methods)");
            }
            sb.AppendLine();

            if (duplicateClusters.Count == 0)
            {
                sb.AppendLine("*No duplicate code blocks found matching the criteria.*");
                return sb.ToString();
            }

            int clusterNum = 1;
            foreach (var cluster in duplicateClusters)
            {
                sb.AppendLine($"## Cluster {clusterNum}: {cluster.Blocks.Count} occurrences (~{cluster.Similarity}% similar)");
                sb.AppendLine();

                foreach (var block in cluster.Blocks)
                {
                    sb.AppendLine($"- `{block.FilePath}` → **{block.TypeName}.{block.MemberName}** (lines {block.StartLine}-{block.EndLine}, {block.LineCount} lines)");
                }

                // Show a snippet of the duplicated code
                var firstBlock = cluster.Blocks.First();
                var snippet = GetCodeSnippet(firstBlock.Content, 8);
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(snippet);
                sb.AppendLine("```");
                sb.AppendLine();

                clusterNum++;
            }

            sb.AppendLine("---");
            
            // Add actionable recommendations
            var highestImpact = duplicateClusters.OrderByDescending(c => c.Blocks.Sum(b => b.LineCount)).FirstOrDefault();
            if (highestImpact != null && highestImpact.Blocks.Count > 0)
            {
                var firstMethod = highestImpact.Blocks.First();
                var totalLines = highestImpact.Blocks.Sum(b => b.LineCount);
                sb.AppendLine($"💡 **Highest impact:** Cluster with `{firstMethod.TypeName}.{firstMethod.MemberName}` ({totalLines} total lines across {highestImpact.Blocks.Count} occurrences)");
                sb.AppendLine();
            }
            
            sb.AppendLine("*Consider extracting duplicated code into shared helper methods using `extract_method`.*");

            return sb.ToString();
        }

        #region Helper Methods

        private string GetRelativePath(string fullPath)
        {
            foreach (var dir in _inputDirectories)
            {
                if (fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = fullPath.Substring(dir.Length).TrimStart('\\', '/');
                    return relative;
                }
            }
            return Path.GetFileName(fullPath);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        /// <summary>
        /// Normalize code for comparison: remove whitespace variations, standardize formatting.
        /// </summary>
        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return string.Empty;

            var sb = new StringBuilder();
            bool lastWasWhitespace = false;

            foreach (var c in code)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasWhitespace)
                    {
                        sb.Append(' ');
                        lastWasWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasWhitespace = false;
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Calculate similarity percentage between two normalized code strings.
        /// Uses Levenshtein distance ratio.
        /// </summary>
        private static int CalculateSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            // Quick check for exact match
            if (a == b) return 100;

            // Quick check for very different lengths
            var lengthRatio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
            if (lengthRatio < 0.5) return 0;

            // Use hash-based comparison for performance on longer strings
            if (a.Length > 500 || b.Length > 500)
            {
                return CalculateHashSimilarity(a, b);
            }

            // Levenshtein for shorter strings
            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            var similarity = (int)((1.0 - (double)distance / maxLen) * 100);
            return Math.Max(0, similarity);
        }

        /// <summary>
        /// Hash-based similarity using n-gram fingerprinting.
        /// </summary>
        private static int CalculateHashSimilarity(string a, string b)
        {
            const int ngramSize = 5;

            var ngramsA = GetNgrams(a, ngramSize);
            var ngramsB = GetNgrams(b, ngramSize);

            if (ngramsA.Count == 0 || ngramsB.Count == 0)
                return 0;

            var intersection = ngramsA.Intersect(ngramsB).Count();
            var union = ngramsA.Union(ngramsB).Count();

            return union > 0 ? (int)((double)intersection / union * 100) : 0;
        }

        private static HashSet<string> GetNgrams(string text, int n)
        {
            var ngrams = new HashSet<string>();
            for (int i = 0; i <= text.Length - n; i++)
            {
                ngrams.Add(text.Substring(i, n));
            }
            return ngrams;
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var d = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[a.Length, b.Length];
        }

        private static string GetCodeSnippet(string code, int maxLines)
        {
            var lines = code.Split('\n').Take(maxLines).ToList();
            var result = string.Join("\n", lines.Select(l => l.TrimEnd('\r')));
            if (code.Split('\n').Length > maxLines)
                result += "\n// ... (truncated)";
            return result;
        }

        #endregion

        #region Helper Classes

        private class CodeBlock
        {
            public string FilePath { get; set; } = "";
            public string TypeName { get; set; } = "";
            public string MemberName { get; set; } = "";
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public string Content { get; set; } = "";
            public string NormalizedContent { get; set; } = "";
            public int LineCount { get; set; }
        }

        private class DuplicateCluster
        {
            public List<CodeBlock> Blocks { get; set; } = new();
            public int Similarity { get; set; }
        }

        #endregion
    }
}
