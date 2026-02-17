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
        private readonly Func<DateTime> _getLastEditTimestamp;

        public McpSemanticToolHandler(WorkspaceAnalysis workspaceAnalysis, List<CallSite> callSites, Action<string> sendActivity, Func<DateTime> getLastEditTimestamp)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _semanticAnalyzer = new SemanticAnalyzer(workspaceAnalysis, callSites);
            _sendActivity = sendActivity;
            _getLastEditTimestamp = getLastEditTimestamp;
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
            return AppendStalenessWarning(result.ToMarkdown());
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

            return AppendStalenessWarning(sb.ToString());
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

            return AppendStalenessWarning(sb.ToString());
        }

        public string FindImplementations(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("typeName", out var typeEl))
                return "Error: 'typeName' parameter is required.";

            var typeName = typeEl.GetString() ?? "";
            _sendActivity($"Finding implementations of: {typeName}");

            bool includeAbstract = false;
            if (arguments.TryGetProperty("includeAbstract", out var abstractEl))
                includeAbstract = abstractEl.GetBoolean();

            var result = _semanticAnalyzer.FindImplementations(typeName, includeAbstract: includeAbstract);
            return result.ToMarkdown();
        }

        public string GetMethodBody(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("methodName", out var methodEl))
                return "Error: 'methodName' parameter is required.";

            var methodName = methodEl.GetString() ?? "";
            _sendActivity($"Getting method body: {methodName}");

            string? typeName = null;
            if (arguments.TryGetProperty("typeName", out var typeEl))
                typeName = typeEl.GetString();

            bool includeDoc = false;
            if (arguments.TryGetProperty("includeDoc", out var docEl))
                includeDoc = docEl.GetBoolean();

            var result = _semanticAnalyzer.GetMethodBody(methodName, typeName, includeDoc);
            return result.ToMarkdown();
        }

        public string GetDiagnostics(JsonElement arguments)
        {
            string? specificPath = null;
            if (arguments.TryGetProperty("path", out var pathEl))
                specificPath = pathEl.GetString();

            _sendActivity($"Checking syntax{(specificPath != null ? $": {specificPath}" : "")}");

            var sb = new StringBuilder();
            sb.AppendLine("# Syntax Check");
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

                sb.AppendLine($"**Files checked:** {csFiles.Count}");
                sb.AppendLine();

                // Syntax-only parse — fast and reliable
                var parseErrors = new List<string>();

                foreach (var file in csFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file.FilePath);
                        var tree = CSharpSyntaxTree.ParseText(content, path: file.RelativePath);

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

                if (parseErrors.Any())
                {
                    sb.AppendLine("## ❌ Syntax Errors Found");
                    sb.AppendLine();
                    foreach (var err in parseErrors.Take(30))
                        sb.AppendLine(err);
                    if (parseErrors.Count > 30)
                        sb.AppendLine($"- ... and {parseErrors.Count - 30} more");

                    // Return syntax-only — caller (McpServer) will NOT proceed to build
                    return sb.ToString();
                }

                sb.AppendLine("✅ **Syntax OK** — no parse errors found.");
                sb.AppendLine();

                // Signal to caller that syntax passed — build should follow
                return "SYNTAX_OK\n" + sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error running diagnostics: {ex.Message}\n\n💡 **Tip:** Use `build` (without quickCheck) for full compilation.";
            }
            finally
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            }
        }

        /// <summary>
        /// Appends a staleness warning if any .cs files were indexed before the last edit.
        /// </summary>
        private string AppendStalenessWarning(string markdown)
        {
            var lastEdit = _getLastEditTimestamp();
            var staleCount = _workspaceAnalysis.AllFiles
                .Count(f => f.Extension == ".cs" && f.LastIndexedUtc < lastEdit.AddSeconds(-5));

            if (staleCount > 0)
            {
                markdown += "\n\n⚠️ **Note:** Some files may have stale index data (" +
                            staleCount + " files indexed before the last edit). " +
                            "Use `codemerger_refresh` if results seem incomplete.";
            }

            return markdown;
        }
    }
}
