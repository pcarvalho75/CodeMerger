using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles semantic analysis MCP tools (find references, call graph, diagnostics).
    /// </summary>
    public class McpSemanticToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly SemanticAnalyzer _semanticAnalyzer;
        private readonly Action<string> _sendActivity;

        public McpSemanticToolHandler(WorkspaceAnalysis workspaceAnalysis, List<CallSite> callSites, Action<string> sendActivity)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _semanticAnalyzer = new SemanticAnalyzer(workspaceAnalysis, callSites);
            _sendActivity = sendActivity;
        }

        public string FindReferences(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("symbolName", out var symbolEl))
            {
                return "Error: 'symbolName' parameter is required.";
            }

            var symbolName = symbolEl.GetString() ?? "";
            _sendActivity($"Finding references: {symbolName}");

            string? symbolKind = null;
            if (arguments.TryGetProperty("symbolKind", out var kindEl))
                symbolKind = kindEl.GetString();

            var result = _semanticAnalyzer.FindUsages(symbolName, symbolKind);
            return result.ToMarkdown();
        }

        public string GetCallers(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("methodName", out var methodEl))
            {
                return "Error: 'methodName' parameter is required.";
            }

            var methodName = methodEl.GetString() ?? "";
            _sendActivity($"Finding callers of: {methodName}");

            string typeName = "";
            if (arguments.TryGetProperty("typeName", out var typeEl))
                typeName = typeEl.GetString() ?? "";

            var result = _semanticAnalyzer.GetCallGraph(typeName, methodName, 2);

            var sb = new StringBuilder();
            sb.AppendLine($"# Callers of `{(string.IsNullOrEmpty(typeName) ? "" : typeName + ".")}{methodName}`");
            sb.AppendLine();

            if (result.Callers.Any())
            {
                sb.AppendLine($"**Found:** {result.Callers.Count} callers");
                sb.AppendLine();
                sb.AppendLine("| Caller | File | Line |");
                sb.AppendLine("|--------|------|------|");

                foreach (var caller in result.Callers)
                {
                    sb.AppendLine($"| `{caller.TypeName}.{caller.MethodName}` | `{caller.FilePath}` | {caller.Line} |");
                }

                // Show upstream callers if available
                var upstreamCallers = result.Callers.Where(c => c.UpstreamCallers?.Any() == true).ToList();
                if (upstreamCallers.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("## Upstream Call Chain");
                    foreach (var caller in upstreamCallers)
                    {
                        sb.AppendLine($"- `{caller.TypeName}.{caller.MethodName}` is called by:");
                        foreach (var upstream in caller.UpstreamCallers!)
                        {
                            sb.AppendLine($"  - `{upstream}`");
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine("*No callers found. This may be an entry point or unused method.*");
            }

            return sb.ToString();
        }

        public string GetCallees(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("methodName", out var methodEl))
            {
                return "Error: 'methodName' parameter is required.";
            }

            var methodName = methodEl.GetString() ?? "";
            _sendActivity($"Finding calls from: {methodName}");

            string typeName = "";
            if (arguments.TryGetProperty("typeName", out var typeEl))
                typeName = typeEl.GetString() ?? "";

            var result = _semanticAnalyzer.GetCallGraph(typeName, methodName, 2);

            var sb = new StringBuilder();
            sb.AppendLine($"# Methods called by `{(string.IsNullOrEmpty(typeName) ? "" : typeName + ".")}{methodName}`");
            sb.AppendLine();

            if (result.Callees.Any())
            {
                sb.AppendLine($"**Found:** {result.Callees.Count} calls");
                sb.AppendLine();
                sb.AppendLine("| Called Method | File | Line |");
                sb.AppendLine("|---------------|------|------|");

                foreach (var callee in result.Callees)
                {
                    sb.AppendLine($"| `{callee.TypeName}.{callee.MethodName}` | `{callee.FilePath}` | {callee.Line} |");
                }

                // Show downstream calls if available
                var downstreamCallees = result.Callees.Where(c => c.DownstreamCallees?.Any() == true).ToList();
                if (downstreamCallees.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("## Downstream Call Chain");
                    foreach (var callee in downstreamCallees)
                    {
                        sb.AppendLine($"- `{callee.TypeName}.{callee.MethodName}` calls:");
                        foreach (var downstream in callee.DownstreamCallees!)
                        {
                            sb.AppendLine($"  - `{downstream}`");
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine("*No outgoing calls found.*");
            }

            return sb.ToString();
        }

        public string GetDiagnostics(JsonElement arguments)
        {
            string? specificPath = null;
            if (arguments.TryGetProperty("path", out var pathEl))
                specificPath = pathEl.GetString();

            bool errorsOnly = false;
            if (arguments.TryGetProperty("errorsOnly", out var errorsEl))
                errorsOnly = errorsEl.GetBoolean();

            _sendActivity($"Getting diagnostics{(specificPath != null ? $": {specificPath}" : "")}");

            var sb = new StringBuilder();
            sb.AppendLine("# Compilation Diagnostics");
            sb.AppendLine();

            try
            {
                // Get C# files to analyze
                var csFiles = _workspaceAnalysis.AllFiles
                    .Where(f => f.Extension == ".cs")
                    .ToList();

                if (specificPath != null)
                {
                    csFiles = csFiles.Where(f =>
                        f.RelativePath.Equals(specificPath, StringComparison.OrdinalIgnoreCase) ||
                        f.FileName.Equals(specificPath, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (csFiles.Count == 0)
                        return $"Error: File not found: {specificPath}";
                }

                // MEMORY/TIME OPTIMIZATION: Strict file limit
                const int MaxFilesForDiagnostics = 15;
                bool truncated = csFiles.Count > MaxFilesForDiagnostics;
                if (truncated)
                {
                    sb.AppendLine($"⚠️ **Limited analysis:** Checking {MaxFilesForDiagnostics} of {csFiles.Count} files.");
                    sb.AppendLine($"💡 **Tip:** Use `codemerger_build` for full compilation with all references.");
                    sb.AppendLine();
                    csFiles = csFiles.Take(MaxFilesForDiagnostics).ToList();
                }

                // Parse files into syntax trees (syntax-only, fast)
                var syntaxTrees = new List<SyntaxTree>();
                var parseErrors = new List<string>();

                foreach (var file in csFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file.FilePath);
                        var tree = CSharpSyntaxTree.ParseText(content, path: file.RelativePath);
                        syntaxTrees.Add(tree);

                        // Check for syntax errors immediately (fast)
                        var syntaxDiags = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                        foreach (var diag in syntaxDiags.Take(5))
                        {
                            var line = diag.Location.GetLineSpan().StartLinePosition.Line + 1;
                            parseErrors.Add($"- ❌ `{file.RelativePath}` **Line {line}:** {diag.GetMessage()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        parseErrors.Add($"- ❌ `{file.RelativePath}`: {ex.Message}");
                    }
                }

                // Report syntax errors first (these are reliable)
                if (parseErrors.Any())
                {
                    sb.AppendLine("## Syntax Errors (reliable)");
                    sb.AppendLine();
                    foreach (var err in parseErrors.Take(20))
                        sb.AppendLine(err);
                    if (parseErrors.Count > 20)
                        sb.AppendLine($"- ... and {parseErrors.Count - 20} more");
                    sb.AppendLine();
                }

                // Skip semantic analysis if we already have syntax errors
                if (parseErrors.Any())
                {
                    sb.AppendLine("---");
                    sb.AppendLine("*Skipping semantic analysis due to syntax errors. Fix syntax first.*");
                    return sb.ToString();
                }

                // Semantic analysis with timeout protection
                sb.AppendLine("## Semantic Analysis");
                sb.AppendLine();

                var references = new List<MetadataReference>
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                };

                var runtimePath = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
                if (File.Exists(runtimePath))
                    references.Add(MetadataReference.CreateFromFile(runtimePath));

                var compilation = CSharpCompilation.Create(
                    "DiagnosticsCheck",
                    syntaxTrees,
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                // Get diagnostics with a reasonable limit
                var diagnostics = compilation.GetDiagnostics()
                    .Where(d => d.Location.IsInSource)
                    .Where(d => !errorsOnly || d.Severity == DiagnosticSeverity.Error)
                    // Filter out common false positives from missing references
                    .Where(d => d.Id != "CS0246" && d.Id != "CS0234" && d.Id != "CS0012")
                    .OrderByDescending(d => d.Severity)
                    .Take(30) // Hard limit on results
                    .ToList();

                if (diagnostics.Count == 0)
                {
                    sb.AppendLine("✓ No issues found in analyzed files!");
                }
                else
                {
                    sb.AppendLine($"**Issues found:** {diagnostics.Count}");
                    sb.AppendLine();

                    foreach (var diag in diagnostics)
                    {
                        var lineSpan = diag.Location.GetLineSpan();
                        var line = lineSpan.StartLinePosition.Line + 1;
                        var file = Path.GetFileName(diag.Location.SourceTree?.FilePath ?? "?");
                        var severity = diag.Severity == DiagnosticSeverity.Error ? "❌" : "⚠️";
                        sb.AppendLine($"- {severity} `{file}` **Line {line}:** [{diag.Id}] {diag.GetMessage()}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("*⚠️ This uses basic .NET references only. For accurate results with NuGet/WPF/etc, use `codemerger_build`.*");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error running diagnostics: {ex.Message}\n\n💡 **Tip:** Use `codemerger_build` for reliable compilation.";
            }
            finally
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: false);
            }
        }
    }
}
