using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
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
        private RefactoringService? _refactoringService;
        private List<string> _inputDirectories = new();
        private string _projectName = string.Empty;

        public bool IsRunning => _serverTask != null && !_serverTask.IsCompleted;
        public event Action<string>? OnLog;

        // Activity pipe for notifying MainWindow of tool calls
        public const string ActivityPipeName = "codemerger_activity";

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

            // Initialize refactoring service
            _refactoringService = new RefactoringService(_projectAnalysis, inputDirectories);

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
                        version = "1.0.0"
                    }
                }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private string HandleListTools(int id)
        {
            var tools = new object[]
            {
                // === READ TOOLS ===
                new
                {
                    name = "codemerger_get_project_overview",
                    description = "Get high-level project information including framework, structure, total files, and entry points. Use this first to understand the project before diving into specific files.",
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
                    description = "List all files in the project with their classifications (View, Model, Service, etc.) and estimated tokens. Use 'classification' filter to narrow down results.",
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
                    description = "Get the full content of a specific file by its relative path. For making changes, prefer using codemerger_write_file with surgical edits rather than rewriting entire files. If you only need to modify a small section, read the file first, then write back only with the necessary changes.",
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
                    description = "Search for types, methods, or keywords in the codebase. Use this to find where something is defined or used before making changes.",
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
                    description = "Get detailed information about a specific type including its members, base types, and interfaces. Useful for understanding a class before modifying it.",
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
                    description = "Get dependencies of a type (what it uses) and reverse dependencies (what uses it). Essential before renaming or refactoring to understand impact.",
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
                    description = "Get the inheritance hierarchy for all types in the project.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },

                // === WRITE TOOLS ===
                new
                {
                    name = "codemerger_write_file",
                    description = "Write content to a file (create new or overwrite existing). Creates a .bak backup before overwriting. IMPORTANT: For small changes to existing files, read the file first with codemerger_get_file, make your edits, then write the complete modified content. Prefer minimal changes over rewriting entire files.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file (e.g., 'Services/MyService.cs')" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "Complete file content to write" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before overwriting (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "path", "content" } }
                    }
                },
                new
                {
                    name = "codemerger_preview_write",
                    description = "Preview what a file write would look like without actually writing. Shows a diff of changes. Use this before codemerger_write_file to verify your changes are correct.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "Complete file content to preview" } } }
                            }
                        },
                        { "required", new[] { "path", "content" } }
                    }
                },
                new
                {
                    name = "codemerger_rename_symbol",
                    description = "Rename a symbol (class, method, variable) across all files in the project. Use preview=true first to see all affected locations before applying.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "oldName", new Dictionary<string, string> { { "type", "string" }, { "description", "Current name of the symbol" } } },
                                { "newName", new Dictionary<string, string> { { "type", "string" }, { "description", "New name for the symbol" } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only show what would change without applying (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "oldName", "newName" } }
                    }
                },
                new
                {
                    name = "codemerger_generate_interface",
                    description = "Generate an interface from a class's public members. Returns the generated code which you can then write to a file using codemerger_write_file.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "className", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the class to extract interface from" } } },
                                { "interfaceName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name for the generated interface (default: I{ClassName})" } } }
                            }
                        },
                        { "required", new[] { "className" } }
                    }
                },
                new
                {
                    name = "codemerger_extract_method",
                    description = "Extract a range of lines into a new method. Returns the modified file content which you can then write using codemerger_write_file.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "filePath", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "startLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "First line to extract (1-indexed)" } } },
                                { "endLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "Last line to extract (1-indexed)" } } },
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name for the new method" } } }
                            }
                        },
                        { "required", new[] { "filePath", "startLine", "endLine", "methodName" } }
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
            SendActivity($"Tool: {toolName}");

            if (_projectAnalysis == null)
            {
                return CreateToolResponse(id, "Error: No project indexed. Please select a project in CodeMerger first.");
            }

            var result = toolName switch
            {
                // Read tools
                "codemerger_get_project_overview" => GetProjectOverview(),
                "codemerger_list_files" => ListFiles(arguments),
                "codemerger_get_file" => GetFile(arguments),
                "codemerger_search_code" => SearchCode(arguments),
                "codemerger_get_type" => GetType(arguments),
                "codemerger_get_dependencies" => GetDependencies(arguments),
                "codemerger_get_type_hierarchy" => GetTypeHierarchy(),
                // Write tools
                "codemerger_write_file" => WriteFile(arguments),
                "codemerger_preview_write" => PreviewWriteFile(arguments),
                "codemerger_rename_symbol" => RenameSymbol(arguments),
                "codemerger_generate_interface" => GenerateInterface(arguments),
                "codemerger_extract_method" => ExtractMethod(arguments),
                _ => $"Unknown tool: {toolName}"
            };

            return CreateToolResponse(id, result);
        }

        private string GetProjectOverview()
        {
            if (_projectAnalysis == null) return "No project indexed.";

            SendActivity("Reading project overview");

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

            SendActivity("Listing files");

            var files = _projectAnalysis.AllFiles.AsEnumerable();

            // Filter by classification
            if (arguments.TryGetProperty("classification", out var classEl))
            {
                var classFilter = classEl.GetString();
                if (Enum.TryParse<FileClassification>(classFilter, true, out var classification))
                {
                    files = files.Where(f => f.Classification == classification);
                }
            }

            // Limit
            var limit = 50;
            if (arguments.TryGetProperty("limit", out var limitEl))
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

            if (!arguments.TryGetProperty("path", out var pathEl))
            {
                return "Error: 'path' parameter is required.";
            }

            var path = pathEl.GetString();
            SendActivity($"Reading: {path}");

            var file = _projectAnalysis.AllFiles.FirstOrDefault(f =>
                f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Equals(path, StringComparison.OrdinalIgnoreCase));

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

            if (!arguments.TryGetProperty("query", out var queryEl))
            {
                return "Error: 'query' parameter is required.";
            }

            var query = queryEl.GetString()?.ToLowerInvariant() ?? "";
            SendActivity($"Searching: {query}");

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

            if (!arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";
            SendActivity($"Getting type: {typeName}");

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

            if (!arguments.TryGetProperty("typeName", out var typeNameEl))
            {
                return "Error: 'typeName' parameter is required.";
            }

            var typeName = typeNameEl.GetString() ?? "";
            SendActivity($"Getting dependencies: {typeName}");

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

            SendActivity("Getting type hierarchy");

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

        // === WRITE TOOL IMPLEMENTATIONS ===

        private string WriteFile(JsonElement arguments)
        {
            if (_refactoringService == null) return "Error: Refactoring service not initialized.";

            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("content", out var contentEl))
                return "Error: 'content' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var content = contentEl.GetString() ?? "";

            SendActivity($"Writing: {path}");

            var createBackup = true;
            if (arguments.TryGetProperty("createBackup", out var backupEl))
                createBackup = backupEl.GetBoolean();

            var result = _refactoringService.WriteFile(path, content, createBackup);
            Log($"WriteFile: {path} - {(result.Success ? "OK" : "FAILED")}");

            return result.ToMarkdown();
        }

        private string PreviewWriteFile(JsonElement arguments)
        {
            if (_refactoringService == null) return "Error: Refactoring service not initialized.";

            if (!arguments.TryGetProperty("path", out var pathEl))
                return "Error: 'path' parameter is required.";

            if (!arguments.TryGetProperty("content", out var contentEl))
                return "Error: 'content' parameter is required.";

            var path = pathEl.GetString() ?? "";
            var content = contentEl.GetString() ?? "";

            SendActivity($"Preview: {path}");

            var result = _refactoringService.PreviewWriteFile(path, content);
            Log($"PreviewWrite: {path}");

            return result.ToMarkdown();
        }

        private string RenameSymbol(JsonElement arguments)
        {
            if (_refactoringService == null) return "Error: Refactoring service not initialized.";

            if (!arguments.TryGetProperty("oldName", out var oldNameEl))
                return "Error: 'oldName' parameter is required.";

            if (!arguments.TryGetProperty("newName", out var newNameEl))
                return "Error: 'newName' parameter is required.";

            var oldName = oldNameEl.GetString() ?? "";
            var newName = newNameEl.GetString() ?? "";

            var preview = true;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            SendActivity($"Rename: {oldName} → {newName}");

            var result = _refactoringService.RenameSymbol(oldName, newName, preview);
            Log($"RenameSymbol: {oldName} -> {newName} (preview={preview})");

            return result.ToMarkdown();
        }

        private string GenerateInterface(JsonElement arguments)
        {
            if (_refactoringService == null) return "Error: Refactoring service not initialized.";

            if (!arguments.TryGetProperty("className", out var classNameEl))
                return "Error: 'className' parameter is required.";

            var className = classNameEl.GetString() ?? "";

            SendActivity($"Generate interface: {className}");

            string? interfaceName = null;
            if (arguments.TryGetProperty("interfaceName", out var interfaceNameEl))
                interfaceName = interfaceNameEl.GetString();

            var result = _refactoringService.GenerateInterface(className, interfaceName);
            Log($"GenerateInterface: {className} -> {result.InterfaceName}");

            return result.ToMarkdown();
        }

        private string ExtractMethod(JsonElement arguments)
        {
            if (_refactoringService == null) return "Error: Refactoring service not initialized.";

            if (!arguments.TryGetProperty("filePath", out var filePathEl))
                return "Error: 'filePath' parameter is required.";

            if (!arguments.TryGetProperty("startLine", out var startLineEl))
                return "Error: 'startLine' parameter is required.";

            if (!arguments.TryGetProperty("endLine", out var endLineEl))
                return "Error: 'endLine' parameter is required.";

            if (!arguments.TryGetProperty("methodName", out var methodNameEl))
                return "Error: 'methodName' parameter is required.";

            var filePath = filePathEl.GetString() ?? "";
            var startLine = startLineEl.GetInt32();
            var endLine = endLineEl.GetInt32();
            var methodName = methodNameEl.GetString() ?? "";

            SendActivity($"Extract method: {methodName}");

            var result = _refactoringService.ExtractMethod(filePath, startLine, endLine, methodName);
            Log($"ExtractMethod: {filePath} lines {startLine}-{endLine} -> {methodName}()");

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

        /// <summary>
        /// Send activity message to MainWindow via named pipe (fire and forget).
        /// </summary>
        private void SendActivity(string activity)
        {
            Task.Run(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", ActivityPipeName, PipeDirection.Out);
                    pipe.Connect(100); // Short timeout

                    using var writer = new StreamWriter(pipe);
                    writer.WriteLine($"{_projectName}|{activity}");
                    writer.Flush();
                }
                catch
                {
                    // MainWindow not running or not listening - that's OK
                }
            });
        }
    }
}
