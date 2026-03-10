using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMerger.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services
{
    /// <summary>
    /// Applies high-level refactoring intents across the entire codebase in a single operation.
    /// Each intent scans the workspace, finds all targets, and generates diffs.
    /// </summary>
    public class IntentRefactoringEngine
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly CompilationService? _compilationService;
        private readonly Dictionary<string, IIntentHandler> _handlers;
        private string? _customRulesError;

        private const string RulesFileName = "CODEMERGER_RULES.json";

        public IntentRefactoringEngine(
            WorkspaceAnalysis workspaceAnalysis,
            CompilationService? compilationService,
            List<string>? inputDirectories = null)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _compilationService = compilationService;

            _handlers = new Dictionary<string, IIntentHandler>(StringComparer.OrdinalIgnoreCase)
            {
                ["add_xml_doc"] = new AddXmlDocHandler(),
                ["add_null_checks"] = new AddNullChecksHandler(),
                ["extract_interfaces"] = new ExtractInterfacesHandler(),
                ["enforce_async_naming"] = new EnforceAsyncNamingHandler(),
                ["add_sealed"] = new AddSealedHandler()
            };

            // Load custom rules from CODEMERGER_RULES.json
            if (inputDirectories != null)
            {
                LoadCustomRules(inputDirectories);
            }
        }

        /// <summary>
        /// Load custom intent rules from CODEMERGER_RULES.json in the workspace.
        /// </summary>
        private void LoadCustomRules(List<string> inputDirectories)
        {
            foreach (var dir in inputDirectories)
            {
                var rulesPath = Path.Combine(dir, RulesFileName);
                if (!File.Exists(rulesPath)) continue;

                try
                {
                    var json = File.ReadAllText(rulesPath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    var rulesFile = JsonSerializer.Deserialize<CustomRulesFile>(json, options);
                    if (rulesFile?.CustomIntents == null) continue;

                    foreach (var rule in rulesFile.CustomIntents)
                    {
                        if (string.IsNullOrEmpty(rule.Name) || string.IsNullOrEmpty(rule.FindPattern))
                            continue;

                        var handler = new CustomRuleIntentHandler(rule);
                        _handlers[rule.Name] = handler;
                    }
                }
                catch (Exception ex)
                {
                    _customRulesError = $"Failed to load {RulesFileName}: {ex.Message}";
                }

                break; // Only load from first directory that has the file
            }
        }

        /// <summary>
        /// Apply a pattern/intent across the codebase.
        /// </summary>
        public IntentResult ApplyPattern(string intent, string? scope, bool preview, int maxTargets)
        {
            var handler = ResolveHandler(intent);
            if (handler == null)
            {
                return new IntentResult
                {
                    IntentName = "unresolved",
                    Summary = $"Could not match intent '{intent}'. Available intents:\n" +
                        string.Join("\n", _handlers.Values.Select(h => $"- **{h.Name}**: {h.Description}"))
                };
            }

            // Pre-check: does the scope match any files?
            var scopedFiles = FilterByScope(_workspaceAnalysis, scope);
            if (scopedFiles.Count == 0)
            {
                return new IntentResult
                {
                    IntentName = handler.Name,
                    Summary = $"No files match the scope '{scope}'. Check namespace or path filter."
                };
            }

            var context = new IntentContext
            {
                WorkspaceAnalysis = _workspaceAnalysis,
                CompilationService = _compilationService,
                ScopeFilter = scope,
                PreviewOnly = preview,
                MaxTargets = maxTargets
            };

            var result = handler.Execute(context);
            result.IntentName = handler.Name;
            return result;
        }

        /// <summary>
        /// List all available intents.
        /// </summary>
        public string ListIntents()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Available Intents");
            sb.AppendLine();

            var builtIn = _handlers.Values.Where(h => h is not CustomRuleIntentHandler).ToList();
            var custom = _handlers.Values.OfType<CustomRuleIntentHandler>().ToList();

            if (builtIn.Count > 0)
            {
                sb.AppendLine("## Built-in");
                foreach (var handler in builtIn)
                {
                    sb.AppendLine($"### `{handler.Name}`");
                    sb.AppendLine(handler.Description);
                    sb.AppendLine($"**Keywords:** {string.Join(", ", handler.Keywords)}");
                    sb.AppendLine();
                }
            }

            if (custom.Count > 0)
            {
                sb.AppendLine("## Custom (from CODEMERGER_RULES.json)");
                foreach (var handler in custom)
                {
                    sb.AppendLine($"### `{handler.Name}`");
                    sb.AppendLine(handler.Description);
                    sb.AppendLine($"**Keywords:** {string.Join(", ", handler.Keywords)}");
                    sb.AppendLine();
                }
            }
            else if (!string.IsNullOrEmpty(_customRulesError))
            {
                sb.AppendLine($"## Custom Rules Error");
                sb.AppendLine($"⚠️ {_customRulesError}");
            }
            else
            {
                sb.AppendLine("*No custom intents loaded. Create `CODEMERGER_RULES.json` in your workspace root to add custom rules.*");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Match an intent string to a handler via keyword matching.
        /// </summary>
        private IIntentHandler? ResolveHandler(string intent)
        {
            if (intent.Equals("list", StringComparison.OrdinalIgnoreCase))
                return null; // Special case handled by caller

            var lower = intent.ToLowerInvariant();

            // Exact name match first
            if (_handlers.TryGetValue(lower, out var exactMatch))
                return exactMatch;

            // Keyword match
            foreach (var handler in _handlers.Values)
            {
                if (handler.Keywords.Any(k => lower.Contains(k)))
                    return handler;
            }

            return null;
        }

        /// <summary>
        /// Format an IntentResult as markdown.
        /// </summary>
        public static string ToMarkdown(IntentResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Intent: `{result.IntentName}`");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(result.Summary))
            {
                sb.AppendLine(result.Summary);
                sb.AppendLine();
            }

            if (result.Changes.Count == 0 && result.TargetsFound == 0)
            {
                sb.AppendLine("No targets found matching this intent.");
                return sb.ToString();
            }

            sb.AppendLine($"**Targets found:** {result.TargetsFound}");
            sb.AppendLine($"**Changes generated:** {result.Changes.Count}");
            sb.AppendLine();

            foreach (var change in result.Changes)
            {
                sb.AppendLine($"### `{change.FilePath}`");
                sb.AppendLine(change.Description);
                sb.AppendLine();
                if (!string.IsNullOrEmpty(change.Diff))
                {
                    sb.AppendLine("```diff");
                    // Limit diff size per change
                    var diff = change.Diff.Length > 2000
                        ? change.Diff.Substring(0, 2000) + "\n... (truncated)"
                        : change.Diff;
                    sb.AppendLine(diff);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            if (result.TargetsFound > result.Changes.Count)
            {
                sb.AppendLine($"*{result.TargetsFound - result.Changes.Count} additional targets not shown (maxTargets limit).*");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Filter files by scope (namespace or glob pattern).
        /// </summary>
        internal static List<FileAnalysis> FilterByScope(WorkspaceAnalysis workspace, string? scope)
        {
            if (string.IsNullOrEmpty(scope) || scope == "all")
                return workspace.AllFiles;

            return workspace.AllFiles.Where(f =>
            {
                // Namespace match
                if (!string.IsNullOrEmpty(f.Namespace) &&
                    f.Namespace.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Path glob match
                if (f.RelativePath.Contains(scope, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }).ToList();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent Infrastructure
    // ────────────────────────────────────────────────────────────────────

    public interface IIntentHandler
    {
        string Name { get; }
        string Description { get; }
        string[] Keywords { get; }
        IntentResult Execute(IntentContext context);
    }

    public class IntentContext
    {
        public WorkspaceAnalysis WorkspaceAnalysis { get; set; } = null!;
        public CompilationService? CompilationService { get; set; }
        public string? ScopeFilter { get; set; }
        public bool PreviewOnly { get; set; } = true;
        public int MaxTargets { get; set; } = 50;
    }

    public class IntentResult
    {
        public string IntentName { get; set; } = "";
        public int TargetsFound { get; set; }
        public int TargetsModified { get; set; }
        public List<IntentFileChange> Changes { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    public class IntentFileChange
    {
        public string FilePath { get; set; } = "";
        public string Diff { get; set; } = "";
        public string Description { get; set; } = "";
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent 1: Add XML Doc Comments
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add XML doc comments to all public methods missing them.
    /// </summary>
    public class AddXmlDocHandler : IIntentHandler
    {
        public string Name => "add_xml_doc";
        public string Description => "Add XML doc comments to all public methods/properties missing them.";
        public string[] Keywords => new[] { "xml doc", "doc comment", "document", "summary", "/// " };

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);
            var csFiles = files.Where(f => f.Extension == ".cs").ToList();

            foreach (var file in csFiles)
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                var targets = new List<(CodeTypeInfo type, CodeMemberInfo member)>();

                foreach (var type in file.Types)
                {
                    foreach (var member in type.Members)
                    {
                        if (member.AccessModifier != "public") continue;
                        if (member.Kind != CodeMemberKind.Method && member.Kind != CodeMemberKind.Property) continue;
                        if (!string.IsNullOrEmpty(member.XmlDoc)) continue;

                        targets.Add((type, member));
                        result.TargetsFound++;
                    }
                }

                if (targets.Count == 0) continue;

                // Generate the diff
                try
                {
                    var content = File.ReadAllText(file.FilePath);
                    var lines = content.Split('\n');
                    var insertions = new List<(int line, string comment)>();

                    foreach (var (type, member) in targets)
                    {
                        if (member.StartLine <= 0 || member.StartLine > lines.Length) continue;

                        // Detect indent from the member's declaration line
                        var memberLine = lines[member.StartLine - 1];
                        var indent = memberLine.Substring(0, memberLine.Length - memberLine.TrimStart().Length);

                        var docComment = GenerateDocComment(member, indent);
                        insertions.Add((member.StartLine, docComment));
                    }

                    if (insertions.Count > 0)
                    {
                        var diffSb = new StringBuilder();
                        foreach (var (line, comment) in insertions.OrderBy(i => i.line))
                        {
                            diffSb.AppendLine($"+ Line {line}: Insert before member");
                            foreach (var docLine in comment.Split('\n'))
                                diffSb.AppendLine($"+   {docLine}");
                        }

                        result.Changes.Add(new IntentFileChange
                        {
                            FilePath = file.RelativePath,
                            Description = $"{insertions.Count} public member(s) need doc comments",
                            Diff = diffSb.ToString()
                        });

                        // Apply if not preview
                        if (!context.PreviewOnly)
                        {
                            ApplyDocComments(file.FilePath, lines, insertions);
                            result.TargetsModified += insertions.Count;
                        }
                    }
                }
                catch { }
            }

            result.Summary = context.PreviewOnly
                ? $"Preview: {result.TargetsFound} public members need doc comments across {result.Changes.Count} files."
                : $"Added doc comments to {result.TargetsModified} members across {result.Changes.Count} files.";

            return result;
        }

        private string GenerateDocComment(CodeMemberInfo member, string indent)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// TODO: Document {member.Name}.");
            sb.AppendLine($"{indent}/// </summary>");

            if (member.Parameters != null)
            {
                foreach (var param in member.Parameters)
                {
                    // param format is typically "type name"
                    var parts = param.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var paramName = parts.Length >= 2 ? parts[^1] : param.Trim();
                    sb.AppendLine($"{indent}/// <param name=\"{paramName}\">TODO</param>");
                }
            }

            if (!string.IsNullOrEmpty(member.ReturnType) && member.ReturnType != "void" && member.Kind == CodeMemberKind.Method)
            {
                sb.AppendLine($"{indent}/// <returns>TODO</returns>");
            }

            return sb.ToString().TrimEnd('\n', '\r');
        }

        private void ApplyDocComments(string filePath, string[] lines, List<(int line, string comment)> insertions)
        {
            // Apply bottom-up so line numbers stay valid
            var sorted = insertions.OrderByDescending(i => i.line).ToList();
            var lineList = new List<string>(lines);

            foreach (var (line, comment) in sorted)
            {
                var idx = line - 1; // 0-indexed
                if (idx >= 0 && idx <= lineList.Count)
                {
                    var commentLines = comment.Split('\n');
                    lineList.InsertRange(idx, commentLines);
                }
            }

            File.WriteAllText(filePath, string.Join('\n', lineList));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent 2: Add Null Checks
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add ArgumentNullException.ThrowIfNull for reference-type parameters on public methods.
    /// </summary>
    public class AddNullChecksHandler : IIntentHandler
    {
        public string Name => "add_null_checks";
        public string Description => "Add null argument checks (ArgumentNullException.ThrowIfNull) to public methods with reference-type parameters.";
        public string[] Keywords => new[] { "null check", "null guard", "argument null", "throwifnull", "null validation" };

        private static readonly HashSet<string> ValueTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "int", "long", "short", "byte", "float", "double", "decimal", "bool",
            "char", "uint", "ulong", "ushort", "sbyte", "nint", "nuint",
            "Int32", "Int64", "Int16", "Byte", "Single", "Double", "Decimal", "Boolean",
            "Char", "UInt32", "UInt64", "UInt16", "SByte",
            "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "DateOnly", "TimeOnly",
            "CancellationToken"
        };

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);
            var csFiles = files.Where(f => f.Extension == ".cs").ToList();

            foreach (var file in csFiles)
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                var fileTargets = new List<(CodeMemberInfo member, List<string> paramsToCheck)>();

                foreach (var type in file.Types)
                {
                    foreach (var member in type.Members)
                    {
                        if (member.AccessModifier != "public") continue;
                        if (member.Kind != CodeMemberKind.Method && member.Kind != CodeMemberKind.Constructor) continue;
                        if (member.Parameters == null || member.Parameters.Count == 0) continue;

                        var referenceParams = member.Parameters
                            .Select(p => ParseParameter(p))
                            .Where(p => p.name != null && IsReferenceType(p.type))
                            .Select(p => p.name!)
                            .ToList();

                        if (referenceParams.Count == 0) continue;

                        // Check if the method body already has null checks
                        if (!string.IsNullOrEmpty(member.Body))
                        {
                            referenceParams = referenceParams
                                .Where(p => !member.Body.Contains($"ThrowIfNull({p}") &&
                                           !member.Body.Contains($"ArgumentNullException") &&
                                           !member.Body.Contains($"{p} == null") &&
                                           !member.Body.Contains($"{p} is null"))
                                .ToList();
                        }

                        if (referenceParams.Count > 0)
                        {
                            fileTargets.Add((member, referenceParams));
                            result.TargetsFound++;
                        }
                    }
                }

                if (fileTargets.Count == 0) continue;

                var diffSb = new StringBuilder();
                foreach (var (member, paramsToCheck) in fileTargets)
                {
                    diffSb.AppendLine($"+ {member.Name} (line {member.StartLine}): Add null checks for: {string.Join(", ", paramsToCheck)}");
                    foreach (var p in paramsToCheck)
                        diffSb.AppendLine($"+     ArgumentNullException.ThrowIfNull({p});");
                }

                result.Changes.Add(new IntentFileChange
                {
                    FilePath = file.RelativePath,
                    Description = $"{fileTargets.Count} method(s) need null checks",
                    Diff = diffSb.ToString()
                });

                // Apply if not preview
                if (!context.PreviewOnly)
                {
                    try
                    {
                        ApplyNullChecks(file.FilePath, fileTargets);
                        result.TargetsModified += fileTargets.Count;
                    }
                    catch { }
                }
            }

            result.Summary = context.PreviewOnly
                ? $"Preview: {result.TargetsFound} methods need null checks across {result.Changes.Count} files."
                : $"Added null checks to {result.TargetsModified} methods across {result.Changes.Count} files.";

            return result;
        }

        private (string type, string? name) ParseParameter(string param)
        {
            var trimmed = param.Trim();
            // Handle "this Type name" for extension methods
            if (trimmed.StartsWith("this ")) trimmed = trimmed.Substring(5);
            // Handle "params Type[] name"
            if (trimmed.StartsWith("params ")) trimmed = trimmed.Substring(7);

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0], parts[^1].TrimEnd(','));
            return (trimmed, null);
        }

        private bool IsReferenceType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            // Nullable value types are not reference types
            if (typeName.EndsWith("?") && ValueTypes.Contains(typeName.TrimEnd('?'))) return false;
            // Known value types
            if (ValueTypes.Contains(typeName)) return false;
            // Enum-like short names are ambiguous, but default to reference type
            return true;
        }

        private void ApplyNullChecks(string filePath, List<(CodeMemberInfo member, List<string> paramsToCheck)> targets)
        {
            var content = File.ReadAllText(filePath);

            // Sort targets by descending line so edits don't shift earlier targets
            foreach (var (member, paramsToCheck) in targets.OrderByDescending(t => t.member.StartLine))
            {
                // Split once per iteration (content changes after each insertion)
                var lineList = new List<string>(content.Split('\n'));

                // Find the opening brace of the method body
                var braceLineIdx = -1;
                for (int i = member.StartLine - 1; i < Math.Min(member.StartLine + 5, lineList.Count); i++)
                {
                    if (lineList[i].TrimStart().StartsWith("{"))
                    {
                        braceLineIdx = i;
                        break;
                    }
                }

                if (braceLineIdx < 0) continue;

                // Detect indentation from the opening brace line + one level
                var braceLine = lineList[braceLineIdx];
                var baseIndent = braceLine.Substring(0, braceLine.Length - braceLine.TrimStart().Length);
                var bodyIndent = baseIndent + "    "; // One level deeper than the brace
                var checks = string.Join("\n", paramsToCheck.Select(p => $"{bodyIndent}ArgumentNullException.ThrowIfNull({p});"));

                // Insert after the opening brace
                lineList.Insert(braceLineIdx + 1, checks);
                content = string.Join('\n', lineList);
            }

            File.WriteAllText(filePath, content);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent 3: Extract Interfaces
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate interfaces for service/handler classes that don't have one.
    /// </summary>
    public class ExtractInterfacesHandler : IIntentHandler
    {
        public string Name => "extract_interfaces";
        public string Description => "Generate interfaces for service/handler classes that don't already implement one.";
        public string[] Keywords => new[] { "extract interface", "generate interface", "create interface", "add interface" };

        private static readonly string[] ServiceSuffixes = { "Service", "Handler", "Repository", "Manager", "Provider", "Factory" };

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);
            var csFiles = files.Where(f => f.Extension == ".cs").ToList();

            foreach (var file in csFiles)
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                foreach (var type in file.Types)
                {
                    if (type.Kind != CodeTypeKind.Class) continue;
                    if (!ServiceSuffixes.Any(s => type.Name.EndsWith(s))) continue;

                    // Check if it already implements a corresponding interface
                    var expectedInterface = $"I{type.Name}";
                    if (type.Interfaces.Any(i => i.Equals(expectedInterface, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Get public members for the interface
                    var publicMembers = type.Members
                        .Where(m => m.AccessModifier == "public" &&
                                   (m.Kind == CodeMemberKind.Method || m.Kind == CodeMemberKind.Property) &&
                                   m.Name != type.Name) // Skip constructors
                        .ToList();

                    if (publicMembers.Count == 0) continue;

                    result.TargetsFound++;

                    // Generate interface code
                    var ifaceSb = new StringBuilder();
                    ifaceSb.AppendLine($"    public interface {expectedInterface}");
                    ifaceSb.AppendLine("    {");
                    foreach (var member in publicMembers)
                    {
                        if (member.Kind == CodeMemberKind.Method)
                        {
                            var sig = member.Signature ?? member.Name + "()";
                            // Strip access modifier keywords from signature
                            sig = sig.Replace("public ", "").Replace("virtual ", "").Replace("override ", "")
                                     .Replace("static ", "").Replace("sealed ", "").Trim();
                            // Signature is "Name(params)" — prepend return type for valid C#
                            var returnType = member.ReturnType ?? "void";
                            ifaceSb.AppendLine($"        {returnType} {sig};");
                        }
                        else if (member.Kind == CodeMemberKind.Property)
                        {
                            ifaceSb.AppendLine($"        {member.ReturnType} {member.Name} {{ get; set; }}");
                        }
                    }
                    ifaceSb.AppendLine("    }");

                    result.Changes.Add(new IntentFileChange
                    {
                        FilePath = file.RelativePath,
                        Description = $"Generate `{expectedInterface}` from `{type.Name}` ({publicMembers.Count} members) and add `: {expectedInterface}` to class",
                        Diff = ifaceSb.ToString()
                    });
                }
            }

            result.Summary = context.PreviewOnly
                ? $"Preview: {result.TargetsFound} service classes could have interfaces extracted."
                : $"Note: Interface extraction generates code but does not auto-apply (complex multi-file operation). Use the diffs above as guidance.";

            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent 4: Enforce Async Naming
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find async methods missing the Async suffix.
    /// </summary>
    public class EnforceAsyncNamingHandler : IIntentHandler
    {
        public string Name => "enforce_async_naming";
        public string Description => "Find async methods that don't have the 'Async' suffix and suggest renaming them.";
        public string[] Keywords => new[] { "async naming", "async suffix", "rename async", "async convention" };

        private static readonly HashSet<string> ExemptNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Main", "OnInitialized", "OnAfterRender", "Dispose", "DisposeAsync",
            "OnNavigatedTo", "OnNavigatedFrom", "OnAppearing", "OnDisappearing"
        };

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);
            var csFiles = files.Where(f => f.Extension == ".cs").ToList();

            var renames = new List<(string file, string oldName, string newName, int line)>();

            foreach (var file in csFiles)
            {
                foreach (var type in file.Types)
                {
                    foreach (var member in type.Members)
                    {
                        if (member.Kind != CodeMemberKind.Method) continue;
                        if (!member.IsAsync) continue;
                        if (member.Name.EndsWith("Async")) continue;
                        if (ExemptNames.Contains(member.Name)) continue;
                        // Skip event handlers (common pattern: void OnXxx)
                        if (member.ReturnType == "void") continue;

                        result.TargetsFound++;
                        renames.Add((file.RelativePath, member.Name, member.Name + "Async", member.StartLine));
                    }
                }
            }

            // Group by file
            foreach (var group in renames.GroupBy(r => r.file))
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                var diffSb = new StringBuilder();
                foreach (var (_, oldName, newName, line) in group)
                {
                    diffSb.AppendLine($"- Line {line}: `{oldName}` → `{newName}`");
                }

                result.Changes.Add(new IntentFileChange
                {
                    FilePath = group.Key,
                    Description = $"{group.Count()} async method(s) missing 'Async' suffix",
                    Diff = diffSb.ToString()
                });
            }

            result.Summary = context.PreviewOnly
                ? $"Preview: {result.TargetsFound} async methods are missing the 'Async' suffix. Use `rename_symbol` to apply each rename safely."
                : $"Note: Async renames require `rename_symbol` for safe cross-file updates. Use the list above as guidance.";

            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Intent 5: Add Sealed Modifier
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add 'sealed' to classes that are not inherited from and not abstract.
    /// </summary>
    public class AddSealedHandler : IIntentHandler
    {
        public string Name => "add_sealed";
        public string Description => "Add 'sealed' modifier to classes that have no derived classes and are not abstract.";
        public string[] Keywords => new[] { "sealed", "seal class", "add sealed", "no inheritance" };

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);
            var csFiles = files.Where(f => f.Extension == ".cs").ToList();

            // Build set of all base types (classes that are inherited from)
            var baseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in context.WorkspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    if (!string.IsNullOrEmpty(type.BaseType))
                        baseTypes.Add(type.BaseType);
                }
            }

            foreach (var file in csFiles)
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                var targets = new List<CodeTypeInfo>();

                // Read file once for all types in this file
                string[]? fileLines = null;
                try { fileLines = File.ReadAllLines(file.FilePath); }
                catch { continue; }

                foreach (var type in file.Types)
                {
                    if (type.Kind != CodeTypeKind.Class) continue;
                    if (type.IsAbstract) continue;
                    if (baseTypes.Contains(type.Name)) continue; // Someone inherits from this

                    // Check if already sealed using cached file content
                    if (type.StartLine > 0 && type.StartLine <= fileLines.Length)
                    {
                        var classLine = fileLines[type.StartLine - 1];
                        if (classLine.Contains(" sealed ")) continue; // Already sealed
                    }

                    targets.Add(type);
                    result.TargetsFound++;
                }

                if (targets.Count == 0) continue;

                var diffSb = new StringBuilder();
                foreach (var type in targets)
                {
                    diffSb.AppendLine($"- Line {type.StartLine}: `class {type.Name}` → `sealed class {type.Name}`");
                }

                result.Changes.Add(new IntentFileChange
                {
                    FilePath = file.RelativePath,
                    Description = $"{targets.Count} class(es) can be sealed",
                    Diff = diffSb.ToString()
                });

                // Apply if not preview
                if (!context.PreviewOnly)
                {
                    try
                    {
                        var content = File.ReadAllText(file.FilePath);
                        foreach (var type in targets.OrderByDescending(t => t.StartLine))
                        {
                            // Insert "sealed " before "class "
                            content = Regex.Replace(content,
                                $@"(\b(?:public|internal|private|protected)\s+(?:static\s+)?)(class\s+{Regex.Escape(type.Name)}\b)",
                                "$1sealed $2",
                                RegexOptions.None, TimeSpan.FromSeconds(1));
                        }
                        File.WriteAllText(file.FilePath, content);
                        result.TargetsModified += targets.Count;
                    }
                    catch { }
                }
            }

            result.Summary = context.PreviewOnly
                ? $"Preview: {result.TargetsFound} classes can be sealed (not inherited, not abstract)."
                : $"Sealed {result.TargetsModified} classes across {result.Changes.Count} files.";

            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Custom Intent Rules (loaded from CODEMERGER_RULES.json)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// JSON model for CODEMERGER_RULES.json.
    /// 
    /// Example file:
    /// {
    ///   "customIntents": [
    ///     {
    ///       "name": "enforce_logging",
    ///       "description": "Ensure all catch blocks have a logging call",
    ///       "keywords": ["logging", "catch", "logger"],
    ///       "findPattern": "catch\\s*\\([^)]+\\)\\s*\\{[^}]*\\}",
    ///       "excludePattern": "_logger|Log\\(",
    ///       "fileFilter": ".cs",
    ///       "message": "Catch block missing logging call"
    ///     },
    ///     {
    ///       "name": "ban_console_write",
    ///       "description": "Find and replace Console.Write with Debug.Write",
    ///       "keywords": ["console", "debug"],
    ///       "findPattern": "Console\\.Write(Line)?\\(",
    ///       "replacePattern": "Debug.Write$1(",
    ///       "fileFilter": ".cs",
    ///       "message": "Replace Console.Write with Debug.Write"
    ///     }
    ///   ]
    /// }
    /// </summary>
    public class CustomRulesFile
    {
        public List<CustomRuleDefinition> CustomIntents { get; set; } = new();
    }

    public class CustomRuleDefinition
    {
        /// <summary>Intent name used in apply_pattern calls.</summary>
        public string Name { get; set; } = "";

        /// <summary>Human-readable description.</summary>
        public string Description { get; set; } = "";

        /// <summary>Keywords for fuzzy matching.</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Regex pattern to find matches in source files.</summary>
        public string FindPattern { get; set; } = "";

        /// <summary>Optional: regex replacement pattern. If empty, this is a report-only rule.</summary>
        public string? ReplacePattern { get; set; }

        /// <summary>Optional: if a match also matches this pattern, skip it (used for "find X but not Y").</summary>
        public string? ExcludePattern { get; set; }

        /// <summary>Optional: only scan files whose path contains this string (e.g., ".cs", "Services/").</summary>
        public string? FileFilter { get; set; }

        /// <summary>Message shown for each violation found.</summary>
        public string Message { get; set; } = "Custom rule violation";
    }

    /// <summary>
    /// Intent handler that executes a user-defined custom rule from CODEMERGER_RULES.json.
    /// Rules can be report-only (findPattern only) or transform rules (findPattern + replacePattern).
    /// </summary>
    public class CustomRuleIntentHandler : IIntentHandler
    {
        private readonly CustomRuleDefinition _rule;

        public string Name => _rule.Name;
        public string Description => _rule.Description;
        public string[] Keywords => _rule.Keywords.Count > 0
            ? _rule.Keywords.ToArray()
            : new[] { _rule.Name.Replace("_", " ") };

        public CustomRuleIntentHandler(CustomRuleDefinition rule)
        {
            _rule = rule;
        }

        public IntentResult Execute(IntentContext context)
        {
            var result = new IntentResult();
            var files = IntentRefactoringEngine.FilterByScope(context.WorkspaceAnalysis, context.ScopeFilter);

            // Apply file filter from rule definition
            if (!string.IsNullOrEmpty(_rule.FileFilter))
            {
                files = files.Where(f => f.RelativePath.Contains(_rule.FileFilter, StringComparison.OrdinalIgnoreCase)
                                      || f.Extension.Equals(_rule.FileFilter, StringComparison.OrdinalIgnoreCase)
                                      || f.Extension.Equals("." + _rule.FileFilter.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                             .ToList();
            }

            Regex findRegex;
            Regex? excludeRegex = null;
            bool isTransform = !string.IsNullOrEmpty(_rule.ReplacePattern);

            try
            {
                findRegex = new Regex(_rule.FindPattern, RegexOptions.Multiline, TimeSpan.FromSeconds(5));
                if (!string.IsNullOrEmpty(_rule.ExcludePattern))
                    excludeRegex = new Regex(_rule.ExcludePattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                return new IntentResult
                {
                    IntentName = _rule.Name,
                    Summary = $"Error: Invalid regex in rule '{_rule.Name}': {ex.Message}"
                };
            }

            foreach (var file in files)
            {
                if (result.Changes.Count >= context.MaxTargets) break;

                try
                {
                    var content = File.ReadAllText(file.FilePath);
                    var matches = findRegex.Matches(content);

                    if (matches.Count == 0) continue;

                    var violations = new List<(Match match, int line)>();

                    foreach (Match match in matches)
                    {
                        // Apply exclude filter
                        if (excludeRegex != null && excludeRegex.IsMatch(match.Value))
                            continue;

                        // Calculate line number
                        int lineNum = content.Substring(0, match.Index).Count(c => c == '\n') + 1;
                        violations.Add((match, lineNum));
                        result.TargetsFound++;
                    }

                    if (violations.Count == 0) continue;

                    var diffSb = new StringBuilder();

                    if (isTransform)
                    {
                        // Transform rule: show find → replace diff
                        foreach (var (match, line) in violations)
                        {
                            var replacement = match.Result(_rule.ReplacePattern!);
                            var matchPreview = match.Value.Length > 80
                                ? match.Value.Substring(0, 80) + "..."
                                : match.Value;
                            var replacePreview = replacement.Length > 80
                                ? replacement.Substring(0, 80) + "..."
                                : replacement;
                            diffSb.AppendLine($"- Line {line}: `{matchPreview.Replace("\n", "\\n")}` → `{replacePreview.Replace("\n", "\\n")}`");
                        }

                        // Apply if not preview
                        if (!context.PreviewOnly)
                        {
                            var newContent = findRegex.Replace(content, m =>
                            {
                                if (excludeRegex != null && excludeRegex.IsMatch(m.Value))
                                    return m.Value; // Skip excluded matches
                                return m.Result(_rule.ReplacePattern!);
                            });

                            if (newContent != content)
                            {
                                File.WriteAllText(file.FilePath, newContent);
                                result.TargetsModified += violations.Count;
                            }
                        }
                    }
                    else
                    {
                        // Report-only rule: just list violations
                        foreach (var (match, line) in violations)
                        {
                            var preview = match.Value.Length > 100
                                ? match.Value.Substring(0, 100).Replace("\n", "\\n") + "..."
                                : match.Value.Replace("\n", "\\n");
                            diffSb.AppendLine($"- Line {line}: {_rule.Message} — `{preview}`");
                        }
                    }

                    result.Changes.Add(new IntentFileChange
                    {
                        FilePath = file.RelativePath,
                        Description = $"{violations.Count} {(isTransform ? "replacement(s)" : "violation(s)")} found",
                        Diff = diffSb.ToString()
                    });
                }
                catch { }
            }

            var mode = isTransform ? "transform" : "report";
            result.Summary = context.PreviewOnly
                ? $"Preview ({mode}): {result.TargetsFound} matches for '{_rule.Name}' across {result.Changes.Count} files."
                : isTransform
                    ? $"Applied {result.TargetsModified} replacements for '{_rule.Name}' across {result.Changes.Count} files."
                    : $"Found {result.TargetsFound} violations for '{_rule.Name}' across {result.Changes.Count} files. (Report-only rule — no changes applied.)";

            return result;
        }
    }
}
