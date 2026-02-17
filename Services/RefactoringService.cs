using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeMerger.Models;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services
{
    /// <summary>
    /// Provides refactoring and code modification capabilities.
    /// </summary>
    public class RefactoringService
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly List<string> _inputDirectories;
        private readonly WorkspaceSettings _settings;

        public RefactoringService(WorkspaceAnalysis workspaceAnalysis, List<string> inputDirectories, WorkspaceSettings? settings = null)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _inputDirectories = inputDirectories;
            _settings = settings ?? WorkspaceSettings.GetDefaultSettings();
        }

        /// <summary>
        /// Write content to a file (create or overwrite).
        /// Supports ../ paths to write to sibling projects within the workspace.
        /// </summary>
        public WriteFileResult WriteFile(string relativePath, string content, bool? createBackup = null)
        {
            // Use settings default if not explicitly specified
            var shouldBackup = createBackup ?? _settings.CreateBackupFiles;
            
            var result = new WriteFileResult { RelativePath = relativePath };

            try
            {
                // Security: Validate path has no dangerous characters
                if (!IsPathSafe(relativePath))
                {
                    result.Success = false;
                    result.Error = "Invalid path: contains invalid characters";
                    return result;
                }

                string fullPath;
                FileAnalysis? existingFile = null;

                // Try to match path prefix to a known project root
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
                    // Path starts with a root name like "Vortex/Engines/file.cs"
                    // Use that root and strip the prefix
                    baseDir = matchedRoot;
                    var rootName = Path.GetFileName(matchedRoot.TrimEnd('\\', '/'));
                    effectivePath = relativePath.Substring(rootName.Length + 1); // +1 for the separator
                }
                else
                {
                    // No root prefix - use first directory (original behavior)
                    baseDir = _inputDirectories.FirstOrDefault() ?? Directory.GetCurrentDirectory();
                }

                fullPath = Path.GetFullPath(Path.Combine(baseDir, effectivePath.Replace('/', Path.DirectorySeparatorChar)));

                // Security: Verify resolved path is within workspace
                if (!IsPathWithinWorkspace(fullPath))
                {
                    result.Success = false;
                    result.Error = $"Invalid path: resolved path escapes workspace. Valid roots: {string.Join(", ", _inputDirectories.Select(d => Path.GetFileName(d.TrimEnd('\\', '/'))))}";
                    return result;
                }

                // Check if file exists - match by full path OR relative path
                existingFile = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                    f.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Replace('\\', '/').Equals(relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

                // SAFETY CHECK: If creating a NEW file with multiple roots and no explicit root prefix,
                // require explicit project specification to avoid accidentally creating in wrong location
                if (existingFile == null && matchedRoot == null && _inputDirectories.Count > 1)
                {
                    var rootNames = _inputDirectories.Select(d => Path.GetFileName(d.TrimEnd('\\', '/'))).ToList();
                    result.Success = false;
                    result.Error = $"Ambiguous target: This workspace has multiple project roots and the path '{relativePath}' doesn't specify which one.\n\n" +
                                $"**Available roots:** {string.Join(", ", rootNames)}\n\n" +
                                $"**Please prefix your path with the target project**, e.g.:\n" +
                                string.Join("\n", rootNames.Take(3).Select(r => $"- `{r}/{relativePath}`"));
                    return result;
                }

                if (existingFile != null)
                {
                    fullPath = existingFile.FilePath;
                    result.IsNewFile = false;

                    // Create backup
                    if (shouldBackup && File.Exists(fullPath))
                    {
                        var backupPath = fullPath + ".bak";
                        File.Copy(fullPath, backupPath, overwrite: true);
                        result.BackupPath = backupPath;
                    }

                    // Generate diff
                    var oldContent = File.ReadAllText(fullPath);
                    result.Diff = GenerateDiff(oldContent, content, existingFile.RelativePath);
                }
                else
                {
                    result.IsNewFile = true;

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }

                // Write the file atomically
                WriteFileAtomic(fullPath, content);
                result.FullPath = fullPath;
                result.Success = true;
                result.BytesWritten = content.Length;

                // Warn if new file is outside any .csproj directory
                if (result.IsNewFile && fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var csprojWarning = CheckCsprojProximity(fullPath, baseDir);
                    if (csprojWarning != null)
                        result.Warning = csprojWarning;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Check if a file path has a .csproj in its directory or any parent up to baseDir.
        /// Returns a warning string if not, null if OK.
        /// </summary>
        private string? CheckCsprojProximity(string fullPath, string baseDir)
        {
            var dir = Path.GetDirectoryName(fullPath);
            var normalizedBase = Path.GetFullPath(baseDir).TrimEnd('\\', '/');

            while (!string.IsNullOrEmpty(dir))
            {
                var normalizedDir = Path.GetFullPath(dir).TrimEnd('\\', '/');

                // Check for .csproj in this directory
                try
                {
                    if (Directory.EnumerateFiles(dir, "*.csproj").Any())
                        return null; // Found — no warning
                }
                catch { /* skip inaccessible dirs */ }

                // Stop if we've reached or passed the workspace root
                if (normalizedDir.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase) ||
                    !normalizedDir.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                    break;

                dir = Path.GetDirectoryName(dir);
            }

            // No .csproj found — find the nearest one for a suggestion
            var csprojFiles = new List<string>();
            try
            {
                csprojFiles = Directory.EnumerateFiles(baseDir, "*.csproj", SearchOption.AllDirectories).Take(5).ToList();
            }
            catch { }

            if (csprojFiles.Count == 0)
                return null; // No .csproj in workspace at all — probably not a .NET project structure

            var csprojDirs = csprojFiles.Select(f => Path.GetDirectoryName(f)!).Distinct().ToList();
            var suggestions = csprojDirs.Select(d =>
            {
                var relDir = d.Substring(normalizedBase.Length).TrimStart('\\', '/');
                var fileName = Path.GetFileName(fullPath);
                return string.IsNullOrEmpty(relDir) ? fileName : $"{relDir.Replace('\\', '/')}/{fileName}";
            }).ToList();

            return $"⚠️ **Warning:** This file was created outside any .csproj directory and may not be included in the build.\n" +
                   $"**Did you mean:** {string.Join(" or ", suggestions.Select(s => $"`{s}`"))}";
        }

        /// <summary>
        /// Check if a relative path is safe (no dangerous characters).
        /// Note: Path traversal (..) is allowed - security is enforced by IsPathWithinWorkspace().
        /// </summary>
        private bool IsPathSafe(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            // Reject absolute paths
            if (Path.IsPathRooted(relativePath))
                return false;

            // Reject paths with null bytes or other dangerous characters
            if (relativePath.Contains('\0'))
                return false;

            return true;
        }

        /// <summary>
        /// Check if a fully resolved path is within one of the workspace directories.
        /// </summary>
        private bool IsPathWithinWorkspace(string fullPath)
        {
            var normalizedPath = Path.GetFullPath(fullPath);
            
            foreach (var inputDir in _inputDirectories)
            {
                var normalizedDir = Path.GetFullPath(inputDir);
                if (!normalizedDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    normalizedDir += Path.DirectorySeparatorChar;

                if (normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Write file atomically: write to temp file, then move to target.
        /// Prevents corruption if process crashes mid-write.
        /// </summary>
        private void WriteFileAtomic(string targetPath, string content)
        {
            var dir = Path.GetDirectoryName(targetPath) ?? ".";
            var tempPath = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                // Write to temp file
                File.WriteAllText(tempPath, content);

                // Atomic move (replace if exists)
                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                // Clean up temp file if it still exists (move failed)
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        /// <summary>
        /// Preview what a file write would look like without actually writing.
        /// </summary>
        public WriteFileResult PreviewWriteFile(string relativePath, string content)
        {
            var result = new WriteFileResult
            {
                RelativePath = relativePath,
                IsPreview = true
            };

            var existingFile = _workspaceAnalysis.AllFiles
                .FirstOrDefault(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
                                    f.RelativePath.Replace('\\', '/').Equals(relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

            if (existingFile != null)
            {
                result.IsNewFile = false;
                var oldContent = File.ReadAllText(existingFile.FilePath);
                result.Diff = GenerateDiff(oldContent, content, relativePath);
            }
            else
            {
                result.IsNewFile = true;
                result.Diff = $"+++ {relativePath} (new file)\n{content}";
            }

            result.Success = true;
            return result;
        }

        /// <summary>
        /// Rename a symbol across all files.
        /// </summary>
        public RenameResult RenameSymbol(string oldName, string newName, bool preview = true)
        {
            var result = new RenameResult
            {
                OldName = oldName,
                NewName = newName,
                IsPreview = preview
            };

            var affectedFiles = new Dictionary<string, RenameFileChange>();

            foreach (var file in _workspaceAnalysis.AllFiles.Where(f => f.Extension == ".cs"))
            {
                try
                {
                    var content = File.ReadAllText(file.FilePath);

                    // Use syntax tree to rename only actual identifiers (skips strings, comments)
                    var outcome = SyntaxAwareRenamer.Rename(content, oldName, newName);

                    if (outcome != null)
                    {
                        affectedFiles[file.RelativePath] = new RenameFileChange
                        {
                            FilePath = file.RelativePath,
                            FullPath = file.FilePath,
                            OccurrenceCount = outcome.OccurrenceCount,
                            Diff = GenerateDiff(content, outcome.ModifiedSource, file.RelativePath)
                        };

                        if (!preview)
                        {
                            // Create backup only if settings allow
                            if (_settings.CreateBackupFiles)
                                File.Copy(file.FilePath, file.FilePath + ".bak", overwrite: true);
                            File.WriteAllText(file.FilePath, outcome.ModifiedSource);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file.RelativePath}: {ex.Message}");
                }
            }

            result.AffectedFiles = affectedFiles.Values.ToList();
            result.TotalOccurrences = affectedFiles.Values.Sum(f => f.OccurrenceCount);
            result.Success = !result.Errors.Any();

            return result;
        }

        /// <summary>
        /// Generate an interface from a class's public members.
        /// </summary>
        public GenerateInterfaceResult GenerateInterface(string className, string? interfaceName = null)
        {
            var result = new GenerateInterfaceResult { ClassName = className };

            // Find the class
            var classFile = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Select(t => new { File = f, Type = t }))
                .FirstOrDefault(x => x.Type.Name.Equals(className, StringComparison.OrdinalIgnoreCase));

            if (classFile == null)
            {
                result.Error = $"Class '{className}' not found";
                return result;
            }

            var type = classFile.Type;
            var file = classFile.File;

            interfaceName ??= $"I{className}";
            result.InterfaceName = interfaceName;

            var sb = new StringBuilder();

            // Get namespace from file
            try
            {
                var content = File.ReadAllText(file.FilePath);
                var tree = CSharpSyntaxTree.ParseText(content);
                var root = tree.GetCompilationUnitRoot();
                var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

                if (ns != null)
                {
                    sb.AppendLine($"namespace {ns.Name}");
                    sb.AppendLine("{");
                }

                sb.AppendLine($"    /// <summary>");
                sb.AppendLine($"    /// Interface for {className}");
                sb.AppendLine($"    /// </summary>");
                sb.AppendLine($"    public interface {interfaceName}");
                sb.AppendLine("    {");

                // Add public methods and properties
                foreach (var member in type.Members.Where(m => m.AccessModifier == "public"))
                {
                    if (member.Kind == CodeMemberKind.Method && member.Name != className) // Skip constructors
                    {
                        var asyncMod = member.IsAsync ? "" : ""; // Interface doesn't need async keyword, just Task return
                        sb.AppendLine($"        {member.ReturnType} {member.Signature};");
                    }
                    else if (member.Kind == CodeMemberKind.Property)
                    {
                        sb.AppendLine($"        {member.ReturnType} {member.Name} {{ get; set; }}");
                    }
                }

                sb.AppendLine("    }");

                if (ns != null)
                {
                    sb.AppendLine("}");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            result.GeneratedCode = sb.ToString();
            result.SuggestedFilePath = Path.Combine(
                Path.GetDirectoryName(file.RelativePath) ?? "",
                $"{interfaceName}.cs"
            );
            result.Success = true;

            return result;
        }

        /// <summary>
        /// Extract code into a new method.
        /// </summary>
        public ExtractMethodResult ExtractMethod(string filePath, int startLine, int endLine, string newMethodName)
        {
            var result = new ExtractMethodResult
            {
                FilePath = filePath,
                NewMethodName = newMethodName
            };

            var file = _workspaceAnalysis.AllFiles
                .FirstOrDefault(f => f.RelativePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (file == null)
            {
                result.Error = $"File '{filePath}' not found";
                return result;
            }

            try
            {
                var lines = File.ReadAllLines(file.FilePath).ToList();

                if (startLine < 1 || endLine > lines.Count || startLine > endLine)
                {
                    result.Error = $"Invalid line range: {startLine}-{endLine}";
                    return result;
                }

                // Extract the lines
                var extractedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToList();
                var extractedCode = string.Join(Environment.NewLine, extractedLines);

                // Detect indentation
                var baseIndent = extractedLines.FirstOrDefault()?.TakeWhile(char.IsWhiteSpace).Count() ?? 8;
                var methodIndent = new string(' ', baseIndent);
                var bodyIndent = new string(' ', baseIndent + 4);

                // Build new method
                var newMethod = new StringBuilder();
                newMethod.AppendLine();
                newMethod.AppendLine($"{methodIndent}private void {newMethodName}()");
                newMethod.AppendLine($"{methodIndent}{{");
                foreach (var line in extractedLines)
                {
                    newMethod.AppendLine(line);
                }
                newMethod.AppendLine($"{methodIndent}}}");

                // Replace extracted code with method call
                var callIndent = new string(' ', baseIndent);
                lines.RemoveRange(startLine - 1, endLine - startLine + 1);
                lines.Insert(startLine - 1, $"{callIndent}{newMethodName}();");

                // Find where to insert the new method (after the current class's closing brace area)
                // Simple approach: insert at end of file before last }
                var insertIndex = lines.Count - 1;
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (lines[i].Trim() == "}")
                    {
                        insertIndex = i;
                        break;
                    }
                }

                lines.Insert(insertIndex, newMethod.ToString());

                result.ModifiedContent = string.Join(Environment.NewLine, lines);
                result.ExtractedCode = extractedCode;
                result.MethodCall = $"{newMethodName}();";
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Generate a proper unified diff with context and hunks using DiffPlex (Myers algorithm).
        /// O(n+m) space instead of O(n*m) — safe for large files.
        /// </summary>
        private string GenerateDiff(string oldContent, string newContent, string fileName)
        {
            var oldLines = oldContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var newLines = newContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

            // Skip diff for full rewrites — produces useless 100% delete + 100% add output
            int commonLines = 0;
            int checkLimit = Math.Min(oldLines.Length, newLines.Length);
            for (int i = 0; i < checkLimit; i++)
            {
                if (oldLines[i] == newLines[i]) commonLines++;
            }
            int maxLines = Math.Max(oldLines.Length, newLines.Length);
            double similarityPct = maxLines > 0 ? (double)commonLines / maxLines * 100 : 100;

            if (similarityPct < 20)
            {
                return $"(Full rewrite — {oldLines.Length} lines removed, {newLines.Length} lines added. " +
                       $"Diff skipped to avoid excessive output.)";
            }

            // Use DiffPlex inline diff (Myers algorithm)
            var diffBuilder = new InlineDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(oldContent, newContent);

            var sb = new StringBuilder();
            sb.AppendLine($"--- a/{fileName}");
            sb.AppendLine($"+++ b/{fileName}");

            // Build unified diff hunks from DiffPlex output
            const int contextLines = 3;
            var lines = diff.Lines;
            int totalDiffLines = 0;

            int lineIdx = 0;
            while (lineIdx < lines.Count)
            {
                // Skip unchanged lines until we find a change
                if (lines[lineIdx].Type == ChangeType.Unchanged)
                {
                    lineIdx++;
                    continue;
                }

                // Found a change — build a hunk with context
                int hunkStart = Math.Max(0, lineIdx - contextLines);
                int oldLineNum = 1, newLineNum = 1;

                // Calculate line numbers up to hunk start
                for (int i = 0; i < hunkStart; i++)
                {
                    if (lines[i].Type != ChangeType.Inserted) oldLineNum++;
                    if (lines[i].Type != ChangeType.Deleted) newLineNum++;
                }

                var hunkLines = new List<string>();
                int hunkOldStart = oldLineNum;
                int hunkNewStart = newLineNum;
                int hunkOldCount = 0;
                int hunkNewCount = 0;

                // Add leading context
                for (int i = hunkStart; i < lineIdx; i++)
                {
                    hunkLines.Add(" " + lines[i].Text);
                    hunkOldCount++;
                    hunkNewCount++;
                }

                // Add changes and merge nearby hunks
                int trailingUnchanged = 0;
                while (lineIdx < lines.Count)
                {
                    var line = lines[lineIdx];

                    if (line.Type == ChangeType.Unchanged)
                    {
                        trailingUnchanged++;
                        if (trailingUnchanged > contextLines * 2)
                        {
                            // Gap too large — end this hunk (back up the extra context lines)
                            lineIdx -= (trailingUnchanged - contextLines);
                            trailingUnchanged = contextLines;
                            break;
                        }
                        hunkLines.Add(" " + line.Text);
                        hunkOldCount++;
                        hunkNewCount++;
                    }
                    else
                    {
                        trailingUnchanged = 0;
                        if (line.Type == ChangeType.Deleted)
                        {
                            hunkLines.Add("-" + line.Text);
                            hunkOldCount++;
                        }
                        else if (line.Type == ChangeType.Inserted)
                        {
                            hunkLines.Add("+" + line.Text);
                            hunkNewCount++;
                        }
                        else if (line.Type == ChangeType.Modified)
                        {
                            hunkLines.Add("-" + line.Text);
                            hunkLines.Add("+" + line.Text);
                            hunkOldCount++;
                            hunkNewCount++;
                        }
                    }

                    lineIdx++;
                }

                // Trim trailing context to exactly contextLines
                while (trailingUnchanged > contextLines && hunkLines.Count > 0 && hunkLines[^1].StartsWith(" "))
                {
                    hunkLines.RemoveAt(hunkLines.Count - 1);
                    hunkOldCount--;
                    hunkNewCount--;
                    trailingUnchanged--;
                }

                // Emit hunk
                sb.AppendLine($"@@ -{hunkOldStart},{hunkOldCount} +{hunkNewStart},{hunkNewCount} @@");
                foreach (var hl in hunkLines)
                {
                    sb.AppendLine(hl);
                    totalDiffLines++;
                    if (totalDiffLines >= 100)
                    {
                        sb.AppendLine("... (diff truncated)");
                        return sb.ToString();
                    }
                }
            }

            return sb.ToString();
        }
    }

    #region Result Models

    public class WriteFileResult
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool Success { get; set; }
        public bool IsNewFile { get; set; }
        public bool IsPreview { get; set; }
        public string? BackupPath { get; set; }
        public string? Error { get; set; }
        public string? Warning { get; set; }
        public string? Diff { get; set; }
        public int BytesWritten { get; set; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            if (IsPreview)
            {
                sb.AppendLine("# Write File Preview");
            }
            else
            {
                sb.AppendLine("# Write File Result");
            }
            sb.AppendLine();

            if (Success)
            {
                sb.AppendLine($"**File:** `{RelativePath}`");
                sb.AppendLine($"**Status:** {(IsNewFile ? "Created" : "Updated")}");

                if (!IsPreview)
                {
                    sb.AppendLine($"**Bytes written:** {BytesWritten}");
                    if (!string.IsNullOrEmpty(BackupPath))
                    {
                        sb.AppendLine($"**Backup:** `{BackupPath}`");
                    }
                }

                if (!string.IsNullOrEmpty(Diff))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Changes");
                    sb.AppendLine("```diff");
                    sb.AppendLine(Diff);
                    sb.AppendLine("```");
                }

                if (!string.IsNullOrEmpty(Warning))
                {
                    sb.AppendLine();
                    sb.AppendLine(Warning);
                }
            }
            else
            {
                sb.AppendLine($"**Error:** {Error}");
            }

            return sb.ToString();
        }
    }

    public class RenameResult
    {
        public string OldName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public bool IsPreview { get; set; }
        public bool Success { get; set; }
        public List<RenameFileChange> AffectedFiles { get; set; } = new();
        public int TotalOccurrences { get; set; }
        public List<string> Errors { get; set; } = new();

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine(IsPreview ? "# Rename Preview" : "# Rename Result");
            sb.AppendLine();
            sb.AppendLine($"**Rename:** `{OldName}` → `{NewName}`");
            sb.AppendLine($"**Total occurrences:** {TotalOccurrences} in {AffectedFiles.Count} files");
            sb.AppendLine();

            if (AffectedFiles.Any())
            {
                sb.AppendLine("## Affected Files");
                foreach (var file in AffectedFiles)
                {
                    sb.AppendLine($"### `{file.FilePath}` ({file.OccurrenceCount} occurrences)");
                    if (!string.IsNullOrEmpty(file.Diff))
                    {
                        sb.AppendLine("```diff");
                        // Only show first 50 lines of diff
                        var diffLines = file.Diff.Split('\n').Take(50);
                        sb.AppendLine(string.Join("\n", diffLines));
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }

            if (Errors.Any())
            {
                sb.AppendLine("## Errors");
                foreach (var error in Errors)
                {
                    sb.AppendLine($"- {error}");
                }
            }

            return sb.ToString();
        }
    }

    public class RenameFileChange
    {
        public string FilePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public int OccurrenceCount { get; set; }
        public string Diff { get; set; } = string.Empty;
    }

    public class GenerateInterfaceResult
    {
        public string ClassName { get; set; } = string.Empty;
        public string InterfaceName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string GeneratedCode { get; set; } = string.Empty;
        public string SuggestedFilePath { get; set; } = string.Empty;

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Generate Interface: `{InterfaceName}`");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(Error))
            {
                sb.AppendLine($"**Error:** {Error}");
                return sb.ToString();
            }

            sb.AppendLine($"**From class:** `{ClassName}`");
            sb.AppendLine($"**Suggested file:** `{SuggestedFilePath}`");
            sb.AppendLine();
            sb.AppendLine("## Generated Code");
            sb.AppendLine("```csharp");
            sb.AppendLine(GeneratedCode);
            sb.AppendLine("```");

            return sb.ToString();
        }
    }

    public class ExtractMethodResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string NewMethodName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string ExtractedCode { get; set; } = string.Empty;
        public string MethodCall { get; set; } = string.Empty;
        public string ModifiedContent { get; set; } = string.Empty;

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Extract Method: `{NewMethodName}`");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(Error))
            {
                sb.AppendLine($"**Error:** {Error}");
                return sb.ToString();
            }

            sb.AppendLine($"**File:** `{FilePath}`");
            sb.AppendLine($"**Method call:** `{MethodCall}`");
            sb.AppendLine();

            sb.AppendLine("## Extracted Code");
            sb.AppendLine("```csharp");
            sb.AppendLine(ExtractedCode);
            sb.AppendLine("```");

            sb.AppendLine();
            sb.AppendLine("*Use `codemerger_write_file` with the modified content to apply this change.*");

            return sb.ToString();
        }
    }

    #endregion
}
