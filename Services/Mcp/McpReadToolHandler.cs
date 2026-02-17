using System;
using System.Collections.Generic;
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
        private readonly FilePathResolver _pathResolver;
        private readonly List<string> _inputDirectories;
        private readonly Action<string> _sendActivity;

        public McpReadToolHandler(WorkspaceAnalysis workspaceAnalysis, List<CallSite> callSites, List<string> inputDirectories, Action<string> sendActivity)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _contextAnalyzer = new ContextAnalyzer(workspaceAnalysis, callSites);
            _pathResolver = new FilePathResolver(workspaceAnalysis, inputDirectories);
            _inputDirectories = inputDirectories;
            _sendActivity = sendActivity;
        }

        /// <summary>
        /// Returns true if any files have SourceWorkspace set (merged mode).
        /// </summary>
        private bool IsMergedMode => _workspaceAnalysis.AllFiles.Any(f => !string.IsNullOrEmpty(f.SourceWorkspace));

        /// <summary>
        /// Formats the workspace prefix for display, e.g. "[SmartMoney] "
        /// Returns empty string if no source workspace.
        /// </summary>
        private static string FormatWorkspacePrefix(FileAnalysis file)
        {
            return string.IsNullOrEmpty(file.SourceWorkspace) ? "" : $"[{file.SourceWorkspace}] ";
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

            // Tool selection guide (survives context condensation)
            sb.AppendLine();
            sb.AppendLine("## Tool Guide");
            sb.AppendLine("- **Renaming** → `rename_symbol` (not str_replace). Call `get_dependencies` first, preview before applying.");
            sb.AppendLine("- **Moving files** → `move_file` (updates usings). Preview first.");
            sb.AppendLine("- **Adding params** → `add_parameter` (updates call sites). Preview first.");
            sb.AppendLine("- **Editing** → `str_replace` (one edit at a time, verify with `build` between edits).");
            sb.AppendLine("- **New files** → `write_file`. For >600 lines: skeleton first, fill via str_replace.");
            sb.AppendLine("- **Searching** → `search_code` for symbols, `grep` for text/comments, `find_references` for semantic usages.");
            sb.AppendLine("- **Task context** → `get_context` with natural language description.");
            sb.AppendLine("- **WORKFLOW**: Present roadmap → pause between steps for user OK (unless told otherwise) → final Review & Cleanup step.");
            sb.AppendLine();
            sb.AppendLine("## Search Decision Tree");
            sb.AppendLine("- Need to find a C# type or method? → `search_code` (NEVER grep)");
            sb.AppendLine("- Need to see who uses a symbol? → `find_references` (NEVER grep)");
            sb.AppendLine("- Need to see who calls a method? → `get_callers` (NEVER grep)");
            sb.AppendLine("- Need to see what a method calls? → `get_callees`");
            sb.AppendLine("- Need to understand a class API? → `get_type`");
            sb.AppendLine("- Need to read one method? → `get_method_body` (not get_file)");
            sb.AppendLine("- Need XAML bindings, string literals, comments? → `grep` (ONLY valid use)");
            sb.AppendLine("- Need to understand XAML layout/structure? → `get_xaml_tree` (not grep or get_lines)");
            sb.AppendLine("- Need to explore for a task? → `get_context` with natural language");
            sb.AppendLine("- find_references results seem incomplete? → follow up with `grep` as safety net");
            sb.AppendLine();
            sb.AppendLine("## Mandatory Session Workflow");
            sb.AppendLine("1. `get_project_overview` → orientation");
            sb.AppendLine("2. `notes get` → read architecture context (skip rediscovery)");
            sb.AppendLine("3. `get_context` → task-specific file discovery");
            sb.AppendLine("4. `get_type` / `get_method_body` → drill into specifics");
            sb.AppendLine("5. Make changes with `str_replace` → `build` → verify");
            sb.AppendLine();

            // Buildable Projects section — show .sln and .csproj files available for `build`
            var buildTargets = DiscoverBuildTargets();
            if (buildTargets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Build Targets");
                sb.AppendLine("Use `build path=\"Filename.sln\"` or `build path=\"Project.csproj\"` to target a specific project.");
                sb.AppendLine();
                foreach (var target in buildTargets)
                {
                    sb.AppendLine($"- `{Path.GetFileName(target)}` — {target}");
                }
            }

            // Project References section
            if (_workspaceAnalysis.ProjectReferences.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Project References");

                // Group by resolved path to show unique external projects
                var groupedRefs = _workspaceAnalysis.ProjectReferences
                    .GroupBy(r => r.ResolvedPath)
                    .OrderBy(g => g.First().Name);

                foreach (var group in groupedRefs)
                {
                    var first = group.First();
                    var referencedBy = string.Join(", ", group.Select(r => r.SourceProject).Distinct());
                    var isExternal = !_workspaceAnalysis.AllFiles.Any(f => 
                        f.FilePath.Equals(first.ResolvedPath, StringComparison.OrdinalIgnoreCase));
                    
                    var location = isExternal ? "external" : "in workspace";
                    sb.AppendLine($"- **{first.Name}** ({location})");
                    sb.AppendLine($"  - Path: `{first.ResolvedPath}`");
                    sb.AppendLine($"  - Referenced by: {referencedBy}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Discovers all .sln and .csproj files in and around the workspace directories.
        /// </summary>
        private List<string> DiscoverBuildTargets()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly))
                        results.Add(f);
                    foreach (var f in Directory.GetFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly))
                        results.Add(f);
                    foreach (var f in Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories))
                        results.Add(f);
                }
                catch { }

                var parent = Directory.GetParent(dir)?.FullName;
                if (parent != null && checkedParents.Add(parent) && Directory.Exists(parent))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(parent, "*.sln", SearchOption.TopDirectoryOnly))
                            results.Add(f);
                        foreach (var f in Directory.GetFiles(parent, "*.slnx", SearchOption.TopDirectoryOnly))
                            results.Add(f);
                    }
                    catch { }
                }
            }

            return results.OrderBy(f => Path.GetExtension(f)).ThenBy(f => f).ToList();
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

            // Check if we have multiple root directories
            var distinctRoots = _workspaceAnalysis.AllFiles
                .Select(f => f.RootDirectory)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct()
                .ToList();
            var hasMultipleRoots = distinctRoots.Count > 1;

            // Check if we're in merged mode
            var isMerged = IsMergedMode;

            var sb = new StringBuilder();

            if (isMerged)
            {
                // Merged mode: show workspace column
                sb.AppendLine("| Workspace | File | Namespace | Classification | Tokens |");
                sb.AppendLine("|-----------|------|-----------|----------------|--------|");

                foreach (var file in fileList.Take(limit))
                {
                    var ns = !string.IsNullOrEmpty(file.Namespace) ? file.Namespace : "-";
                    var ws = !string.IsNullOrEmpty(file.SourceWorkspace) ? file.SourceWorkspace : "-";
                    sb.AppendLine($"| {ws} | {file.RelativePath} | {ns} | {file.Classification} | {file.EstimatedTokens:N0} |");
                }
            }
            else if (hasMultipleRoots)
            {
                sb.AppendLine("| File | Root | Namespace | Classification | Tokens |");
                sb.AppendLine("|------|------|-----------|----------------|--------|");

                foreach (var file in fileList.Take(limit))
                {
                    var ns = !string.IsNullOrEmpty(file.Namespace) ? file.Namespace : "-";
                    var rootName = !string.IsNullOrEmpty(file.RootDirectory) ? Path.GetFileName(file.RootDirectory.TrimEnd('\\', '/')) : "-";
                    sb.AppendLine($"| {file.RelativePath} | {rootName} | {ns} | {file.Classification} | {file.EstimatedTokens:N0} |");
                }
            }
            else
            {
                sb.AppendLine("| File | Namespace | Classification | Tokens |");
                sb.AppendLine("|------|-----------|----------------|--------|");

                foreach (var file in fileList.Take(limit))
                {
                    var ns = !string.IsNullOrEmpty(file.Namespace) ? file.Namespace : "-";
                    sb.AppendLine($"| {file.RelativePath} | {ns} | {file.Classification} | {file.EstimatedTokens:N0} |");
                }
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

            var path = pathEl.GetString() ?? "";
            _sendActivity($"Reading: {path}");

            var (file, error) = _pathResolver.FindFile(path);
            if (file == null)
            {
                return error!;
            }

            try
            {
                var content = File.ReadAllText(file.FilePath);
                var sb = new StringBuilder();
                sb.AppendLine($"# {file.RelativePath}");
                sb.AppendLine();
                sb.AppendLine($"**Classification:** {file.Classification}");
                sb.AppendLine($"**Tokens:** {file.EstimatedTokens:N0}");
                if (!string.IsNullOrEmpty(file.SourceWorkspace))
                    sb.AppendLine($"**Source Workspace:** {file.SourceWorkspace}");
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
                        var ws = FormatWorkspacePrefix(match.File);
                        var ns = !string.IsNullOrEmpty(match.File.Namespace) ? $" [{match.File.Namespace}]" : "";
                        sb.AppendLine($"- {ws}**{match.Type.Name}** ({match.Type.Kind}) in `{match.File.RelativePath}`{ns}");
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
                        var ws = FormatWorkspacePrefix(match.File);
                        var sig = !string.IsNullOrEmpty(match.Member.Signature) ? match.Member.Signature : match.Member.Name;
                        var ns = !string.IsNullOrEmpty(match.File.Namespace) ? $" [{match.File.Namespace}]" : "";
                        sb.AppendLine($"- {ws}**{match.Type.Name}.{sig}** in `{match.File.RelativePath}`{ns}");
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
                        var ws = FormatWorkspacePrefix(file);
                        var ns = !string.IsNullOrEmpty(file.Namespace) ? $" [{file.Namespace}]" : "";
                        sb.AppendLine($"- {ws}`{file.RelativePath}` ({file.Classification}){ns}");
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
            if (!string.IsNullOrEmpty(match.File.SourceWorkspace))
                sb.AppendLine($"**Source Workspace:** {match.File.SourceWorkspace}");
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

                // Detect overloads: names that appear more than once
                var nameCounts = group.GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

                foreach (var member in group)
                {
                    var sig = !string.IsNullOrEmpty(member.Signature) ? member.Signature : member.Name;
                    var ret = !string.IsNullOrEmpty(member.ReturnType) ? $" : {member.ReturnType}" : "";
                    var overload = nameCounts.Contains(member.Name) ? "  [overload]" : "";
                    sb.AppendLine($"- {member.AccessModifier} {sig}{ret}{overload}");
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

            var summaryOnly = false;
            if (arguments.TryGetProperty("summaryOnly", out var summaryEl))
                summaryOnly = summaryEl.GetBoolean();

            var result = _contextAnalyzer.SearchContent(pattern, isRegex, caseSensitive, contextLines, maxResults, summaryOnly);
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

        public string GetLines(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("startLine", out var startEl))
                return "Error: 'startLine' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var startLine = startEl.GetInt32();

            var endLine = startLine + 20; // Default to 20 lines
            if (arguments.TryGetProperty("endLine", out var endEl))
                endLine = endEl.GetInt32();

            _sendActivity($"GetLines: {path} [{startLine}-{endLine}]");

            var (file, error) = _pathResolver.FindFile(path);
            if (file == null)
            {
                return $"Error: {error}";
            }

            try
            {
                var lines = File.ReadAllLines(file.FilePath);
                var sb = new StringBuilder();

                sb.AppendLine($"# Raw Lines: {file.RelativePath}");
                sb.AppendLine();
                sb.AppendLine($"**Lines {startLine}-{Math.Min(endLine, lines.Length)} of {lines.Length}**");
                sb.AppendLine();
                sb.AppendLine("**Whitespace legend:** `→` = tab, `·` = space");
                sb.AppendLine();
                sb.AppendLine("```");

                // Adjust for 1-based line numbers
                var start = Math.Max(1, startLine) - 1;
                var end = Math.Min(endLine, lines.Length);

                for (int i = start; i < end; i++)
                {
                    var line = lines[i];
                    var visualized = VisualizeAllWhitespace(line);
                    sb.AppendLine($"{i + 1,4}: {visualized}");
                }

                sb.AppendLine("```");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error reading file: {ex.Message}";
            }
        }

        public string GetXamlTree(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            var path = pathEl.GetString() ?? "";
            _sendActivity($"XAML tree: {path}");

            var (file, error) = _pathResolver.FindFile(path);
            if (file == null)
                return $"Error: {error}";

            if (!file.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                return $"Error: '{file.RelativePath}' is not a XAML file.";

            var result = XamlAnalyzer.Analyze(file.FilePath, file.RootDirectory);
            return result.ToMarkdown();
        }

        /// <summary>
        /// Makes ALL whitespace visible: tabs as → and spaces as · (middle dot).
        /// Used for precise whitespace inspection.
        /// </summary>
        private static string VisualizeAllWhitespace(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            var result = new StringBuilder();
            foreach (var c in line)
            {
                if (c == '\t')
                    result.Append('→');
                else if (c == ' ')
                    result.Append('·');
                else if (c == '\r')
                    continue; // Skip carriage returns
                else
                    result.Append(c);
            }
            return result.ToString();
        }
    }
}
