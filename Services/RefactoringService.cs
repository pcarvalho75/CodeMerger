using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeMerger.Models;
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
        private readonly ProjectAnalysis _projectAnalysis;
        private readonly List<string> _inputDirectories;

        public RefactoringService(ProjectAnalysis projectAnalysis, List<string> inputDirectories)
        {
            _projectAnalysis = projectAnalysis;
            _inputDirectories = inputDirectories;
        }

        /// <summary>
        /// Write content to a file (create or overwrite).
        /// </summary>
        public WriteFileResult WriteFile(string relativePath, string content, bool createBackup = true)
        {
            var result = new WriteFileResult { RelativePath = relativePath };

            try
            {
                // Find the file or determine where to create it
                var existingFile = _projectAnalysis.AllFiles
                    .FirstOrDefault(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
                                        f.RelativePath.Replace('\\', '/').Equals(relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

                string fullPath;
                if (existingFile != null)
                {
                    fullPath = existingFile.FilePath;
                    result.IsNewFile = false;

                    // Create backup
                    if (createBackup && File.Exists(fullPath))
                    {
                        var backupPath = fullPath + ".bak";
                        File.Copy(fullPath, backupPath, overwrite: true);
                        result.BackupPath = backupPath;
                    }

                    // Generate diff
                    var oldContent = File.ReadAllText(fullPath);
                    result.Diff = GenerateDiff(oldContent, content, relativePath);
                }
                else
                {
                    // New file - use first input directory
                    var baseDir = _inputDirectories.FirstOrDefault() ?? Directory.GetCurrentDirectory();
                    fullPath = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    result.IsNewFile = true;

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }

                // Write the file
                File.WriteAllText(fullPath, content);
                result.FullPath = fullPath;
                result.Success = true;
                result.BytesWritten = content.Length;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
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

            var existingFile = _projectAnalysis.AllFiles
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

            foreach (var file in _projectAnalysis.AllFiles.Where(f => f.Extension == ".cs"))
            {
                try
                {
                    var content = File.ReadAllText(file.FilePath);

                    // Use word boundary matching to avoid partial replacements
                    var pattern = $@"\b{Regex.Escape(oldName)}\b";
                    var matches = Regex.Matches(content, pattern);

                    if (matches.Count > 0)
                    {
                        var newContent = Regex.Replace(content, pattern, newName);

                        affectedFiles[file.RelativePath] = new RenameFileChange
                        {
                            FilePath = file.RelativePath,
                            FullPath = file.FilePath,
                            OccurrenceCount = matches.Count,
                            Diff = GenerateDiff(content, newContent, file.RelativePath)
                        };

                        if (!preview)
                        {
                            // Create backup
                            File.Copy(file.FilePath, file.FilePath + ".bak", overwrite: true);
                            File.WriteAllText(file.FilePath, newContent);
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
            var classFile = _projectAnalysis.AllFiles
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

            var file = _projectAnalysis.AllFiles
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
        /// Generate a simple unified diff.
        /// </summary>
        private string GenerateDiff(string oldContent, string newContent, string fileName)
        {
            var oldLines = oldContent.Split('\n');
            var newLines = newContent.Split('\n');

            var sb = new StringBuilder();
            sb.AppendLine($"--- a/{fileName}");
            sb.AppendLine($"+++ b/{fileName}");

            int oldIdx = 0, newIdx = 0;

            while (oldIdx < oldLines.Length || newIdx < newLines.Length)
            {
                if (oldIdx >= oldLines.Length)
                {
                    sb.AppendLine($"+{newLines[newIdx]}");
                    newIdx++;
                }
                else if (newIdx >= newLines.Length)
                {
                    sb.AppendLine($"-{oldLines[oldIdx]}");
                    oldIdx++;
                }
                else if (oldLines[oldIdx].TrimEnd() == newLines[newIdx].TrimEnd())
                {
                    // Same line - skip in diff output for brevity
                    oldIdx++;
                    newIdx++;
                }
                else
                {
                    sb.AppendLine($"-{oldLines[oldIdx]}");
                    sb.AppendLine($"+{newLines[newIdx]}");
                    oldIdx++;
                    newIdx++;
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
