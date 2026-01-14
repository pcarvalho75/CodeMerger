using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles semantic analysis MCP tools (find references, call graph).
    /// </summary>
    public class McpSemanticToolHandler
    {
        private readonly SemanticAnalyzer _semanticAnalyzer;
        private readonly Action<string> _sendActivity;

        public McpSemanticToolHandler(WorkspaceAnalysis workspaceAnalysis, List<CallSite> callSites, Action<string> sendActivity)
        {
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
    }
}
