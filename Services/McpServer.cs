using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    public class McpServer
    {
        private readonly CodeAnalyzer _codeAnalyzer;
        private readonly IndexGenerator _indexGenerator;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _serverTask;
        private ProjectAnalysis? _projectAnalysis;
        
        // Services
        private ContextAnalyzer? _contextAnalyzer;
        private SemanticAnalyzer? _semanticAnalyzer;
        private RefactoringService? _refactoringService;
        
        private List<string> _inputDirectories = new();
        private string _projectName = string.Empty;

        public bool IsRunning => _serverTask != null && !_serverTask.IsCompleted;
        public event Action<string>? OnLog;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public McpServer()
        {
            _codeAnalyzer = new CodeAnalyzer();
            _indexGenerator = new IndexGenerator();
        }

        public void IndexProject(string projectName, List<string> inputDirectories, List<string> files)
        {
            _projectName = projectName;
            _inputDirectories = inputDirectories;

            // Clear previous call sites
            _codeAnalyzer.CallSites.Clear();

            var fileAnalyses = new List<FileAnalysis>();
            foreach (var file in files)
            {
                var baseDir = inputDirectories.FirstOrDefault(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
                if (baseDir == null) continue;

                var analysis = _codeAnalyzer.AnalyzeFile(file, baseDir);
                fileAnalyses.Add(analysis);
            }

            var chunkManager = new ChunkManager(150000);
            var chunks = chunkManager.CreateChunks(fileAnalyses);
            _projectAnalysis = _indexGenerator.BuildProjectAnalysis(projectName, fileAnalyses, chunks);
            
            // Initialize all services
            _contextAnalyzer = new ContextAnalyzer(_projectAnalysis);
            _semanticAnalyzer = new SemanticAnalyzer(_projectAnalysis, _codeAnalyzer.CallSites);
            _refactoringService = new RefactoringService(_projectAnalysis, inputDirectories);

            Log($"Indexed {fileAnalyses.Count} files, {_projectAnalysis.TypeHierarchy.Count} types, {_codeAnalyzer.CallSites.Count} call sites");
        }

        public async Task StartAsync(Stream inputStream, Stream outputStream)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            Log("MCP Server starting...");

            _serverTask = Task.Run(async () =>
            {
                using var reader = new StreamReader(inputStream, Encoding.UTF8);
                using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;

                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var response = ProcessMessage(line);
                        if (response != null)
                        {
                            await writer.WriteLineAsync(response);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error: {ex.Message}");
                    }
                }

                Log("MCP Server stopped.");
            }, token);

            await Task.CompletedTask;
        }

        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
            Log("MCP Server stopping...");
        }

        private string? ProcessMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                var method = root.GetProperty("method").GetString();
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;

                Log($"Received: {method}");

                return method switch
                {
                    "initialize" => HandleInitialize(id),
                    "tools/list" => HandleListTools(id),
                    "tools/call" => HandleToolCall(id, root),
                    "notifications/initialized" => null,
                    _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
                };
            }
            catch (Exception ex)
            {
                Log($"Parse error: {ex.Message}");
                return CreateErrorResponse(0, -32700, "Parse error");
            }
        }

        private string HandleInitialize(int id)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        tools = new { }
                    },
                    serverInfo = new
                    {
                        name = "codemerger-mcp",
                        version = "3.0.0"
                    }
                }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private string HandleListTools(int id)
        {
            var tools = new List<object>
            {
                // ===== BASIC TOOLS =====
                MakeTool("codemerger_get_project_overview",
                    "Get high-level project information including framework, structure, total files, and entry points",
                    new Dictionary<string, object>()),

                MakeTool("codemerger_list_files",
                    "List all files in the project with their classifications (View, Model, Service, etc.) and estimated tokens",
                    new Dictionary<string, object>
                    {
                        { "classification", StringParam("Filter by classification: View, Model, Service, Controller, Test, Config, Unknown") },
                        { "limit", IntParam("Maximum files to return (default 50)") }
                    }),

                MakeTool("codemerger_get_file",
                    "Get the full content of a specific file by its relative path",
                    new Dictionary<string, object>
                    {
                        { "path", StringParam("Relative path to the file") }
                    },
                    new[] { "path" }),

                MakeTool("codemerger_search_code",
                    "Search for types, methods, or keywords in the codebase",
                    new Dictionary<string, object>
                    {
                        { "query", StringParam("Search query (type name, method name, or keyword)") },
                        { "searchIn", StringParam("Where to search: types, methods, files, all (default: all)") }
                    },
                    new[] { "query" }),

                MakeTool("codemerger_get_type",
                    "Get detailed information about a specific type including its members, base types, and interfaces",
                    new Dictionary<string, object>
                    {
                        { "typeName", StringParam("Name of the type") }
                    },
                    new[] { "typeName" }),

                MakeTool("codemerger_get_dependencies",
                    "Get dependencies of a type (what it uses) and reverse dependencies (what uses it)",
                    new Dictionary<string, object>
                    {
                        { "typeName", StringParam("Name of the type") }
                    },
                    new[] { "typeName" }),

                MakeTool("codemerger_get_type_hierarchy",
                    "Get the inheritance hierarchy for all types in the project",
                    new Dictionary<string, object>()),

                // ===== SMART CONTEXT TOOLS =====
                MakeTool("codemerger_search_content",
                    "Search inside file contents using text or regex patterns. Returns matching lines with context. Great for finding usages, patterns, TODOs, or any text across the codebase.",
                    new Dictionary<string, object>
                    {
                        { "pattern", StringParam("Search pattern (text or regex). Examples: 'TODO', 'catch.*Exception', 'async Task'") },
                        { "isRegex", BoolParam("Treat pattern as regex (default: true)", true) },
                        { "caseSensitive", BoolParam("Case-sensitive search (default: false)", false) },
                        { "contextLines", IntParam("Number of context lines before/after match (default: 2)", 2) },
                        { "maxResults", IntParam("Maximum number of matches to return (default: 50)", 50) }
                    },
                    new[] { "pattern" }),

                MakeTool("codemerger_get_context_for_task",
                    "SMART CONTEXT: Describe what you want to do and get the most relevant files automatically. Analyzes your task description, extracts keywords, scores files by relevance, and returns prioritized context with suggestions. Use this FIRST when starting a new task!",
                    new Dictionary<string, object>
                    {
                        { "task", StringParam("Natural language description of what you want to do") },
                        { "maxFiles", IntParam("Maximum number of relevant files to return (default: 10)", 10) },
                        { "maxTokens", IntParam("Maximum total tokens for context budget (default: 50000)", 50000) }
                    },
                    new[] { "task" }),

                // ===== SEMANTIC ANALYSIS TOOLS =====
                MakeTool("codemerger_get_method_body",
                    "Get a specific method's full body, signature, parameters, and documentation. More efficient than getting the whole file when you only need one method.",
                    new Dictionary<string, object>
                    {
                        { "typeName", StringParam("Name of the type containing the method") },
                        { "methodName", StringParam("Name of the method") }
                    },
                    new[] { "typeName", "methodName" }),

                MakeTool("codemerger_find_usages",
                    "Find all usages of a symbol (type, method, property, field) across the codebase. Shows definitions, references, invocations, and implementations.",
                    new Dictionary<string, object>
                    {
                        { "symbolName", StringParam("Name of the symbol to find") },
                        { "symbolKind", StringParam("Optional filter: Type, Method, Property, Field") }
                    },
                    new[] { "symbolName" }),

                MakeTool("codemerger_get_call_graph",
                    "Get the call graph for a method - who calls it (callers) and what it calls (callees). Essential for understanding code flow and impact analysis.",
                    new Dictionary<string, object>
                    {
                        { "typeName", StringParam("Name of the type containing the method (optional for broader search)") },
                        { "methodName", StringParam("Name of the method") },
                        { "depth", IntParam("How deep to trace calls (default: 2)", 2) }
                    },
                    new[] { "methodName" }),

                MakeTool("codemerger_find_implementations",
                    "Find all implementations of an interface or overrides of a base class method.",
                    new Dictionary<string, object>
                    {
                        { "interfaceName", StringParam("Name of the interface or base class") },
                        { "methodName", StringParam("Optional: specific method to find implementations of") }
                    },
                    new[] { "interfaceName" }),

                MakeTool("codemerger_semantic_query",
                    "Find code elements matching semantic criteria: async methods, static members, specific return types, missing docs, etc.",
                    new Dictionary<string, object>
                    {
                        { "isAsync", BoolParam("Find async methods") },
                        { "isStatic", BoolParam("Find static members") },
                        { "isVirtual", BoolParam("Find virtual members") },
                        { "returnType", StringParam("Filter by return type (e.g., 'Task', 'string')") },
                        { "memberKind", StringParam("Filter: Method, Property, Field, Event, Constructor") },
                        { "accessModifier", StringParam("Filter: public, private, protected, internal") },
                        { "namePattern", StringParam("Filter by name pattern") },
                        { "implementsInterface", StringParam("Find types implementing this interface") },
                        { "findInterfaces", BoolParam("Find all interface definitions") },
                        { "missingXmlDocs", BoolParam("Find public members without XML documentation") }
                    }),

                // ===== REFACTORING TOOLS =====
                MakeTool("codemerger_write_file",
                    "Write content to a file (create new or overwrite existing). Creates a backup of existing files. Returns diff of changes.",
                    new Dictionary<string, object>
                    {
                        { "path", StringParam("Relative path for the file") },
                        { "content", StringParam("Full content to write") },
                        { "createBackup", BoolParam("Create .bak backup of existing files (default: true)", true) }
                    },
                    new[] { "path", "content" }),

                MakeTool("codemerger_preview_write",
                    "Preview what a file write would look like without actually writing. Shows diff against existing file.",
                    new Dictionary<string, object>
                    {
                        { "path", StringParam("Relative path for the file") },
                        { "content", StringParam("Content to preview") }
                    },
                    new[] { "path", "content" }),

                MakeTool("codemerger_rename_symbol",
                    "Rename a symbol across all files in the project. Use preview=true first to see what would change.",
                    new Dictionary<string, object>
                    {
                        { "oldName", StringParam("Current name of the symbol") },
                        { "newName", StringParam("New name for the symbol") },
                        { "preview", BoolParam("Preview changes without applying (default: true)", true) }
                    },
                    new[] { "oldName", "newName" }),

                MakeTool("codemerger_generate_interface",
                    "Generate an interface from a class's public members. Returns the interface code and suggested file path.",
                    new Dictionary<string, object>
                    {
                        { "className", StringParam("Name of the class to extract interface from") },
                        { "interfaceName", StringParam("Name for the new interface (default: I{ClassName})") }
                    },
                    new[] { "className" }),

                MakeTool("codemerger_extract_method",
                    "Extract a range of lines into a new method. Returns the modified file content.",
                    new Dictionary<string, object>
                    {
                        { "filePath", StringParam("Relative path to the file") },
                        { "startLine", IntParam("First line to extract (1-based)") },
                        { "endLine", IntParam("Last line to extract (1-based)") },
                        { "methodName", StringParam("Name for the new method") }
                    },
                    new[] { "filePath", "startLine", "endLine", "methodName" })
            };

            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new { tools }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        #region Tool Schema Helpers

        private object MakeTool(string name, string description, Dictionary<string, object> properties, string[]? required = null)
        {
            return new
            {
                name,
                description,
                inputSchema = new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", properties },
                    { "required", required ?? Array.Empty<string>() }
                }
            };
        }

        private Dictionary<string, object> StringParam(string description) =>
            new() { { "type", "string" }, { "description", description } };

        private Dictionary<string, object> IntParam(string description, int? defaultVal = null)
        {
            var param = new Dictionary<string, object> { { "type", "integer" }, { "description", description } };
            if (defaultVal.HasValue) param["default"] = defaultVal.Value;
            return param;
        }

        private Dictionary<string, object> BoolParam(string description, bool? defaultVal = null)
        {
            var param = new Dictionary<string, object> { { "type", "boolean" }, { "description", description } };
            if (defaultVal.HasValue) param["default"] = defaultVal.Value;
            return param;
        }

        #endregion

        private string HandleToolCall(int id, JsonElement root)
        {
            var paramsEl = root.GetProperty("params");
            var toolName = paramsEl.GetProperty("name").GetString();
            var arguments = paramsEl.TryGetProperty("arguments", out var argsEl) ? argsEl : default;

            Log($"Tool call: {toolName}");

            if (_projectAnalysis == null)
            {
                return CreateToolResponse(id, "Error: No project indexed. Please select a project in CodeMerger first.");
            }

            var result = toolName switch
            {
                // Basic tools
                "codemerger_get_project_overview" => GetProjectOverview(),
                "codemerger_list_files" => ListFiles(arguments),
                "codemerger_get_file" => GetFile(arguments),
                "codemerger_search_code" => SearchCode(arguments),
                "codemerger_get_type" => GetType(arguments),
                "codemerger_get_dependencies" => GetDependencies(arguments),
                "codemerger_get_type_hierarchy" => GetTypeHierarchy(),
                
                // Smart context tools
                "codemerger_search_content" => SearchContent(arguments),
                "codemerger_get_context_for_task" => GetContextForTask(arguments),
                
                // Semantic analysis tools
                "codemerger_get_method_body" => GetMethodBody(arguments),
                "codemerger_find_usages" => FindUsages(arguments),
                "codemerger_get_call_graph" => GetCallGraph(arguments),
                "codemerger_find_implementations" => FindImplementations(arguments),
                "codemerger_semantic_query" => SemanticQuery(arguments),
                
                // Refactoring tools
                "codemerger_write_file" => WriteFile(arguments),
                "codemerger_preview_write" => PreviewWriteFile(arguments),
                "codemerger_rename_symbol" => RenameSymbol(arguments),
                "codemerger_generate_interface" => GenerateInterface(arguments),
                "codemerger_extract_method" => ExtractMethod(arguments),
                
                _ => $"Unknown tool: {toolName}"
            };

            return CreateToolResponse(id, result);
        }

        #region Basic Tool Implementations

        private string GetProjectOverview()
        {
            if (_projectAnalysis == null) return "No project indexed.";

            var sb = new StringBuilder();
            sb.AppendLine($"# Project: {_projectAnalysis.ProjectName}");
            sb.AppendLine();
            sb.AppendLine($"**Framework:** {_projectAnalysis.DetectedFramework}");
            sb.AppendLine($"**Total Files:** {_projectAnalysis.TotalFiles}");
            sb.AppendLine($"**Total Tokens:** {_projectAnalysis.TotalTokens:N0}");
            sb.AppendLine($"**Total Types:** {_projectAnalysis.TypeHierarchy.Count}");
            sb.AppendLine($"**Call Sites Tracked:** {_codeAnalyzer.CallSites.Count}");
            sb.AppendLine();

            sb.AppendLine("## File Breakdown");
            var byClassification = _projectAnalysis.AllFiles
                .GroupBy(f => f.Classification)
                .OrderByDescending(g => g.Count());

            foreach (var group in byClassification)
            {
                sb.AppendLine($"- {group.Key}: {group.Count()} files");
            }

            sb.AppendLine();
            sb.AppendLine("## Key Entry Points");
            var entryPoints = _projectAnalysis.AllFiles
                .Where(f => f.FileName.Contains("Program") || f.FileName.Contains("App.xaml") || f.FileName.Contains("Startup"))
                .Take(5);

            foreach (var file in entryPoints)
            {
                sb.AppendLine($"- {file.RelativePath}");
            }

            return sb.ToString();
        }

        private string ListFiles(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";

            var files = _projectAnalysis.AllFiles.AsEnumerable();

            if (arguments.ValueKind != JsonValueKind.Undefined && arguments.TryGetProperty("classification", out var classEl))
            {
                var classFilter = classEl.GetString();
                if (Enum.TryParse<FileClassification>(classFilter, true, out var classification))
                {
                    files = files.Where(f => f.Classification == classification);
                }
            }

            var limit = 50;
            if (arguments.ValueKind != JsonValueKind.Undefined && arguments.TryGetProperty("limit", out var limitEl))
            {
                limit = limitEl.GetInt32();
            }

            var sb = new StringBuilder();
            sb.AppendLine("| File | Classification | Tokens |");
            sb.AppendLine("|------|----------------|--------|");

            foreach (var file in files.Take(limit))
            {
                sb.AppendLine($"| {file.RelativePath} | {file.Classification} | {file.EstimatedTokens:N0} |");
            }

            var total = files.Count();
            if (total > limit)
            {
                sb.AppendLine();
                sb.AppendLine($"*Showing {limit} of {total} files.*");
            }

            return sb.ToString();
        }

        private string GetFile(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("path", out var pathEl))
            {
                return "Error: 'path' parameter is required.";
            }

            var path = pathEl.GetString();
            var normalizedPath = path?.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            var file = _projectAnalysis.AllFiles.FirstOrDefault(f =>
            {
                var normalizedFilePath = f.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                return normalizedFilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                       f.FileName.Equals(path, StringComparison.OrdinalIgnoreCase);
            });

            if (file == null)
            {
                return $"File not found: {path}\n\nAvailable files:\n" +
                       string.Join("\n", _projectAnalysis.AllFiles.Take(10).Select(f => $"- {f.RelativePath}"));
            }

            try
            {
                var content = File.ReadAllText(file.FilePath);
                var sb = new StringBuilder();
                sb.AppendLine($"# {file.RelativePath}");
                sb.AppendLine();
                sb.AppendLine($"**Classification:** {file.Classification}");
                sb.AppendLine($"**Tokens:** {file.EstimatedTokens:N0}");
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

        private string SearchCode(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("query", out var queryEl))
            {
                return "Error: 'query' parameter is required.";
            }

            var query = queryEl.GetString()?.ToLowerInvariant() ?? "";
            var searchIn = "all";
            if (arguments.TryGetProperty("searchIn", out var searchInEl))
            {
                searchIn = searchInEl.GetString()?.ToLowerInvariant() ?? "all";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Search Results for: {query}");
            sb.AppendLine();

            if (searchIn == "all" || searchIn == "types")
            {
                var matchingTypes = _projectAnalysis.AllFiles
                    .SelectMany(f => f.Types.Select(t => new { File = f, Type = t }))
                    .Where(x => x.Type.Name.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingTypes.Any())
                {
                    sb.AppendLine("## Types");
                    foreach (var match in matchingTypes)
                    {
                        sb.AppendLine($"- **{match.Type.Name}** ({match.Type.Kind}) in `{match.File.RelativePath}`");
                    }
                    sb.AppendLine();
                }
            }

            if (searchIn == "all" || searchIn == "methods")
            {
                var matchingMethods = _projectAnalysis.AllFiles
                    .SelectMany(f => f.Types.SelectMany(t => t.Members.Select(m => new { File = f, Type = t, Member = m })))
                    .Where(x => x.Member.Name.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingMethods.Any())
                {
                    sb.AppendLine("## Methods/Members");
                    foreach (var match in matchingMethods)
                    {
                        var sig = !string.IsNullOrEmpty(match.Member.Signature) ? match.Member.Signature : match.Member.Name;
                        sb.AppendLine($"- **{match.Type.Name}.{sig}** in `{match.File.RelativePath}`");
                    }
                    sb.AppendLine();
                }
            }

            if (searchIn == "all" || searchIn == "files")
            {
                var matchingFiles = _projectAnalysis.AllFiles
                    .Where(f => f.FileName.ToLowerInvariant().Contains(query) ||
                               f.RelativePath.ToLowerInvariant().Contains(query))
                    .Take(20);

                if (matchingFiles.Any())
                {
                    sb.AppendLine("## Files");
                    foreach (var file in matchingFiles)
                    {
                        sb.AppendLine($"- `{file.RelativePath}` ({file.Classification})");
                    }
                }
            }

            if (sb.Length < 50)
            {
                sb.AppendLine("*No results found.*");
            }

            return sb.ToString();
        }

        private string GetType(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";
            var match = _projectAnalysis.AllFiles
                .SelectMany(f => f.Types.Select(t => new { File = f, Type = t }))
                .FirstOrDefault(x => x.Type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                return $"Type not found: {typeName}\n\nAvailable types:\n" +
                       string.Join("\n", _projectAnalysis.TypeHierarchy.Keys.Take(20).Select(t => $"- {t}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# {match.Type.Name}");
            sb.AppendLine();
            sb.AppendLine($"**Kind:** {match.Type.Kind}");
            sb.AppendLine($"**File:** `{match.File.RelativePath}` (lines {match.Type.StartLine}-{match.Type.EndLine})");

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
                    var mods = new List<string>();
                    if (member.IsStatic) mods.Add("static");
                    if (member.IsAsync) mods.Add("async");
                    if (member.IsVirtual) mods.Add("virtual");
                    if (member.IsOverride) mods.Add("override");
                    var modStr = mods.Any() ? $" [{string.Join(", ", mods)}]" : "";
                    sb.AppendLine($"- {member.AccessModifier} {sig}{ret}{modStr} (line {member.StartLine})");
                }
            }

            return sb.ToString();
        }

        private string GetDependencies(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";

            var sb = new StringBuilder();
            sb.AppendLine($"# Dependencies for: {typeName}");
            sb.AppendLine();

            if (_projectAnalysis.DependencyMap.TryGetValue(typeName, out var deps) && deps.Count > 0)
            {
                sb.AppendLine("## Uses (depends on)");
                foreach (var dep in deps)
                {
                    sb.AppendLine($"- {dep}");
                }
                sb.AppendLine();
            }

            var reverseDeps = _projectAnalysis.DependencyMap
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

        private string GetTypeHierarchy()
        {
            if (_projectAnalysis == null) return "No project indexed.";

            var sb = new StringBuilder();
            sb.AppendLine("# Type Hierarchy");
            sb.AppendLine();

            foreach (var kvp in _projectAnalysis.TypeHierarchy.OrderBy(k => k.Key))
            {
                var inheritance = kvp.Value.Count > 0 ? $" : {string.Join(", ", kvp.Value)}" : "";
                sb.AppendLine($"- **{kvp.Key}**{inheritance}");
            }

            return sb.ToString();
        }

        #endregion

        #region Smart Context Tools

        private string SearchContent(JsonElement arguments)
        {
            if (_contextAnalyzer == null) return "Context analyzer not initialized.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("pattern", out var patternEl))
            {
                return "Error: 'pattern' parameter is required.";
            }

            var pattern = patternEl.GetString() ?? "";
            var isRegex = GetBool(arguments, "isRegex", true);
            var caseSensitive = GetBool(arguments, "caseSensitive", false);
            var contextLines = GetInt(arguments, "contextLines", 2);
            var maxResults = GetInt(arguments, "maxResults", 50);

            var result = _contextAnalyzer.SearchContent(pattern, isRegex, caseSensitive, contextLines, maxResults);
            return result.ToMarkdown();
        }

        private string GetContextForTask(JsonElement arguments)
        {
            if (_contextAnalyzer == null) return "Context analyzer not initialized.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("task", out var taskEl))
            {
                return "Error: 'task' parameter is required.";
            }

            var task = taskEl.GetString() ?? "";
            var maxFiles = GetInt(arguments, "maxFiles", 10);
            var maxTokens = GetInt(arguments, "maxTokens", 50000);

            var result = _contextAnalyzer.GetContextForTask(task, maxFiles, maxTokens);
            return result.ToMarkdown();
        }

        #endregion

        #region Semantic Analysis Tools

        private string GetMethodBody(JsonElement arguments)
        {
            if (_semanticAnalyzer == null) return "Semantic analyzer not initialized.";

            var typeName = GetString(arguments, "typeName");
            var methodName = GetString(arguments, "methodName");

            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
            {
                return "Error: 'typeName' and 'methodName' parameters are required.";
            }

            var result = _semanticAnalyzer.GetMethodBody(typeName, methodName);
            return result.ToMarkdown();
        }

        private string FindUsages(JsonElement arguments)
        {
            if (_semanticAnalyzer == null) return "Semantic analyzer not initialized.";

            var symbolName = GetString(arguments, "symbolName");
            if (string.IsNullOrEmpty(symbolName))
            {
                return "Error: 'symbolName' parameter is required.";
            }

            var symbolKind = GetString(arguments, "symbolKind");
            var result = _semanticAnalyzer.FindUsages(symbolName, symbolKind);
            return result.ToMarkdown();
        }

        private string GetCallGraph(JsonElement arguments)
        {
            if (_semanticAnalyzer == null) return "Semantic analyzer not initialized.";

            var methodName = GetString(arguments, "methodName");
            if (string.IsNullOrEmpty(methodName))
            {
                return "Error: 'methodName' parameter is required.";
            }

            var typeName = GetString(arguments, "typeName") ?? "";
            var depth = GetInt(arguments, "depth", 2);

            var result = _semanticAnalyzer.GetCallGraph(typeName, methodName, depth);
            return result.ToMarkdown();
        }

        private string FindImplementations(JsonElement arguments)
        {
            if (_semanticAnalyzer == null) return "Semantic analyzer not initialized.";

            var interfaceName = GetString(arguments, "interfaceName");
            if (string.IsNullOrEmpty(interfaceName))
            {
                return "Error: 'interfaceName' parameter is required.";
            }

            var methodName = GetString(arguments, "methodName");
            var result = _semanticAnalyzer.FindImplementations(interfaceName, methodName);
            return result.ToMarkdown();
        }

        private string SemanticQuery(JsonElement arguments)
        {
            if (_semanticAnalyzer == null) return "Semantic analyzer not initialized.";

            var options = new SemanticQueryOptions
            {
                IsAsync = GetNullableBool(arguments, "isAsync"),
                IsStatic = GetNullableBool(arguments, "isStatic"),
                IsVirtual = GetNullableBool(arguments, "isVirtual"),
                ReturnType = GetString(arguments, "returnType"),
                AccessModifier = GetString(arguments, "accessModifier"),
                NamePattern = GetString(arguments, "namePattern"),
                ImplementsInterface = GetString(arguments, "implementsInterface"),
                FindInterfaces = GetBool(arguments, "findInterfaces", false),
                MissingXmlDocs = GetBool(arguments, "missingXmlDocs", false)
            };

            var memberKind = GetString(arguments, "memberKind");
            if (!string.IsNullOrEmpty(memberKind) && Enum.TryParse<CodeMemberKind>(memberKind, true, out var kind))
            {
                options.MemberKind = kind;
            }

            var result = _semanticAnalyzer.SemanticQuery(options);
            return result.ToMarkdown();
        }

        #endregion

        #region Refactoring Tools

        private string WriteFile(JsonElement arguments)
        {
            if (_refactoringService == null) return "Refactoring service not initialized.";

            var path = GetString(arguments, "path");
            var content = GetString(arguments, "content");

            if (string.IsNullOrEmpty(path) || content == null)
            {
                return "Error: 'path' and 'content' parameters are required.";
            }

            var createBackup = GetBool(arguments, "createBackup", true);
            var result = _refactoringService.WriteFile(path, content, createBackup);
            return result.ToMarkdown();
        }

        private string PreviewWriteFile(JsonElement arguments)
        {
            if (_refactoringService == null) return "Refactoring service not initialized.";

            var path = GetString(arguments, "path");
            var content = GetString(arguments, "content");

            if (string.IsNullOrEmpty(path) || content == null)
            {
                return "Error: 'path' and 'content' parameters are required.";
            }

            var result = _refactoringService.PreviewWriteFile(path, content);
            return result.ToMarkdown();
        }

        private string RenameSymbol(JsonElement arguments)
        {
            if (_refactoringService == null) return "Refactoring service not initialized.";

            var oldName = GetString(arguments, "oldName");
            var newName = GetString(arguments, "newName");

            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            {
                return "Error: 'oldName' and 'newName' parameters are required.";
            }

            var preview = GetBool(arguments, "preview", true);
            var result = _refactoringService.RenameSymbol(oldName, newName, preview);
            return result.ToMarkdown();
        }

        private string GenerateInterface(JsonElement arguments)
        {
            if (_refactoringService == null) return "Refactoring service not initialized.";

            var className = GetString(arguments, "className");
            if (string.IsNullOrEmpty(className))
            {
                return "Error: 'className' parameter is required.";
            }

            var interfaceName = GetString(arguments, "interfaceName");
            var result = _refactoringService.GenerateInterface(className, interfaceName);
            return result.ToMarkdown();
        }

        private string ExtractMethod(JsonElement arguments)
        {
            if (_refactoringService == null) return "Refactoring service not initialized.";

            var filePath = GetString(arguments, "filePath");
            var methodName = GetString(arguments, "methodName");
            var startLine = GetInt(arguments, "startLine", 0);
            var endLine = GetInt(arguments, "endLine", 0);

            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(methodName) || startLine == 0 || endLine == 0)
            {
                return "Error: 'filePath', 'startLine', 'endLine', and 'methodName' parameters are required.";
            }

            var result = _refactoringService.ExtractMethod(filePath, startLine, endLine, methodName);
            return result.ToMarkdown();
        }

        #endregion

        #region Helper Methods

        private string? GetString(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var el))
            {
                return el.GetString();
            }
            return null;
        }

        private int GetInt(JsonElement args, string name, int defaultValue)
        {
            if (args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var el))
            {
                return el.GetInt32();
            }
            return defaultValue;
        }

        private bool GetBool(JsonElement args, string name, bool defaultValue)
        {
            if (args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var el))
            {
                return el.GetBoolean();
            }
            return defaultValue;
        }

        private bool? GetNullableBool(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var el))
            {
                return el.GetBoolean();
            }
            return null;
        }

        private string CreateToolResponse(int id, string content)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = content }
                    }
                }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private string CreateErrorResponse(int id, int code, string message)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[MCP] {message}");
        }

        #endregion
    }
}
