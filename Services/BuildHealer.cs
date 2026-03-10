using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeMerger.Services
{
    /// <summary>
    /// Classifies build errors and generates automatic fixes for common patterns.
    /// Integrates with CompilationService for type resolution (e.g., finding correct 'using' for missing types).
    /// </summary>
    public class BuildHealer
    {
        private readonly CompilationService? _compilationService;
        private readonly Action<string> _log;

        // Regex to extract type name from CS0246/CS0103 messages
        private static readonly Regex MissingTypePattern = new(
            @"The (?:type or namespace name|name) '(\w+)' (?:does not exist|could not be found)",
            RegexOptions.Compiled);

        // Regex to extract candidates from CS0104 messages
        private static readonly Regex AmbiguousPattern = new(
            @"'(\w+)' is an ambiguous reference between '([\w.]+)' and '([\w.]+)'",
            RegexOptions.Compiled);

        public BuildHealer(CompilationService? compilationService, Action<string> log)
        {
            _compilationService = compilationService;
            _log = log;
        }

        /// <summary>
        /// Classify a build error into a fixable category.
        /// </summary>
        public ErrorClassification Classify(BuildError error)
        {
            return error.ErrorCode switch
            {
                "CS0246" or "CS0103" => ClassifyMissingType(error),
                "CS0104" => new ErrorClassification
                {
                    Error = error,
                    Category = "ambiguous_reference",
                    IsAutoFixable = true,
                    Confidence = 0.85
                },
                "CS1002" => new ErrorClassification
                {
                    Error = error,
                    Category = "missing_semicolon",
                    IsAutoFixable = true,
                    Confidence = 0.95
                },
                "CS8600" or "CS8602" or "CS8603" or "CS8604" => new ErrorClassification
                {
                    Error = error,
                    Category = "null_warning",
                    IsAutoFixable = false, // Fix not yet implemented (needs column-precise editing)
                    Confidence = 0.3
                },
                "CS0534" => new ErrorClassification
                {
                    Error = error,
                    Category = "abstract_not_implemented",
                    IsAutoFixable = false, // Too complex for auto-fix without more context
                    Confidence = 0.5
                },
                "CS0535" => new ErrorClassification
                {
                    Error = error,
                    Category = "interface_not_implemented",
                    IsAutoFixable = false,
                    Confidence = 0.5
                },
                "CS0029" or "CS0266" => new ErrorClassification
                {
                    Error = error,
                    Category = "type_mismatch",
                    IsAutoFixable = false,
                    Confidence = 0.3
                },
                "CS0161" => new ErrorClassification
                {
                    Error = error,
                    Category = "missing_return",
                    IsAutoFixable = false,
                    Confidence = 0.0
                },
                _ => new ErrorClassification
                {
                    Error = error,
                    Category = "unknown",
                    IsAutoFixable = false,
                    Confidence = 0.0
                }
            };
        }

        /// <summary>
        /// Generate a fix for a classified error. Returns null if no fix can be generated.
        /// </summary>
        public HealSuggestion? GenerateFix(ErrorClassification classified)
        {
            return classified.Category switch
            {
                "missing_using" => FixMissingUsing(classified),
                "ambiguous_reference" => FixAmbiguousReference(classified.Error),
                "missing_semicolon" => FixMissingSemicolon(classified.Error),
                "null_warning" => FixNullWarning(classified.Error),
                _ => null
            };
        }

        /// <summary>
        /// Full heal loop: classify all errors, fix what we can, report results.
        /// Does NOT rebuild — the caller (Build method) handles the rebuild cycle.
        /// Returns the fixes to apply for this iteration.
        /// </summary>
        public HealIterationResult HealIteration(List<BuildError> errors)
        {
            var result = new HealIterationResult();

            foreach (var error in errors)
            {
                var classified = Classify(error);
                if (classified.IsAutoFixable && classified.Confidence >= 0.7)
                {
                    var fix = GenerateFix(classified);
                    if (fix != null)
                    {
                        result.Fixes.Add(fix);
                    }
                    else
                    {
                        result.Unfixable.Add(classified);
                    }
                }
                else
                {
                    result.Unfixable.Add(classified);
                }
            }

            // Deduplicate fixes (same file + same fix content)
            result.Fixes = result.Fixes
                .GroupBy(f => $"{f.FilePath}|{f.Description}")
                .Select(g => g.First())
                .ToList();

            return result;
        }

        /// <summary>
        /// Apply a single fix to the filesystem. Creates .bak backup before modifying.
        /// Returns true on success.
        /// </summary>
        public bool ApplyFix(HealSuggestion fix)
        {
            try
            {
                if (string.IsNullOrEmpty(fix.FilePath) || !File.Exists(fix.FilePath))
                {
                    _log($"BuildHealer: Cannot apply fix — file not found: {fix.FilePath}");
                    return false;
                }

                // Create .bak backup before modifying (only if one doesn't already exist for this heal cycle)
                var bakPath = fix.FilePath + ".heal.bak";
                if (!File.Exists(bakPath))
                {
                    File.Copy(fix.FilePath, bakPath, overwrite: false);
                }

                var content = File.ReadAllText(fix.FilePath);

                if (fix.FixType == FixType.InsertLine)
                {
                    if (string.IsNullOrEmpty(fix.OldText))
                    {
                        // Insert at top of file
                        content = fix.NewText + content;
                    }
                    else
                    {
                        // Insert after the marker
                        var idx = content.IndexOf(fix.OldText, StringComparison.Ordinal);
                        if (idx < 0)
                        {
                            _log($"BuildHealer: Marker not found in {Path.GetFileName(fix.FilePath)}");
                            return false;
                        }
                        var endOfMarker = idx + fix.OldText.Length;
                        content = content.Substring(0, endOfMarker) + fix.NewText + content.Substring(endOfMarker);
                    }
                }
                else // FixType.Replace
                {
                    if (!content.Contains(fix.OldText, StringComparison.Ordinal))
                    {
                        _log($"BuildHealer: Target text not found in {Path.GetFileName(fix.FilePath)}");
                        return false;
                    }
                    content = content.Replace(fix.OldText, fix.NewText);
                }

                File.WriteAllText(fix.FilePath, content);
                _log($"BuildHealer: Applied fix — {fix.Description} in {Path.GetFileName(fix.FilePath)}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"BuildHealer: Error applying fix: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Rollback all files that were modified during healing by restoring from .heal.bak backups.
        /// Called when error count increases after a heal attempt.
        /// </summary>
        public int RollbackFixes(List<HealSuggestion> appliedFixes)
        {
            var rolledBack = 0;
            var restoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fix in appliedFixes)
            {
                if (string.IsNullOrEmpty(fix.FilePath) || restoredFiles.Contains(fix.FilePath))
                    continue;

                var bakPath = fix.FilePath + ".heal.bak";
                if (File.Exists(bakPath))
                {
                    try
                    {
                        File.Copy(bakPath, fix.FilePath, overwrite: true);
                        restoredFiles.Add(fix.FilePath);
                        rolledBack++;
                        _log($"BuildHealer: Rolled back {Path.GetFileName(fix.FilePath)}");
                    }
                    catch (Exception ex)
                    {
                        _log($"BuildHealer: Rollback failed for {Path.GetFileName(fix.FilePath)}: {ex.Message}");
                    }
                }
            }

            return rolledBack;
        }

        /// <summary>
        /// Clean up .heal.bak files after a successful heal or after rollback.
        /// </summary>
        public void CleanupHealBackups(List<HealSuggestion> fixes)
        {
            var cleaned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fix in fixes)
            {
                if (string.IsNullOrEmpty(fix.FilePath) || cleaned.Contains(fix.FilePath))
                    continue;

                var bakPath = fix.FilePath + ".heal.bak";
                if (File.Exists(bakPath))
                {
                    try
                    {
                        File.Delete(bakPath);
                        cleaned.Add(fix.FilePath);
                    }
                    catch { }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Fix Generators
        // ────────────────────────────────────────────────────────────────────

        private ErrorClassification ClassifyMissingType(BuildError error)
        {
            var match = MissingTypePattern.Match(error.Message);
            if (!match.Success)
            {
                return new ErrorClassification
                {
                    Error = error,
                    Category = "unknown_missing_type",
                    IsAutoFixable = false,
                    Confidence = 0.0
                };
            }

            var typeName = match.Groups[1].Value;

            // Check if CompilationService can find the namespace
            if (_compilationService?.IsAvailable == true)
            {
                var ns = _compilationService.FindNamespaceForType(typeName);
                if (ns != null)
                {
                    return new ErrorClassification
                    {
                        Error = error,
                        Category = "missing_using",
                        IsAutoFixable = true,
                        Confidence = 0.9,
                        ResolvedData = ns // Store the namespace for the fix generator
                    };
                }
            }

            // Try well-known types as fallback
            var knownNs = FindWellKnownNamespace(typeName);
            if (knownNs != null)
            {
                return new ErrorClassification
                {
                    Error = error,
                    Category = "missing_using",
                    IsAutoFixable = true,
                    Confidence = 0.75,
                    ResolvedData = knownNs
                };
            }

            return new ErrorClassification
            {
                Error = error,
                Category = "missing_type_unresolved",
                IsAutoFixable = false,
                Confidence = 0.0
            };
        }

        private HealSuggestion? FixMissingUsing(ErrorClassification classified)
        {
            var error = classified.Error;
            var match = MissingTypePattern.Match(error.Message);
            if (!match.Success || string.IsNullOrEmpty(error.FullPath)) return null;

            var typeName = match.Groups[1].Value;

            // Use pre-resolved namespace from classification first, then fall back to fresh lookup
            string? ns = classified.ResolvedData;
            if (ns == null && _compilationService?.IsAvailable == true)
                ns = _compilationService.FindNamespaceForType(typeName);
            if (ns == null)
                ns = FindWellKnownNamespace(typeName);
            if (ns == null) return null;

            // Read the file and find insertion point
            try
            {
                var content = File.ReadAllText(error.FullPath);
                var usingStatement = $"using {ns};";

                // Check if already present
                if (content.Contains(usingStatement)) return null;

                // Find the last using statement to insert after
                var lastUsing = FindLastUsingStatement(content);
                if (lastUsing != null)
                {
                    return new HealSuggestion
                    {
                        Error = error,
                        FilePath = error.FullPath,
                        Description = $"Add '{usingStatement}'",
                        FixType = FixType.InsertLine,
                        OldText = lastUsing,
                        NewText = $"\n{usingStatement}",
                        Confidence = 0.9
                    };
                }

                // No using statements — insert at top of file
                return new HealSuggestion
                {
                    Error = error,
                    FilePath = error.FullPath,
                    Description = $"Add '{usingStatement}' at top",
                    FixType = FixType.InsertLine,
                    OldText = "", // Will be handled specially
                    NewText = $"{usingStatement}\n",
                    Confidence = 0.8
                };
            }
            catch { return null; }
        }

        private HealSuggestion? FixAmbiguousReference(BuildError error)
        {
            var match = AmbiguousPattern.Match(error.Message);
            if (!match.Success || string.IsNullOrEmpty(error.FullPath)) return null;

            var shortName = match.Groups[1].Value;
            var candidate1 = match.Groups[2].Value;
            var candidate2 = match.Groups[3].Value;

            // Pick the best candidate based on existing usings in the file
            try
            {
                var content = File.ReadAllText(error.FullPath);
                var bestCandidate = PickBestCandidate(content, candidate1, candidate2);

                // Read the source line and replace the bare name with the fully qualified one
                var lines = content.Split('\n');
                if (error.Line > 0 && error.Line <= lines.Length)
                {
                    var line = lines[error.Line - 1];
                    // Simple replacement of the bare name — only in this specific line
                    var newLine = ReplaceFirstOccurrence(line, shortName, bestCandidate);
                    if (newLine != line)
                    {
                        return new HealSuggestion
                        {
                            Error = error,
                            FilePath = error.FullPath,
                            Description = $"Fully qualify '{shortName}' as '{bestCandidate}'",
                            FixType = FixType.Replace,
                            OldText = line,
                            NewText = newLine,
                            Confidence = 0.8
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        private HealSuggestion? FixMissingSemicolon(BuildError error)
        {
            if (string.IsNullOrEmpty(error.FullPath) || error.Line <= 0) return null;

            try
            {
                var lines = File.ReadAllLines(error.FullPath);
                if (error.Line > lines.Length) return null;

                var line = lines[error.Line - 1];
                var trimmed = line.TrimEnd();

                // Only add semicolon if the line doesn't already end with one and isn't a block opener
                if (!trimmed.EndsWith(";") && !trimmed.EndsWith("{") && !trimmed.EndsWith("}") && !trimmed.EndsWith(","))
                {
                    return new HealSuggestion
                    {
                        Error = error,
                        FilePath = error.FullPath,
                        Description = $"Add missing semicolon at line {error.Line}",
                        FixType = FixType.Replace,
                        OldText = line,
                        NewText = trimmed + ";" + line.Substring(trimmed.Length), // Preserve trailing whitespace
                        Confidence = 0.9
                    };
                }
            }
            catch { }

            return null;
        }

        private HealSuggestion? FixNullWarning(BuildError error)
        {
            // CS8600: Converting null literal or possible null value to non-nullable type
            // CS8602: Dereference of a possibly null reference
            // CS8603: Possible null reference return
            // CS8604: Possible null reference argument
            // Fix: add null-forgiving operator (!) at the error location
            // This is a lighter touch — just suppresses the warning at the specific location.
            // Not ideal for production but gets the build green.

            if (string.IsNullOrEmpty(error.FullPath) || error.Line <= 0) return null;

            // Null fixes are lower confidence — only suggest, don't auto-apply by default
            return null; // TODO: implement when we have column-precise editing
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Find the last 'using X;' statement in a file (for insertion point).
        /// </summary>
        private string? FindLastUsingStatement(string content)
        {
            string? lastUsing = null;
            var lines = content.Split('\n');

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("("))
                {
                    lastUsing = line.TrimEnd('\r'); // Keep exact line for matching
                }
                else if (trimmed.StartsWith("namespace ") || trimmed.StartsWith("[") ||
                         (trimmed.StartsWith("public ") || trimmed.StartsWith("internal ") ||
                          trimmed.StartsWith("class ") || trimmed.StartsWith("static ")))
                {
                    break; // Past the using block
                }
            }

            return lastUsing;
        }

        /// <summary>
        /// Pick the better of two ambiguous type candidates based on existing usings.
        /// </summary>
        private string PickBestCandidate(string fileContent, string candidate1, string candidate2)
        {
            var ns1 = candidate1.Substring(0, candidate1.LastIndexOf('.'));
            var ns2 = candidate2.Substring(0, candidate2.LastIndexOf('.'));

            bool has1 = fileContent.Contains($"using {ns1};");
            bool has2 = fileContent.Contains($"using {ns2};");

            if (has1 && !has2) return candidate1;
            if (has2 && !has1) return candidate2;

            // Both or neither — prefer System namespace
            if (candidate1.StartsWith("System")) return candidate1;
            if (candidate2.StartsWith("System")) return candidate2;

            // Prefer shorter
            return candidate1.Length <= candidate2.Length ? candidate1 : candidate2;
        }

        /// <summary>
        /// Replace only the first occurrence of a word in a string.
        /// </summary>
        private string ReplaceFirstOccurrence(string text, string oldValue, string newValue)
        {
            // Use word boundary to avoid replacing partial matches, limit to first match only
            var pattern = $@"\b{Regex.Escape(oldValue)}\b";
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return regex.Replace(text, newValue, count: 1);
        }

        /// <summary>
        /// Well-known type→namespace mappings for common cases when compilation isn't available.
        /// </summary>
        private string? FindWellKnownNamespace(string typeName)
        {
            return typeName switch
            {
                "List" or "Dictionary" or "HashSet" or "Queue" or "Stack" or "LinkedList"
                    => "System.Collections.Generic",
                "ConcurrentDictionary" or "ConcurrentBag" or "ConcurrentQueue"
                    => "System.Collections.Concurrent",
                "Regex" or "Match" => "System.Text.RegularExpressions",
                "StringBuilder" => "System.Text",
                "JsonSerializer" or "JsonElement" or "JsonSerializerOptions"
                    => "System.Text.Json",
                "JsonProperty" or "JsonConverter" => "System.Text.Json.Serialization",
                "Task" or "CancellationToken" or "CancellationTokenSource"
                    => "System.Threading.Tasks",
                "Thread" or "Mutex" or "Semaphore" or "Timer"
                    => "System.Threading",
                "File" or "Directory" or "Path" or "Stream" or "StreamReader" or "StreamWriter"
                    or "FileInfo" or "DirectoryInfo" or "FileStream" or "MemoryStream"
                    => "System.IO",
                "Process" or "ProcessStartInfo" or "Stopwatch"
                    => "System.Diagnostics",
                "HttpClient" or "HttpResponseMessage" => "System.Net.Http",
                "IPAddress" or "IPEndPoint" => "System.Net",
                "XDocument" or "XElement" or "XAttribute" => "System.Xml.Linq",
                "Debug" or "Trace" => "System.Diagnostics",
                "ObservableCollection" => "System.Collections.ObjectModel",
                "INotifyPropertyChanged" => "System.ComponentModel",
                "Encoding" => "System.Text",
                "Assembly" => "System.Reflection",
                "SyntaxTree" or "SyntaxNode" or "CSharpSyntaxTree" or "CSharpCompilation"
                    => "Microsoft.CodeAnalysis.CSharp",
                "SemanticModel" or "ISymbol" or "INamedTypeSymbol" or "Diagnostic" or "MetadataReference"
                    => "Microsoft.CodeAnalysis",
                _ => null
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Build Healer Models
    // ────────────────────────────────────────────────────────────────────

    public class BuildError
    {
        public string FilePath { get; set; } = ""; // Relative display path
        public string? FullPath { get; set; }       // Absolute path for file operations
        public int Line { get; set; }
        public int Column { get; set; }
        public string ErrorCode { get; set; } = "";
        public string Message { get; set; } = "";
        public string Display { get; set; } = "";   // Original formatted display string
    }

    public class ErrorClassification
    {
        public BuildError Error { get; set; } = new();
        public string Category { get; set; } = "unknown";
        public bool IsAutoFixable { get; set; }
        public double Confidence { get; set; }
        public string? ResolvedData { get; set; } // Extra data from classification (e.g., namespace)
    }

    public enum FixType
    {
        Replace,    // Replace OldText with NewText
        InsertLine  // Insert NewText after OldText
    }

    public class HealSuggestion
    {
        public BuildError Error { get; set; } = new();
        public string FilePath { get; set; } = "";
        public string Description { get; set; } = "";
        public FixType FixType { get; set; } = FixType.Replace;
        public string OldText { get; set; } = "";
        public string NewText { get; set; } = "";
        public double Confidence { get; set; }
    }

    public class HealIterationResult
    {
        public List<HealSuggestion> Fixes { get; set; } = new();
        public List<ErrorClassification> Unfixable { get; set; } = new();
    }
}
