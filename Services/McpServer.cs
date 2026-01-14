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
        private ContextAnalyzer? _contextAnalyzer; // NEW: Context analyzer instance
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
            
            // NEW: Initialize context analyzer with project analysis
            _contextAnalyzer = new ContextAnalyzer(_projectAnalysis);

            Log($"Indexed {fileAnalyses.Count} files, {_projectAnalysis.TypeHierarchy.Count} types");
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
                        version = "2.0.0" // VERSION BUMP for new features
                    }
                }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private string HandleListTools(int id)
        {
            var tools = new object[]
            {
                new
                {
                    name = "codemerger_get_project_overview",
                    description = "Get high-level project information including framework, structure, total files, and entry points",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_list_files",
                    description = "List all files in the project with their classifications (View, Model, Service, etc.) and estimated tokens",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "classification", new Dictionary<string, string> { { "type", "string" }, { "description", "Filter by classification: View, Model, Service, Controller, Test, Config, Unknown" } } },
                                { "limit", new Dictionary<string, string> { { "type", "integer" }, { "description", "Maximum files to return (default 50)" } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_get_file",
                    description = "Get the full content of a specific file by its relative path",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                },
                new
                {
                    name = "codemerger_search_code",
                    description = "Search for types, methods, or keywords in the codebase",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "query", new Dictionary<string, string> { { "type", "string" }, { "description", "Search query (type name, method name, or keyword)" } } },
                                { "searchIn", new Dictionary<string, string> { { "type", "string" }, { "description", "Where to search: types, methods, files, all (default: all)" } } }
                            }
                        },
                        { "required", new[] { "query" } }
                    }
                },
                new
                {
                    name = "codemerger_get_type",
                    description = "Get detailed information about a specific type including its members, base types, and interfaces",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the type" } } }
                            }
                        },
                        { "required", new[] { "typeName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_dependencies",
                    description = "Get dependencies of a type (what it uses) and reverse dependencies (what uses it)",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the type" } } }
                            }
                        },
                        { "required", new[] { "typeName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_type_hierarchy",
                    description = "Get the inheritance hierarchy for all types in the project",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                // ============================================================
                // NEW TOOL: codemerger_search_content
                // ============================================================
                new
                {
                    name = "codemerger_search_content",
                    description = "Search inside file contents using text or regex patterns. Returns matching lines with context. Great for finding usages, patterns, TODOs, or any text across the codebase.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "pattern", new Dictionary<string, string> { { "type", "string" }, { "description", "Search pattern (text or regex). Examples: 'TODO', 'catch.*Exception', 'async Task'" } } },
                                { "isRegex", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Treat pattern as regex (default: true)" }, { "default", true } } },
                                { "caseSensitive", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Case-sensitive search (default: false)" }, { "default", false } } },
                                { "contextLines", new Dictionary<string, object> { { "type", "integer" }, { "description", "Number of context lines before/after match (default: 2)" }, { "default", 2 } } },
                                { "maxResults", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum number of matches to return (default: 50)" }, { "default", 50 } } }
                            }
                        },
                        { "required", new[] { "pattern" } }
                    }
                },
                // ============================================================
                // NEW TOOL: codemerger_get_context_for_task
                // ============================================================
                new
                {
                    name = "codemerger_get_context_for_task",
                    description = "SMART CONTEXT: Describe what you want to do and get the most relevant files automatically. Analyzes your task description, extracts keywords, scores files by relevance, and returns prioritized context with suggestions. Use this FIRST when starting a new task!",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "task", new Dictionary<string, string> { { "type", "string" }, { "description", "Natural language description of what you want to do. Examples: 'Add a new MCP tool for searching content', 'Fix the file path handling', 'Add a new model class for user settings'" } } },
                                { "maxFiles", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum number of relevant files to return (default: 10)" }, { "default", 10 } } },
                                { "maxTokens", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum total tokens for context budget (default: 50000)" }, { "default", 50000 } } }
                            }
                        },
                        { "required", new[] { "task" } }
                    }
                }
            };

            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new { tools }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

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
                "codemerger_get_project_overview" => GetProjectOverview(),
                "codemerger_list_files" => ListFiles(arguments),
                "codemerger_get_file" => GetFile(arguments),
                "codemerger_search_code" => SearchCode(arguments),
                "codemerger_get_type" => GetType(arguments),
                "codemerger_get_dependencies" => GetDependencies(arguments),
                "codemerger_get_type_hierarchy" => GetTypeHierarchy(),
                // NEW TOOL HANDLERS
                "codemerger_search_content" => SearchContent(arguments),
                "codemerger_get_context_for_task" => GetContextForTask(arguments),
                _ => $"Unknown tool: {toolName}"
            };

            return CreateToolResponse(id, result);
        }

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
            sb.AppendLine();

            // Classification breakdown
            sb.AppendLine("## File Breakdown");
            var byClassification = _projectAnalysis.AllFiles
                .GroupBy(f => f.Classification)
                .OrderByDescending(g => g.Count());

            foreach (var group in byClassification)
            {
                sb.AppendLine($"- {group.Key}: {group.Count()} files");
            }

            // Entry points
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

            // Filter by classification
            if (arguments.ValueKind != JsonValueKind.Undefined && arguments.TryGetProperty("classification", out var classEl))
            {
                var classFilter = classEl.GetString();
                if (Enum.TryParse<FileClassification>(classFilter, true, out var classification))
                {
                    files = files.Where(f => f.Classification == classification);
                }
            }

            // Limit
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
                sb.AppendLine($"*Showing {limit} of {total} files. Use 'classification' filter or increase 'limit' to see more.*");
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
            
            // FIXED: Normalize path separators for cross-platform compatibility
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

            // Search types
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

            // Search methods
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

            // Search files
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
            sb.AppendLine($"**File:** {match.File.RelativePath}");

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

            // What this type depends on
            if (_projectAnalysis.DependencyMap.TryGetValue(typeName, out var deps) && deps.Count > 0)
            {
                sb.AppendLine("## Uses (depends on)");
                foreach (var dep in deps)
                {
                    sb.AppendLine($"- {dep}");
                }
                sb.AppendLine();
            }

            // What depends on this type (reverse dependencies)
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

        // ============================================================
        // NEW TOOL IMPLEMENTATION: SearchContent
        // ============================================================
        private string SearchContent(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";
            if (_contextAnalyzer == null) return "Context analyzer not initialized.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("pattern", out var patternEl))
            {
                return "Error: 'pattern' parameter is required.";
            }

            var pattern = patternEl.GetString() ?? "";
            
            // Parse optional parameters with defaults
            var isRegex = true;
            if (arguments.TryGetProperty("isRegex", out var isRegexEl))
            {
                isRegex = isRegexEl.GetBoolean();
            }

            var caseSensitive = false;
            if (arguments.TryGetProperty("caseSensitive", out var caseSensitiveEl))
            {
                caseSensitive = caseSensitiveEl.GetBoolean();
            }

            var contextLines = 2;
            if (arguments.TryGetProperty("contextLines", out var contextLinesEl))
            {
                contextLines = contextLinesEl.GetInt32();
            }

            var maxResults = 50;
            if (arguments.TryGetProperty("maxResults", out var maxResultsEl))
            {
                maxResults = maxResultsEl.GetInt32();
            }

            Log($"SearchContent: pattern='{pattern}', isRegex={isRegex}, caseSensitive={caseSensitive}");

            var result = _contextAnalyzer.SearchContent(pattern, isRegex, caseSensitive, contextLines, maxResults);
            return result.ToMarkdown();
        }

        // ============================================================
        // NEW TOOL IMPLEMENTATION: GetContextForTask
        // ============================================================
        private string GetContextForTask(JsonElement arguments)
        {
            if (_projectAnalysis == null) return "No project indexed.";
            if (_contextAnalyzer == null) return "Context analyzer not initialized.";

            if (arguments.ValueKind == JsonValueKind.Undefined || !arguments.TryGetProperty("task", out var taskEl))
            {
                return "Error: 'task' parameter is required.";
            }

            var task = taskEl.GetString() ?? "";

            // Parse optional parameters with defaults
            var maxFiles = 10;
            if (arguments.TryGetProperty("maxFiles", out var maxFilesEl))
            {
                maxFiles = maxFilesEl.GetInt32();
            }

            var maxTokens = 50000;
            if (arguments.TryGetProperty("maxTokens", out var maxTokensEl))
            {
                maxTokens = maxTokensEl.GetInt32();
            }

            Log($"GetContextForTask: task='{task}', maxFiles={maxFiles}, maxTokens={maxTokens}");

            var result = _contextAnalyzer.GetContextForTask(task, maxFiles, maxTokens);
            return result.ToMarkdown();
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
    }
}
