using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles file write MCP tools.
    /// Fixed: Simplified StrReplace to avoid position mapping bugs.
    /// </summary>
    public class McpWriteToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly RefactoringService _refactoringService;
        private readonly FilePathResolver _pathResolver;
        private readonly List<string> _inputDirectories;
        private readonly WorkspaceSettings _settings;
        private readonly Action<string> _updateFileIndex;
        private readonly Action<string> _sendActivity;
        private readonly Action<string> _log;

        public McpWriteToolHandler(
            WorkspaceAnalysis workspaceAnalysis,
            RefactoringService refactoringService,
            List<string> inputDirectories,
            WorkspaceSettings settings,
            Action<string> updateFileIndex,
            Action<string> sendActivity,
            Action<string> log)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _refactoringService = refactoringService;
            _pathResolver = new FilePathResolver(workspaceAnalysis, inputDirectories);
            _inputDirectories = inputDirectories;
            _settings = settings;
            _updateFileIndex = updateFileIndex;
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

            // Use settings default, but allow override from arguments
            var createBackup = _settings.CreateBackupFiles;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            var normalizeIndent = false;
            if (arguments.TryGetProperty("normalizeIndent", out var normalizeEl))
                normalizeIndent = normalizeEl.GetBoolean();

            _sendActivity($"StrReplace: {path}");

            var (file, findError) = _pathResolver.FindFile(path);
            if (file == null)
            {
                return findError!;
            }

            try
            {
                var content = File.ReadAllText(file.FilePath);

                // Normalize line endings in search string to match file
                var searchStr = oldStr;
                var replaceStr = newStr;

                // Detect file's line ending style
                string fileLineEnding = "\n";
                if (content.Contains("\r\n"))
                    fileLineEnding = "\r\n";
                else if (content.Contains("\r"))
                    fileLineEnding = "\r";

                // Normalize search string line endings to match file
                searchStr = searchStr.Replace("\r\n", "\n").Replace("\r", "\n");
                if (fileLineEnding != "\n")
                    searchStr = searchStr.Replace("\n", fileLineEnding);

                // Normalize replacement string line endings to match file
                replaceStr = replaceStr.Replace("\r\n", "\n").Replace("\r", "\n");
                if (fileLineEnding != "\n")
                    replaceStr = replaceStr.Replace("\n", fileLineEnding);

                int index;
                string matchedOriginal = "";

                if (normalizeIndent)
                {
                    // Find match with normalized indentation
                    var result = FindWithNormalizedIndent(content, searchStr, fileLineEnding);
                    index = result.index;
                    matchedOriginal = result.matchedText;
                    // newStr is written as-is — the caller controls the desired indentation.
                    // This allows normalizeIndent to fix bad indentation in the file.
                }
                else
                {
                    // Find exact match
                    index = content.IndexOf(searchStr, StringComparison.Ordinal);
                    if (index != -1)
                        matchedOriginal = searchStr;
                }

                if (index == -1)
                {
                    // Not found - provide diagnostics
                    return BuildNotFoundError(content, oldStr, file.RelativePath, normalizeIndent);
                }

                // Check for multiple matches
                int matchLength = matchedOriginal.Length;
                int secondIndex;
                if (normalizeIndent)
                {
                    var secondResult = FindWithNormalizedIndent(content, searchStr, fileLineEnding, index + matchLength);
                    secondIndex = secondResult.index;
                }
                else
                {
                    secondIndex = content.IndexOf(searchStr, index + 1, StringComparison.Ordinal);
                }

                if (secondIndex != -1)
                {
                    return $"Error: String appears multiple times in file. It must be unique (appear exactly once).\n\n" +
                           $"💡 **Tip:** Include more surrounding context to make the match unique.";
                }

                // Create backup
                if (createBackup && File.Exists(file.FilePath))
                {
                    File.Copy(file.FilePath, file.FilePath + ".bak", true);
                }

                // Perform the replacement
                var newContent = content.Substring(0, index) + replaceStr + content.Substring(index + matchLength);

                File.WriteAllText(file.FilePath, newContent);

                var action = string.IsNullOrEmpty(newStr) ? "deleted" : "replaced";
                _log($"StrReplace: {path} - {action}{(normalizeIndent ? " (indent-normalized)" : "")}");

                _updateFileIndex(file.FilePath);

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
        /// Find a match with normalized indentation (ignores leading whitespace differences).
        /// Returns the index in the original content and the actual matched text.
        /// </summary>
        private (int index, string matchedText) FindWithNormalizedIndent(string content, string searchStr, string lineEnding, int startIndex = 0)
        {
            var searchLines = searchStr.Split(new[] { lineEnding }, StringSplitOptions.None);
            var contentLines = content.Split(new[] { lineEnding }, StringSplitOptions.None);

            // Normalize search lines (trim leading whitespace for comparison)
            var normalizedSearch = searchLines.Select(l => l.TrimStart()).ToArray();

            // Find first non-empty search line for initial matching
            int firstNonEmptyIdx = Array.FindIndex(normalizedSearch, l => !string.IsNullOrWhiteSpace(l));
            if (firstNonEmptyIdx == -1)
                return (-1, "");

            string firstSearchLine = normalizedSearch[firstNonEmptyIdx];

            // Calculate starting line index from character position
            int startLineIdx = 0;
            if (startIndex > 0)
            {
                int charCount = 0;
                for (int i = 0; i < contentLines.Length; i++)
                {
                    charCount += contentLines[i].Length + lineEnding.Length;
                    if (charCount > startIndex)
                    {
                        startLineIdx = i;
                        break;
                    }
                }
            }

            // Search through content lines
            for (int i = startLineIdx; i <= contentLines.Length - searchLines.Length; i++)
            {
                // Check if first non-empty line matches
                if (!contentLines[i + firstNonEmptyIdx].TrimStart().Equals(firstSearchLine, StringComparison.Ordinal))
                    continue;

                // Check all lines match (with normalized whitespace)
                bool allMatch = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    var contentTrimmed = contentLines[i + j].TrimStart();
                    var searchTrimmed = normalizedSearch[j];

                    if (!contentTrimmed.Equals(searchTrimmed, StringComparison.Ordinal))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    // Calculate character position of match start
                    int charPos = 0;
                    for (int k = 0; k < i; k++)
                        charPos += contentLines[k].Length + lineEnding.Length;

                    // Build the matched text from original content
                    var matchedLines = contentLines.Skip(i).Take(searchLines.Length);
                    var matchedText = string.Join(lineEnding, matchedLines);

                    return (charPos, matchedText);
                }
            }

            return (-1, "");
        }


        private string BuildNotFoundError(string content, string searchStr, string filePath, bool normalizeIndent = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Error: String not found in file");
            sb.AppendLine();
            sb.AppendLine($"**File:** `{filePath}`");
            sb.AppendLine();

            // Show what we were looking for
            var searchLines = searchStr.Split('\n');
            sb.AppendLine($"**Looking for ({searchLines.Length} lines):**");
            sb.AppendLine("```");
            foreach (var line in searchLines.Take(10))
            {
                sb.AppendLine(line.TrimEnd('\r'));
            }
            if (searchLines.Length > 10)
                sb.AppendLine($"... ({searchLines.Length - 10} more lines)");
            sb.AppendLine("```");
            sb.AppendLine();

            // Find best fuzzy match
            var contentLines = content.Split('\n');
            var searchTrimmed = searchLines.Select(l => l.TrimEnd('\r')).ToArray();
            var firstSearchLine = searchTrimmed.FirstOrDefault(l => l.Trim().Length > 5)?.Trim() ?? "";

            int bestMatchLine = -1;
            int bestMatchScore = 0;

            if (!string.IsNullOrEmpty(firstSearchLine))
            {
                // Find candidate lines that contain part of the first meaningful search line
                var searchSnippet = firstSearchLine.Substring(0, Math.Min(30, firstSearchLine.Length));
                for (int i = 0; i < contentLines.Length; i++)
                {
                    if (contentLines[i].Contains(searchSnippet))
                    {
                        // Score this candidate: how many subsequent lines also match?
                        int score = 0;
                        for (int j = 0; j < searchTrimmed.Length && (i + j) < contentLines.Length; j++)
                        {
                            if (contentLines[i + j].TrimEnd('\r').Trim() == searchTrimmed[j].Trim())
                                score += 2;
                            else if (contentLines[i + j].Contains(searchTrimmed[j].Trim().Substring(0, Math.Min(15, searchTrimmed[j].Trim().Length))))
                                score += 1;
                        }
                        if (score > bestMatchScore)
                        {
                            bestMatchScore = score;
                            bestMatchLine = i;
                        }
                    }
                }
            }

            if (bestMatchLine >= 0)
            {
                sb.AppendLine($"**Possible match at line {bestMatchLine + 1}:**");
                sb.AppendLine("```");
                int showLines = Math.Min(searchTrimmed.Length + 2, contentLines.Length - bestMatchLine);
                for (int i = bestMatchLine; i < bestMatchLine + showLines && i < contentLines.Length; i++)
                {
                    sb.AppendLine(contentLines[i].TrimEnd('\r'));
                }
                sb.AppendLine("```");
                sb.AppendLine();

                // Show differences
                var diffs = new List<string>();
                for (int j = 0; j < searchTrimmed.Length && (bestMatchLine + j) < contentLines.Length; j++)
                {
                    var expected = searchTrimmed[j];
                    var actual = contentLines[bestMatchLine + j].TrimEnd('\r');
                    if (expected != actual)
                    {
                        if (expected.Trim() == actual.Trim())
                            diffs.Add($"Line {bestMatchLine + j + 1}: **indentation differs** (content identical)");
                        else
                            diffs.Add($"Line {bestMatchLine + j + 1}: content differs");
                    }
                }
                if (diffs.Count > 0)
                {
                    sb.AppendLine("**Differences found:**");
                    foreach (var diff in diffs.Take(5))
                        sb.AppendLine($"- {diff}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("💡 **Tips:**");
            sb.AppendLine("- Use `codemerger_get_lines` to see exact file content");
            sb.AppendLine("- Check for whitespace differences (tabs vs spaces)");
            if (!normalizeIndent)
                sb.AppendLine("- Try `normalizeIndent: true` to ignore leading whitespace differences");
            sb.AppendLine("- Copy the exact text from `get_lines` output");

            return sb.ToString();
        }

        public string WriteFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("content", out var contentEl))
                return "Error: 'content' parameter is required.";

            // Preview mode — show diff without writing
            if (arguments.TryGetProperty("preview", out var previewEl) && previewEl.GetBoolean())
                return PreviewWriteFile(arguments);

            var path = pathEl.GetString() ?? "";
            var content = contentEl.GetString() ?? "";

            _sendActivity($"Writing: {path}");

            // Use settings default, but allow override from arguments
            var createBackup = _settings.CreateBackupFiles;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = _refactoringService.WriteFile(path, content, createBackup);
            sw.Stop();

            // Update index for this file in background (non-blocking)
            if (result.Success && !string.IsNullOrEmpty(result.FullPath))
                _updateFileIndex(result.FullPath);

            var response = result.ToMarkdown();
            var responseSize = System.Text.Encoding.UTF8.GetByteCount(response);

            _log($"WriteFile: {path} - {(result.Success ? "OK" : "FAILED")} | {sw.ElapsedMilliseconds}ms | response: {responseSize} bytes");

            // Soft warning for large files that may cause memory issues in MCP clients
            var lineCount = content.Split('\n').Length;
            if (lineCount > 600)
            {
                response += "\n\n⚠️ **Large file warning:** This file was " + lineCount + " lines. " +
                    "Large write_file calls can cause memory issues in some MCP clients. " +
                    "Next time, consider writing a small skeleton first, then filling in sections with `str_replace`.";
            }

            return response;
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

        public string DeleteFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            var path = pathEl.GetString() ?? "";
            _sendActivity($"Deleting: {path}");

            var (file, findError) = _pathResolver.FindFile(path);
            if (file == null)
                return findError!;

            try
            {
                // Create backup before deleting (respects settings)
                string? backupPath = null;
                if (_settings.CreateBackupFiles)
                {
                    backupPath = file.FilePath + ".bak";
                    File.Copy(file.FilePath, backupPath, true);
                }

                // Delete the file
                File.Delete(file.FilePath);

                _log($"DeleteFile: {path}");
                _updateFileIndex(file.FilePath);

                var sb = new StringBuilder();
                sb.AppendLine("# Delete File Result");
                sb.AppendLine();
                sb.AppendLine($"**File:** `{file.RelativePath}`");
                sb.AppendLine($"**Status:** Deleted");
                if (backupPath != null)
                {
                    sb.AppendLine($"**Backup:** `{backupPath}`");
                    sb.AppendLine();
                    sb.AppendLine("*Use `codemerger_undo` to restore this file if needed.*");
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("*No backup created (backups disabled in settings).*");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public string CreateFolder(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            var relativePath = pathEl.GetString() ?? "";
            _sendActivity($"CreateFolder: {relativePath}");

            if (string.IsNullOrWhiteSpace(relativePath))
                return "Error: 'path' cannot be empty.";

            try
            {
                // Resolve base directory (same logic as WriteFile)
                string baseDir;
                string effectivePath = relativePath;

                var matchedRoot = _inputDirectories.FirstOrDefault(dir =>
                {
                    var rootName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    return relativePath.StartsWith(rootName + "/", StringComparison.OrdinalIgnoreCase) ||
                           relativePath.StartsWith(rootName + "\\", StringComparison.OrdinalIgnoreCase);
                });

                if (matchedRoot != null)
                {
                    baseDir = matchedRoot;
                    var rootName = Path.GetFileName(matchedRoot.TrimEnd('\\', '/'));
                    effectivePath = relativePath.Substring(rootName.Length + 1);
                }
                else
                {
                    baseDir = _inputDirectories.FirstOrDefault() ?? Directory.GetCurrentDirectory();
                }

                var fullPath = Path.GetFullPath(Path.Combine(baseDir, effectivePath.Replace('/', Path.DirectorySeparatorChar)));

                // Security: verify within workspace
                bool withinWorkspace = _inputDirectories.Any(dir =>
                    fullPath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                if (!withinWorkspace)
                    return $"Error: Path escapes workspace boundaries.";

                if (Directory.Exists(fullPath))
                    return $"# Create Folder Result\n\n**Path:** `{relativePath}`\n**Status:** Already exists";

                Directory.CreateDirectory(fullPath);
                _log($"CreateFolder: {relativePath}");

                return $"# Create Folder Result\n\n**Path:** `{relativePath}`\n**Full Path:** `{fullPath}`\n**Status:** Created successfully";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public string GrepReplace(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("pattern", out var patternEl))
                return "Error: 'pattern' parameter is required.";
            if (!arguments.TryGetProperty("replacement", out var replacementEl))
                return "Error: 'replacement' parameter is required.";

            var pattern = patternEl.GetString() ?? "";
            var replacement = replacementEl.GetString() ?? "";

            var preview = true;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            var caseSensitive = false;
            if (arguments.TryGetProperty("caseSensitive", out var caseEl))
                caseSensitive = caseEl.GetBoolean();

            string? fileFilter = null;
            if (arguments.TryGetProperty("fileFilter", out var filterEl))
                fileFilter = filterEl.GetString();

            // Parse excludeMatches (match indices to skip)
            var excludeSet = new HashSet<int>();
            if (arguments.TryGetProperty("excludeMatches", out var excludeEl) && excludeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in excludeEl.EnumerateArray())
                {
                    if (item.TryGetInt32(out var idx))
                        excludeSet.Add(idx);
                }
            }

            // Parse excludePattern (lines matching this regex are skipped)
            Regex? excludeRegex = null;
            if (arguments.TryGetProperty("excludePattern", out var exPatEl))
            {
                var exPat = exPatEl.GetString();
                if (!string.IsNullOrEmpty(exPat))
                {
                    try
                    {
                        excludeRegex = new Regex(exPat, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Compiled);
                    }
                    catch (ArgumentException ex)
                    {
                        return $"Error: Invalid excludePattern regex: {ex.Message}";
                    }
                }
            }

            _sendActivity($"GrepReplace: {pattern} → {replacement}{(preview ? " (preview)" : "")}");


            var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            Regex regex;
            try
            {
                regex = new Regex(pattern, regexOptions | RegexOptions.Compiled);
            }
            catch (ArgumentException ex)
            {
                return $"Error: Invalid regex pattern: {ex.Message}";
            }

            var changes = new List<(string relativePath, string filePath, int matchCount, List<(int line, string before, string after, int globalIndex)> lineChanges)>();
            int totalMatches = 0;
            int globalMatchIndex = 0;

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                if (fileFilter != null && !file.RelativePath.Contains(fileFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var lines = File.ReadAllLines(file.FilePath);
                    var fileChanges = new List<(int line, string before, string after, int globalIndex)>();

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (excludeRegex != null && excludeRegex.IsMatch(lines[i]))
                            continue; // Skip lines matching excludePattern

                        if (regex.IsMatch(lines[i]))
                        {
                            var newLine = regex.Replace(lines[i], replacement);
                            if (newLine != lines[i])
                            {
                                globalMatchIndex++;
                                fileChanges.Add((i + 1, lines[i], newLine, globalMatchIndex));
                            }
                        }
                    }

                    if (fileChanges.Count > 0)
                    {
                        totalMatches += fileChanges.Count;
                        changes.Add((file.RelativePath, file.FilePath, fileChanges.Count, fileChanges));
                    }
                }
                catch { /* skip unreadable files */ }
            }

            if (changes.Count == 0)
                return $"No matches found for pattern: `{pattern}`";

            var sb = new StringBuilder();

            if (preview)
            {
                sb.AppendLine("# Grep Replace Preview");
                sb.AppendLine();
                sb.AppendLine($"**Pattern:** `{pattern}`");
                sb.AppendLine($"**Replacement:** `{replacement}`");
                sb.AppendLine($"**Total:** {totalMatches} replacements in {changes.Count} files");
                sb.AppendLine();

                foreach (var (relativePath, _, matchCount, lineChanges) in changes)
                {
                    sb.AppendLine($"## `{relativePath}` ({matchCount} changes)");
                    sb.AppendLine();
                    foreach (var (line, before, after, gIdx) in lineChanges.Take(10))
                    {
                        sb.AppendLine($"**Match #{gIdx} — Line {line}:**");
                        sb.AppendLine($"- `{before.Trim()}`");
                        sb.AppendLine($"+ `{after.Trim()}`");
                    }
                    if (lineChanges.Count > 10)
                        sb.AppendLine($"*... and {lineChanges.Count - 10} more changes*");
                    sb.AppendLine();
                }
                sb.AppendLine("*Call again with `preview: false` to apply. Use `excludeMatches: [#]` to skip specific matches.*");
            }
            else
            {
                // Apply changes line-by-line (matches preview logic exactly)
                int applied = 0;
                int skipped = 0;

                foreach (var (relativePath, filePath, _, lineChanges) in changes)
                {
                    // Determine which line numbers to skip in this file
                    var skipLineNumbers = new HashSet<int>();
                    foreach (var (line, _, _, gIdx) in lineChanges)
                    {
                        if (excludeSet.Contains(gIdx))
                            skipLineNumbers.Add(line);
                    }

                    // If all matches in this file are excluded, skip the file entirely
                    if (skipLineNumbers.Count == lineChanges.Count)
                    {
                        skipped += lineChanges.Count;
                        continue;
                    }

                    try
                    {
                        if (_settings.CreateBackupFiles)
                            File.Copy(filePath, filePath + ".bak", true);

                        var lines = File.ReadAllLines(filePath);

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (skipLineNumbers.Contains(i + 1))
                                continue; // Skip excluded match
                            if (excludeRegex != null && excludeRegex.IsMatch(lines[i]))
                                continue; // Skip lines matching excludePattern

                            if (regex.IsMatch(lines[i]))
                            {
                                var newLine = regex.Replace(lines[i], replacement);
                                if (newLine != lines[i])
                                {
                                    lines[i] = newLine;
                                    applied++;
                                }
                            }
                        }

                        // Preserve original line endings
                        var rawContent = File.ReadAllText(filePath);
                        var lineEnding = rawContent.Contains("\r\n") ? "\r\n" : "\n";
                        File.WriteAllText(filePath, string.Join(lineEnding, lines));
                        _updateFileIndex(filePath);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"Error writing `{relativePath}`: {ex.Message}");
                    }

                    skipped += skipLineNumbers.Count;
                }

                sb.AppendLine("# Grep Replace Result");
                sb.AppendLine();
                sb.AppendLine($"**Pattern:** `{pattern}`");
                sb.AppendLine($"**Replacement:** `{replacement}`");
                sb.AppendLine($"**Applied:** {applied} replacements in {changes.Count} files");
                if (skipped > 0)
                    sb.AppendLine($"**Skipped:** {skipped} (excluded)");
                sb.AppendLine();
                foreach (var (relativePath, _, matchCount, lineChanges) in changes)
                {
                    var fileSkipped = lineChanges.Count(lc => excludeSet.Contains(lc.globalIndex));
                    if (fileSkipped > 0)
                        sb.AppendLine($"- `{relativePath}` ({matchCount - fileSkipped} applied, {fileSkipped} skipped)");
                    else
                        sb.AppendLine($"- `{relativePath}` ({matchCount} changes)");
                }

                _log($"GrepReplace: {applied} applied, {skipped} skipped in {changes.Count} files");
            }

            return sb.ToString();
        }

        public string Undo(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            var path = pathEl.GetString() ?? "";
            _sendActivity($"Undo: {path}");

            // Find the file or determine the full path
            var (file, findError) = _pathResolver.FindFile(path);

            string fullPath;
            string relativePath;

            if (file != null)
            {
                fullPath = file.FilePath;
                relativePath = file.RelativePath;
            }
            else if (findError != null && findError.Contains("Ambiguous"))
            {
                // Multiple matches - return the ambiguity error
                return findError;
            }
            else
            {
                // File might have been deleted - try to find backup by constructing path
                if (_inputDirectories == null || _inputDirectories.Count == 0)
                    return $"Error: Cannot determine file location for: {path}";

                fullPath = Path.Combine(_inputDirectories[0], path.Replace('/', Path.DirectorySeparatorChar));
                relativePath = path;
            }

            var backupPath = fullPath + ".bak";

            if (!File.Exists(backupPath))
                return $"Error: No backup found for: {path}\n\nExpected backup at: `{backupPath}`";

            try
            {
                // Restore from backup
                File.Copy(backupPath, fullPath, true);
                File.Delete(backupPath);

                _log($"Undo: {path}");
                _updateFileIndex(fullPath);

                var sb = new StringBuilder();
                sb.AppendLine("# Undo Result");
                sb.AppendLine();
                sb.AppendLine($"**File:** `{relativePath}`");
                sb.AppendLine($"**Status:** Restored from backup");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public string ReplaceLines(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";
            if (!arguments.TryGetProperty("startLine", out var startEl))
                return "Error: 'startLine' parameter is required.";
            if (!arguments.TryGetProperty("endLine", out var endEl))
                return "Error: 'endLine' parameter is required.";
            if (!arguments.TryGetProperty("newContent", out var contentEl))
                return "Error: 'newContent' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var startLine = startEl.GetInt32();
            var endLine = endEl.GetInt32();
            var newContent = contentEl.GetString() ?? "";

            var preview = false;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            var createBackup = _settings.CreateBackupFiles;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            _sendActivity($"ReplaceLines: {path} [{startLine}-{endLine}]{(preview ? " (preview)" : "")}");

            var (file, findError) = _pathResolver.FindFile(path);
            if (file == null)
                return findError!;

            try
            {
                var lines = File.ReadAllLines(file.FilePath).ToList();
                var totalLines = lines.Count;

                // Validate range
                if (startLine < 1 || startLine > totalLines)
                    return $"Error: startLine {startLine} is out of range (file has {totalLines} lines).";
                if (endLine < startLine || endLine > totalLines)
                    return $"Error: endLine {endLine} is out of range (startLine={startLine}, file has {totalLines} lines).";

                // Convert to 0-based
                int startIdx = startLine - 1;
                int endIdx = endLine; // endLine is inclusive, so remove [startIdx..endIdx)

                // Split new content into lines
                var replacementLines = newContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

                // Build context for preview/result
                var sb = new StringBuilder();
                int contextSize = 3;

                if (preview)
                {
                    sb.AppendLine("# Replace Lines Preview");
                    sb.AppendLine();
                    sb.AppendLine($"**File:** `{file.RelativePath}`");
                    sb.AppendLine($"**Replacing:** lines {startLine}–{endLine} ({endLine - startLine + 1} lines → {replacementLines.Count} lines)");
                    sb.AppendLine();

                    // Show context before
                    int ctxStart = Math.Max(0, startIdx - contextSize);
                    if (ctxStart < startIdx)
                    {
                        sb.AppendLine("**Context before:**");
                        sb.AppendLine("```");
                        for (int i = ctxStart; i < startIdx; i++)
                            sb.AppendLine($"{i + 1,4}: {lines[i]}");
                        sb.AppendLine("```");
                    }

                    // Show lines being removed
                    sb.AppendLine("**Removing:**");
                    sb.AppendLine("```diff");
                    for (int i = startIdx; i < endIdx; i++)
                        sb.AppendLine($"- {lines[i]}");
                    sb.AppendLine("```");

                    // Show replacement
                    sb.AppendLine("**Inserting:**");
                    sb.AppendLine("```diff");
                    foreach (var line in replacementLines)
                        sb.AppendLine($"+ {line}");
                    sb.AppendLine("```");

                    // Show context after
                    int ctxEnd = Math.Min(totalLines, endIdx + contextSize);
                    if (endIdx < ctxEnd)
                    {
                        sb.AppendLine("**Context after:**");
                        sb.AppendLine("```");
                        for (int i = endIdx; i < ctxEnd; i++)
                            sb.AppendLine($"{i + 1,4}: {lines[i]}");
                        sb.AppendLine("```");
                    }

                    sb.AppendLine();
                    sb.AppendLine("*Call again with `preview: false` to apply.*");
                    return sb.ToString();
                }

                // Apply the replacement
                if (createBackup && File.Exists(file.FilePath))
                    File.Copy(file.FilePath, file.FilePath + ".bak", true);

                // Remove old lines and insert new ones
                lines.RemoveRange(startIdx, endIdx - startIdx);
                lines.InsertRange(startIdx, replacementLines);

                // Detect line ending from original file
                var rawContent = File.ReadAllText(file.FilePath);
                var lineEnding = rawContent.Contains("\r\n") ? "\r\n" : "\n";

                File.WriteAllText(file.FilePath, string.Join(lineEnding, lines));

                _log($"ReplaceLines: {path} [{startLine}-{endLine}]");
                _updateFileIndex(file.FilePath);

                sb.AppendLine("# Replace Lines Result");
                sb.AppendLine();
                sb.AppendLine($"**File:** `{file.RelativePath}`");
                sb.AppendLine($"**Status:** Success - replaced lines {startLine}–{endLine} ({endLine - startLine + 1} lines → {replacementLines.Count} lines)");
                sb.AppendLine();

                // Show surrounding context after replacement
                int showStart = Math.Max(0, startIdx - contextSize);
                int showEnd = Math.Min(lines.Count, startIdx + replacementLines.Count + contextSize);
                sb.AppendLine("**Result:**");
                sb.AppendLine("```");
                for (int i = showStart; i < showEnd; i++)
                    sb.AppendLine($"{i + 1,4}: {lines[i]}");
                sb.AppendLine("```");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public string MoveFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("oldPath", out var oldPathEl))
                return "Error: 'oldPath' parameter is required.";
            if (!arguments.TryGetProperty("newPath", out var newPathEl))
                return "Error: 'newPath' parameter is required.";

            var oldPath = oldPathEl.GetString() ?? "";
            var newPath = newPathEl.GetString() ?? "";
            var preview = true;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            _sendActivity($"MoveFile: {oldPath} -> {newPath}");

            var (file, findError) = _pathResolver.FindFile(oldPath);
            if (file == null)
                return findError!;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(preview ? "# Move File Preview" : "# Move File Result");
                sb.AppendLine();
                sb.AppendLine($"**From:** `{oldPath}`");
                sb.AppendLine($"**To:** `{newPath}`");
                sb.AppendLine();

                // Determine old and new namespaces from directory structure
                var oldNamespace = GetNamespaceFromPath(oldPath);
                var newNamespace = GetNamespaceFromPath(newPath);

                var affectedFiles = new List<string>();

                if (oldNamespace != newNamespace && !string.IsNullOrEmpty(oldNamespace))
                {
                    sb.AppendLine($"**Namespace change:** `{oldNamespace}` → `{newNamespace}`");
                    sb.AppendLine();

                    // Find files that reference types from the moved file
                    var typesInFile = file.Types.Select(t => t.Name).ToList();

                    foreach (var otherFile in _workspaceAnalysis.AllFiles.Where(f => f.Extension == ".cs" && f.FilePath != file.FilePath))
                    {
                        var content = File.ReadAllText(otherFile.FilePath);
                        bool needsUpdate = false;

                        // Check if file uses any types from the moved file
                        foreach (var typeName in typesInFile)
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(content, $@"\b{typeName}\b"))
                            {
                                needsUpdate = true;
                                break;
                            }
                        }

                        // Check if file has using statement for old namespace
                        if (content.Contains($"using {oldNamespace};"))
                            needsUpdate = true;

                        if (needsUpdate)
                            affectedFiles.Add(otherFile.RelativePath);
                    }

                    if (affectedFiles.Count > 0)
                    {
                        sb.AppendLine("## Files that may need using statement updates");
                        foreach (var af in affectedFiles.Take(20))
                            sb.AppendLine($"- `{af}`");
                        if (affectedFiles.Count > 20)
                            sb.AppendLine($"- ... and {affectedFiles.Count - 20} more");
                        sb.AppendLine();
                    }
                }

                if (!preview)
                {
                    // Determine full paths
                    var oldDir = Path.GetDirectoryName(file.FilePath) ?? "";
                    var baseDir = oldDir;

                    // Walk up to find the base directory that matches the relative path structure
                    var relativeDir = Path.GetDirectoryName(oldPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                    if (!string.IsNullOrEmpty(relativeDir))
                    {
                        baseDir = file.FilePath.Substring(0, file.FilePath.Length - oldPath.Length);
                    }

                    var newFullPath = Path.Combine(baseDir, newPath.Replace('/', Path.DirectorySeparatorChar));
                    var newDir = Path.GetDirectoryName(newFullPath);

                    // Create backup if enabled
                    string? backupPath = null;
                    if (_settings.CreateBackupFiles)
                    {
                        backupPath = file.FilePath + ".bak";
                        File.Copy(file.FilePath, backupPath, true);
                    }

                    // Ensure target directory exists
                    if (!string.IsNullOrEmpty(newDir) && !Directory.Exists(newDir))
                        Directory.CreateDirectory(newDir);

                    // Update namespace in the file if needed
                    if (oldNamespace != newNamespace && !string.IsNullOrEmpty(newNamespace))
                    {
                        var content = File.ReadAllText(file.FilePath);
                        if (!string.IsNullOrEmpty(oldNamespace))
                            content = content.Replace($"namespace {oldNamespace}", $"namespace {newNamespace}");
                        File.WriteAllText(file.FilePath, content);
                    }

                    // Move the file
                    File.Move(file.FilePath, newFullPath, true);

                    _log($"MoveFile: {oldPath} -> {newPath}");
                    _updateFileIndex(file.FilePath);
                    _updateFileIndex(newFullPath);

                    sb.AppendLine("**Status:** Moved successfully");
                    if (backupPath != null)
                        sb.AppendLine($"**Backup:** `{backupPath}`");

                    if (affectedFiles.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("*Note: You may need to update using statements in the affected files listed above.*");
                    }
                }
                else
                {
                    sb.AppendLine("*This is a preview. Run with `preview: false` to apply the move.*");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string GetNamespaceFromPath(string relativePath)
        {
            var dir = Path.GetDirectoryName(relativePath);
            if (string.IsNullOrEmpty(dir))
                return string.Empty;

            // Convert path separators to dots and clean up
            return dir.Replace('/', '.').Replace('\\', '.').Trim('.');
        }
    }
}
