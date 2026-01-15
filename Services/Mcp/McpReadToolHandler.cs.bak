using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles read-only MCP tools for querying project information.
    /// </summary>
    public class McpReadToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly ContextAnalyzer _contextAnalyzer;
        private readonly Action<string> _sendActivity;

        public McpReadToolHandler(WorkspaceAnalysis workspaceAnalysis, Action<string> sendActivity)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _contextAnalyzer = new ContextAnalyzer(workspaceAnalysis);
            _sendActivity = sendActivity;
        }

        public string GetWorkspaceOverview()
        {
            _sendActivity("Reading workspace overview");

            var sb = new StringBuilder();
            sb.AppendLine($"# Workspace: {_workspaceAnalysis.WorkspaceName}");
            sb.AppendLine();
            sb.AppendLine($"**Framework:** {_workspaceAnalysis.DetectedFramework}");
            sb.AppendLine($"**Total Files:** {_workspaceAnalysis.TotalFiles}");
            sb.AppendLine($"**Total Tokens:** {_workspaceAnalysis.TotalTokens:N0}");
            sb.AppendLine($"**Total Types:** {_workspaceAnalysis.TypeHierarchy.Count}");
            sb.AppendLine();

            var namespaceGroups = _workspaceAnalysis.AllFiles
                .Where(f => !string.IsNullOrEmpty(f.Namespace))
                .GroupBy(f => f.Namespace)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (namespaceGroups.Count > 0)
            {
                sb.AppendLine("## Namespaces Found");
                foreach (var group in namespaceGroups)
                {
                    sb.AppendLine($"- **{group.Key}** ({group.Count()} files)");
                }
                sb.AppendLine();

                var rootNamespaces = namespaceGroups
                    .Select(g => g.Key.Split('.')[0])
                    .Distinct()
                    .ToList();

                if (rootNamespaces.Count > 1)
                {
                    sb.AppendLine($"⚠️ **Note:** This workspace contains {rootNamespaces.Count} different root namespaces: {string.Join(", ", rootNamespaces)}");
                    sb.AppendLine("*Consider searching by namespace if looking for specific modules.*");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("## File Breakdown");
            var byClassification = _workspaceAnalysis.AllFiles
                .GroupBy(f => f.Classification)
                .OrderByDescending(g => g.Count());

            foreach (var group in byClassification)
            {
                sb.AppendLine($"- {group.Key}: {group.Count()} files");
            }

            sb.AppendLine();
            sb.AppendLine("## Key Entry Points");
            var entryPoints = _workspaceAnalysis.AllFiles
                .Where(f => f.FileName.Contains("Program") || f.FileName.Contains("App.xaml") || f.FileName.Contains("Startup") || f.FileName.EndsWith(".csproj"))
                .Take(10);

            foreach (var file in entryPoints)
            {
                var ns = !string.IsNullOrEmpty(file.Namespace) ? $" [{file.Namespace}]" : "";
                sb.AppendLine($"- {file.RelativePath}{ns}");
            }

            return sb.ToString();
        }

        public string ListFiles(JsonElement arguments)
        {
            _sendActivity("Listing files");

            var files = _workspaceAnalysis.AllFiles.AsEnumerable();

            if (arguments.TryGetProperty("classification", out var classEl))
            {
                var classFilter = classEl.GetString();
                if (Enum.TryParse<FileClassification>(classFilter, true, out var classification))
                {
                    files = files.Where(f => f.Classification == classification);
                }
            }

            if (arguments.TryGetProperty("namespace", out var nsEl))
            {
                var nsFilter = nsEl.GetString()?.ToLowerInvariant() ?? "";
                if (!string.IsNullOrEmpty(nsFilter))
                {
                    files = files.Where(f =>
                        !string.IsNullOrEmpty(f.Namespace) &&
                        f.Namespace.ToLowerInvariant().Contains(nsFilter));
                }
            }

            var limit = 50;
            if (arguments.TryGetProperty("limit", out var limitEl))
            {
                limit = limitEl.GetInt32();
            }

            var fileList = files.ToList();

            var sb = new StringBuilder();
            sb.AppendLine("| File | Namespace | Classification | Tokens |");
            sb.AppendLine("|------|-----------|----------------|--------|");

            foreach (var file in fileList.Take(limit))
            {
                var ns = !string.IsNullOrEmpty(file.Namespace) ? file.Namespace : "-";
                sb.AppendLine($"| {file.RelativePath} | {ns} | {file.Classification} | {file.EstimatedTokens:N0} |");
            }

            var total = fileList.Count;
            if (total > limit)
            {
                sb.AppendLine();
                sb.AppendLine($"*Showing {limit} of {total} files. Use 'classification' or 'namespace' filter or increase 'limit' to see more.*");
            }

            return sb.ToString();
        }

        public string GetFile(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
            {
                return "Error: 'path' parameter is required.";
            }

            var path = pathEl.GetString();
            _sendActivity($"Reading: {path}");

            var file = _workspaceAnalysis.AllFiles.FirstOrDefault(f =>
                f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Equals(path, StringComparison.OrdinalIgnoreCase));

            if (file == null)
            {
                return $"File not found: {path}\n\nAvailable files:\n" +
                       string.Join("\n", _workspaceAnalysis.AllFiles.Take(10).Select(f => $"- {f.RelativePath}"));
            }

            try
            {
                var content = File.ReadAllText(file.FilePath);
                var sb = new StringBuilder();
                sb.AppendLine($"# {file.RelativePath}");
                sb.AppendLine();
                sb.AppendLine($"**Classification:** {file.Classification}");
                sb.AppendLine($"**Tokens:** {file.EstimatedTokens:N0}");
                if (!string.IsNullOrEmpty(file.Namespace))
                    sb.AppendLine($"**Namespace:** {file.Namespace}");
                sb.AppendLine();
                sb.AppendLine($"```{file.Language}");
                sb.AppendLine(content);
                sb.AppendLine("```");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error reading file: {ex.Message}";
            }
        }

        public string SearchCode(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("query", out var queryEl))
            {
                return "Error: 'query' parameter is required.";
            }

            var query = queryEl.GetString()?.ToLowerInvariant() ?? "";
            _sendActivity($"Searching: {query}");

            var searchIn = "all";
            if (arguments.TryGetProperty("searchIn", out var searchInEl))
            {
                searchIn = searchInEl.GetString()?.ToLowerInvariant() ?? "all";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Search Results for: {query}");
            sb.AppendLine();

            if (searchIn == "all" || searchIn == "namespaces")
            {
                var matchingNamespaces = _workspaceAnalysis.AllFiles
                    .Where(f => !string.IsNullOrEmpty(f.Namespace) &&
                               f.Namespace.ToLowerInvariant().Contains(query))
                    .GroupBy(f => f.Namespace)
                    .OrderByDescending(g => g.Count())
                    .Take(10);

                if (matchingNamespaces.Any())
                {
                    sb.AppendLine("## Namespaces");
                    foreach (var group in matchingNamespaces)
                    {
                        sb.AppendLine($"- **{group.Key}** ({group.Count()} files)");
                    }
                    sb.AppendLine();
                }
            }

            if (searchIn == "all" || searchIn == "types")
            {
                var matchingTypes = _workspaceAnalysis.AllFiles
                    .SelectMany(f => f.Types.Select(t => new { File = f, Type = t }))
                    .Where(x => x.Type.Name.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingTypes.Any())
                {
                    sb.AppendLine("## Types");
                    foreach (var match in matchingTypes)
                    {
                        var ns = !string.IsNullOrEmpty(match.File.Namespace) ? $" [{match.File.Namespace}]" : "";
                        sb.AppendLine($"- **{match.Type.Name}** ({match.Type.Kind}) in `{match.File.RelativePath}`{ns}");
                    }
                    sb.AppendLine();
                }
            }

            if (searchIn == "all" || searchIn == "methods")
            {
                var matchingMethods = _workspaceAnalysis.AllFiles
                    .SelectMany(f => f.Types.SelectMany(t => t.Members.Select(m => new { File = f, Type = t, Member = m })))
                    .Where(x => x.Member.Name.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingMethods.Any())
                {
                    sb.AppendLine("## Methods/Members");
                    foreach (var match in matchingMethods)
                    {
                        var sig = !string.IsNullOrEmpty(match.Member.Signature) ? match.Member.Signature : match.Member.Name;
                        var ns = !string.IsNullOrEmpty(match.File.Namespace) ? $" [{match.File.Namespace}]" : "";
                        sb.AppendLine($"- **{match.Type.Name}.{sig}** in `{match.File.RelativePath}`{ns}");
                    }
                    sb.AppendLine();
                }
            }

            if (searchIn == "all" || searchIn == "files")
            {
                var matchingFiles = _workspaceAnalysis.AllFiles
                    .Where(f => f.FileName.ToLowerInvariant().Contains(query) ||
                               f.RelativePath.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingFiles.Any())
                {
                    sb.AppendLine("## Files");
                    foreach (var file in matchingFiles)
                    {
                        var ns = !string.IsNullOrEmpty(file.Namespace) ? $" [{file.Namespace}]" : "";
                        sb.AppendLine($"- `{file.RelativePath}` ({file.Classification}){ns}");
                    }
                }
            }

            if (sb.Length < 50)
            {
                sb.AppendLine("*No results found.*");
            }

            return sb.ToString();
        }

        public string GetType(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";
            _sendActivity($"Getting type: {typeName}");

            var match = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Select(t => new { File = f, Type = t }))
                .FirstOrDefault(x => x.Type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                return $"Type not found: {typeName}\n\nAvailable types:\n" +
                       string.Join("\n", _workspaceAnalysis.TypeHierarchy.Keys.Take(20).Select(t => $"- {t}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# {match.Type.Name}");
            sb.AppendLine();
            sb.AppendLine($"**Kind:** {match.Type.Kind}");
            sb.AppendLine($"**File:** {match.File.RelativePath}");
            if (!string.IsNullOrEmpty(match.File.Namespace))
                sb.AppendLine($"**Namespace:** {match.File.Namespace}");

            if (!string.IsNullOrEmpty(match.Type.BaseType))
                sb.AppendLine($"**Base Type:** {match.Type.BaseType}");

            if (match.Type.Interfaces.Count > 0)
                sb.AppendLine($"**Interfaces:** {string.Join(", ", match.Type.Interfaces)}");

            sb.AppendLine();
            sb.AppendLine("## Members");

            var membersByKind = match.Type.Members.GroupBy(m => m.Kind);
            foreach (var group in membersByKind)
            {
                sb.AppendLine($"### {group.Key}s");
                foreach (var member in group)
                {
                    var sig = !string.IsNullOrEmpty(member.Signature) ? member.Signature : member.Name;
                    var ret = !string.IsNullOrEmpty(member.ReturnType) ? $" : {member.ReturnType}" : "";
                    sb.AppendLine($"- {member.AccessModifier} {sig}{ret}");
                }
            }

            return sb.ToString();
        }

        public string GetDependencies(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";
            _sendActivity($"Getting dependencies: {typeName}");

            var sb = new StringBuilder();
            sb.AppendLine($"# Dependencies for: {typeName}");
            sb.AppendLine();

            if (_workspaceAnalysis.DependencyMap.TryGetValue(typeName, out var deps) && deps.Count > 0)
            {
                sb.AppendLine("## Uses (depends on)");
                foreach (var dep in deps)
                {
                    sb.AppendLine($"- {dep}");
                }
                sb.AppendLine();
            }

            var reverseDeps = _workspaceAnalysis.DependencyMap
                .Where(kvp => kvp.Value.Contains(typeName))
                .Select(kvp => kvp.Key)
                .ToList();

            if (reverseDeps.Count > 0)
            {
                sb.AppendLine("## Used by (dependents)");
                foreach (var dep in reverseDeps)
                {
                    sb.AppendLine($"- {dep}");
                }
            }

            if (sb.Length < 50)
            {
                sb.AppendLine("*No dependencies found.*");
            }

            return sb.ToString();
        }

        public string GetTypeHierarchy()
        {
            _sendActivity("Getting type hierarchy");

            var sb = new StringBuilder();
            sb.AppendLine("# Type Hierarchy");
            sb.AppendLine();

            foreach (var kvp in _workspaceAnalysis.TypeHierarchy.OrderBy(k => k.Key))
            {
                var inheritance = kvp.Value.Count > 0 ? $" : {string.Join(", ", kvp.Value)}" : "";
                sb.AppendLine($"- **{kvp.Key}**{inheritance}");
            }

            return sb.ToString();
        }

        public string Grep(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("pattern", out var patternEl))
            {
                return "Error: 'pattern' parameter is required.";
            }

            var pattern = patternEl.GetString() ?? "";
            _sendActivity($"Grep: {pattern}");

            var isRegex = true;
            if (arguments.TryGetProperty("isRegex", out var isRegexEl))
                isRegex = isRegexEl.GetBoolean();

            var caseSensitive = false;
            if (arguments.TryGetProperty("caseSensitive", out var caseEl))
                caseSensitive = caseEl.GetBoolean();

            var contextLines = 2;
            if (arguments.TryGetProperty("contextLines", out var ctxEl))
                contextLines = ctxEl.GetInt32();

            var maxResults = 50;
            if (arguments.TryGetProperty("maxResults", out var maxEl))
                maxResults = maxEl.GetInt32();

            var result = _contextAnalyzer.SearchContent(pattern, isRegex, caseSensitive, contextLines, maxResults);
            return result.ToMarkdown();
        }

        public string GetContext(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("task", out var taskEl))
            {
                return "Error: 'task' parameter is required.";
            }

            var task = taskEl.GetString() ?? "";
            _sendActivity($"Getting context for: {task}");

            var maxFiles = 10;
            if (arguments.TryGetProperty("maxFiles", out var maxFilesEl))
                maxFiles = maxFilesEl.GetInt32();

            var maxTokens = 50000;
            if (arguments.TryGetProperty("maxTokens", out var maxTokensEl))
                maxTokens = maxTokensEl.GetInt32();

            var result = _contextAnalyzer.GetContextForTask(task, maxFiles, maxTokens);
            return result.ToMarkdown();
        }
    }
}
