using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles file write MCP tools.
    /// </summary>
    public class McpWriteToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly RefactoringService _refactoringService;
        private readonly Action _refreshIndex;
        private readonly Action<string> _sendActivity;
        private readonly Action<string> _log;

        public McpWriteToolHandler(
            WorkspaceAnalysis workspaceAnalysis,
            RefactoringService refactoringService,
            Action refreshIndex,
            Action<string> sendActivity,
            Action<string> log)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _refactoringService = refactoringService;
            _refreshIndex = refreshIndex;
            _sendActivity = sendActivity;
            _log = log;
        }

        public string StrReplace(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("oldStr", out var oldStrEl))
                return "Error: 'oldStr' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var oldStr = oldStrEl.GetString() ?? "";
            var newStr = arguments.TryGetProperty("newStr", out var newStrEl) ? newStrEl.GetString() ?? "" : "";

            var createBackup = true;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            _sendActivity($"StrReplace: {path}");

            var file = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Equals(path, StringComparison.OrdinalIgnoreCase));

            if (file == null)
            {
                return $"Error: File not found: {path}";
            }

            try
            {
                var content = File.ReadAllText(file.FilePath);

                // Normalize line endings and trim trailing whitespace for matching
                var fileLineEnding = DetectLineEnding(content);
                var normalizedContent = TrimTrailingWhitespacePerLine(content);
                normalizedContent = NormalizeLineEndings(normalizedContent, "\n");
                
                var normalizedOldStr = TrimTrailingWhitespacePerLine(oldStr);
                normalizedOldStr = NormalizeLineEndings(normalizedOldStr, "\n");
                
                var normalizedNewStr = TrimTrailingWhitespacePerLine(newStr);
                normalizedNewStr = NormalizeLineEndings(normalizedNewStr, fileLineEnding);

                var count = 0;
                var index = 0;
                while ((index = normalizedContent.IndexOf(normalizedOldStr, index, StringComparison.Ordinal)) != -1)
                {
                    count++;
                    index += normalizedOldStr.Length;
                }

                if (count == 0)
                {
                    return BuildNotFoundDiagnostic(normalizedContent, normalizedOldStr, file.RelativePath);
                }

                if (count > 1)
                {
                    return $"Error: String appears {count} times in file. It must be unique (appear exactly once).\n\n**Looking for:**\n```\n{normalizedOldStr}\n```";
                }

                if (createBackup && File.Exists(file.FilePath))
                {
                    File.Copy(file.FilePath, file.FilePath + ".bak", true);
                }

                // Replace in normalized content, then restore file's line endings
                var newContent = normalizedContent.Replace(normalizedOldStr, normalizedNewStr);
                newContent = NormalizeLineEndings(newContent, fileLineEnding);
                File.WriteAllText(file.FilePath, newContent);

                var action = string.IsNullOrEmpty(newStr) ? "deleted" : "replaced";
                _log($"StrReplace: {path} - {action}");

                // Refresh index to reflect changes
                _refreshIndex();

                var sb = new StringBuilder();
                sb.AppendLine($"# String Replace Result");
                sb.AppendLine();
                sb.AppendLine($"**File:** `{file.RelativePath}`");
                sb.AppendLine($"**Status:** Success - string {action}");
                if (createBackup)
                    sb.AppendLine($"**Backup:** `{file.FilePath}.bak`");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Builds a diagnostic message when the search string is not found.
        /// Shows whitespace details and tries to find partial matches.
        /// </summary>
        private string BuildNotFoundDiagnostic(string content, string oldStr, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Error: String not found in file");
            sb.AppendLine();
            sb.AppendLine($"**File:** `{filePath}`");
            sb.AppendLine();

            // Show what we were looking for with whitespace visualization
            var oldStrLines = oldStr.Split('\n');
            sb.AppendLine($"**Looking for ({oldStrLines.Length} lines, {oldStr.Length} chars):**");
            sb.AppendLine("```");
            foreach (var line in oldStrLines.Take(10))
            {
                sb.AppendLine(VisualizeWhitespace(line.TrimEnd('\r')));
            }
            if (oldStrLines.Length > 10)
                sb.AppendLine($"... ({oldStrLines.Length - 10} more lines)");
            sb.AppendLine("```");
            sb.AppendLine();

            // Try to find partial matches using the first non-empty line
            var firstLine = oldStrLines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (firstLine.Length > 10)
            {
                var contentLines = content.Split('\n');
                var partialMatches = new System.Collections.Generic.List<(int lineNum, string line)>();

                for (int i = 0; i < contentLines.Length; i++)
                {
                    // Check if the trimmed content matches (ignoring whitespace differences)
                    if (contentLines[i].Trim().Contains(firstLine.Substring(0, Math.Min(20, firstLine.Length))))
                    {
                        partialMatches.Add((i + 1, contentLines[i]));
                    }
                }

                if (partialMatches.Count > 0)
                {
                    sb.AppendLine("**Possible matches found (similar content at these lines):**");
                    sb.AppendLine("```");
                    foreach (var (lineNum, line) in partialMatches.Take(5))
                    {
                        sb.AppendLine($"Line {lineNum}: {VisualizeWhitespace(line.TrimEnd('\r'))}");
                    }
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("**Hint:** Whitespace mismatch? Use `codemerger_get_lines` to see exact file content.");
                }
                else
                {
                    sb.AppendLine("**No similar content found.** The code may have changed or the search text is incorrect.");
                }
            }

            // Show whitespace legend
            sb.AppendLine();
            sb.AppendLine("**Whitespace legend:** `→` = tab, space = space");

            return sb.ToString();
        }

        /// <summary>
        /// Detects the line ending style used in the file content.
        /// </summary>
        private static string DetectLineEnding(string content)
        {
            // Check for Windows-style CRLF first
            if (content.Contains("\r\n"))
                return "\r\n";
            // Check for old Mac-style CR (rare)
            if (content.Contains("\r"))
                return "\r";
            // Default to Unix-style LF
            return "\n";
        }

        /// <summary>
        /// Normalizes the search string's line endings to match the file's line endings.
        /// </summary>
        private static string NormalizeLineEndings(string searchStr, string targetLineEnding)
        {
            // First normalize to LF, then convert to target
            var normalized = searchStr.Replace("\r\n", "\n").Replace("\r", "\n");
            if (targetLineEnding != "\n")
                normalized = normalized.Replace("\n", targetLineEnding);
            return normalized;
        }

        /// <summary>
        /// Trims trailing whitespace from each line while preserving line endings.
        /// </summary>
        private static string TrimTrailingWhitespacePerLine(string text)
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                // Remove trailing whitespace but preserve \r if present (will be before the split point)
                lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Makes leading whitespace visible by showing tabs as → and preserving spaces.
        /// </summary>
        private static string VisualizeWhitespace(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            // Find the end of leading whitespace
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                i++;

            if (i == 0) return line;

            // Visualize leading whitespace: tabs become →, spaces stay as spaces
            var leading = line.Substring(0, i).Replace("\t", "→");
            var rest = line.Substring(i);

            return leading + rest;
        }

        public string WriteFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("content", out var contentEl))
                return "Error: 'content' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var content = contentEl.GetString() ?? "";

            _sendActivity($"Writing: {path}");

            var createBackup = true;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            var result = _refactoringService.WriteFile(path, content, createBackup);
            _log($"WriteFile: {path} - {(result.Success ? "OK" : "FAILED")}");

            // Refresh index to reflect changes
            if (result.Success)
                _refreshIndex();

            return result.ToMarkdown();
        }

        public string PreviewWriteFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("content", out var contentEl))
                return "Error: 'content' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var content = contentEl.GetString() ?? "";

            _sendActivity($"Preview: {path}");

            var result = _refactoringService.PreviewWriteFile(path, content);
            _log($"PreviewWrite: {path}");

            return result.ToMarkdown();
        }
    }
}
