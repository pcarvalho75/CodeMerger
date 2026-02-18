using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeMerger.Models;
using CodeMerger.Services.Mcp;

namespace CodeMerger.Services
{
    /// <summary>
    /// MCP Server for CodeMerger - provides code analysis tools via Model Context Protocol.
    /// Coordinates tool handlers for read, write, semantic, refactoring, and workspace operations.
    /// </summary>
    public class McpServer
    {
        private readonly CodeAnalyzer _codeAnalyzer;
        private readonly IndexGenerator _indexGenerator;
        private readonly WorkspaceService _workspaceService;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _serverTask;

        // Thread safety lock for workspace state
        private readonly object _stateLock = new object();

        private CodeAnalyzer? _updateAnalyzer; // Reusable analyzer for incremental updates
        private DateTime _lastEditTimestamp = DateTime.UtcNow; // Updated by every write tool

        // Workspace state
        private WorkspaceAnalysis? _workspaceAnalysis;
        private RefactoringService? _refactoringService;
        private List<CallSite> _callSites = new();
        private List<string> _inputDirectories = new();
        private string _workspaceName = string.Empty;

        // Merged workspace mode - tracks which workspace each directory came from
        private bool _isMergedMode = false;
        private Dictionary<string, string> _directoryToWorkspace = new();

        // File scanning settings
        private List<string> _extensions = new();
        private HashSet<string> _ignoredDirs = new();

        // Tool handlers
        private McpReadToolHandler? _readHandler;
        private McpWriteToolHandler? _writeHandler;
        private McpSemanticToolHandler? _semanticHandler;
        private McpRefactoringToolHandler? _refactoringHandler;
        private McpWorkspaceToolHandler? _workspaceHandler;
        private McpMaintenanceToolHandler? _maintenanceHandler;
        private McpLessonToolHandler? _lessonHandler;
        private McpNotesToolHandler? _notesHandler;
        private McpGitToolHandler? _gitHandler;

        // Services
        private readonly LessonService _lessonService;
        private readonly CommunityLessonSyncService _communitySyncService;
        private readonly McpLogger _logger;

        // Workspace settings
        private WorkspaceSettings _workspaceSettings = WorkspaceSettings.GetDefaultSettings();

        // HTTP Transport for ChatGPT Desktop (Streamable HTTP)
        private McpHttpTransport? _httpTransport;

        // Stdio transport for notifications
        private StreamWriter? _stdioWriter;

        // Memory management
        private int _toolCallsSinceGC = 0;
        private const int GC_INTERVAL = 50; // Force GC every 50 tool calls

        public bool IsRunning => _serverTask != null && !_serverTask.IsCompleted;
        public bool IsSseRunning => _httpTransport?.IsRunning ?? false;
        public int SsePort => _httpTransport?.Port ?? 0;
        
        public event Action<string>? OnLog;
        public event Action<string>? OnSseClientConnected;
        public event Action<string>? OnSseClientDisconnected;
        public event Action<string>? OnSseMessageReceived;

        public const string ActivityPipeName = "codemerger_activity";
        public const string CommandPipeName = "codemerger_command";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public McpServer()
        {
            _codeAnalyzer = new CodeAnalyzer();
            _indexGenerator = new IndexGenerator();
            _workspaceService = new WorkspaceService();
            _lessonService = new LessonService();
            _communitySyncService = new CommunityLessonSyncService(_lessonService, Log);
            _logger = new McpLogger();
            
            // Forward logger events to our OnLog event
            _logger.OnLog += msg => OnLog?.Invoke(msg);
        }

        /// <summary>
        /// Gets the path to the log file for external access.
        /// </summary>
        public string LogFilePath => _logger.LogFilePath;

        public void IndexWorkspace(string workspaceName, List<string> inputDirectories, List<string> extensions, HashSet<string> ignoredDirs, string? settingsPath = null)
        {
            _logger.LogSeparator($"INDEX: {workspaceName}");
            
            _workspaceName = workspaceName;
            _inputDirectories = inputDirectories;
            _extensions = extensions;
            _ignoredDirs = ignoredDirs;

            // Load workspace settings
            if (!string.IsNullOrEmpty(settingsPath))
            {
                _workspaceSettings = WorkspaceSettings.LoadFromWorkspace(settingsPath, Log);
            }
            else
            {
                _workspaceSettings = WorkspaceSettings.GetDefaultSettings();
            }

            PerformIndexing();
        }

        /// <summary>
        /// Switches to a different workspace without restarting the server.
        /// Loads the new workspace config and re-indexes.
        /// Supports comma-separated names for merged workspace mode (e.g., "SmartMoney,Sequoia").
        /// </summary>
        public bool SwitchToWorkspace(string workspaceName, bool notifyGui = true)
        {
            // Check for merged workspace request (comma-separated names)
            if (workspaceName.Contains(','))
            {
                var names = workspaceName
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToArray();

                if (names.Length > 1)
                {
                    return LoadMergedWorkspaces(names, notifyGui);
                }
                // Single name after split, treat as normal
                workspaceName = names.FirstOrDefault() ?? workspaceName;
            }

            // Reset merged mode for single workspace
            _isMergedMode = false;
            _directoryToWorkspace.Clear();

            var workspace = _workspaceService.LoadWorkspace(workspaceName);
            if (workspace == null)
            {
                Log($"SwitchToWorkspace failed: workspace '{workspaceName}' not found");
                return false;
            }

            _logger.LogSeparator($"SWITCH: {workspaceName}");
            Log($"Switching to workspace: {workspaceName}");

            // Load workspace settings from workspace folder
            var workspaceFolder = _workspaceService.GetWorkspaceFolder(workspaceName);
            _workspaceSettings = WorkspaceSettings.LoadFromWorkspace(workspaceFolder, Log);
            Log($"Settings loaded: CreateBackupFiles={_workspaceSettings.CreateBackupFiles}");

            // Parse extensions
            var extensions = workspace.Extensions
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => ext.Trim())
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToList();

            // Parse ignored directories
            var ignoredDirsInput = workspace.IgnoredDirectories + ",.git";
            var ignoredDirNames = ignoredDirsInput
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => dir.Trim().ToLowerInvariant())
                .ToHashSet();

            // Update instance state - filter out disabled directories
            _workspaceName = workspaceName;
            _inputDirectories = workspace.InputDirectories
                .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                .ToList();
            _extensions = extensions;
            _ignoredDirs = ignoredDirNames;

            // Clear update state to free memory
            _updateAnalyzer = null;

            // CRITICAL: Null out handlers FIRST to release references to old workspace
            _readHandler = null;
            _writeHandler = null;
            _semanticHandler = null;
            _refactoringHandler = null;
            _maintenanceHandler = null;
            _workspaceHandler = null;
            _refactoringService = null;

            // Clear old workspace data before creating new
            _workspaceAnalysis = null;
            _callSites.Clear();

            // Force GC NOW to release old workspace memory before loading new one
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            // Set as active workspace
            _workspaceService.SetActiveWorkspace(workspaceName);

            // Re-index with new config
            PerformIndexing();

            LogWithMemory($"Switched to workspace: {workspaceName} ({_workspaceAnalysis?.TotalFiles ?? 0} files)");
            
            // Notify GUI about workspace switch
            if (notifyGui)
                SendActivity($"WORKSPACE_SWITCHED|{workspaceName}|");
            
            return true;
        }

        /// <summary>
        /// Loads multiple workspaces into a single merged virtual workspace.
        /// Files are tagged with their source workspace for clear project boundaries.
        /// Shared directories appear with both workspace tags.
        /// </summary>
        private bool LoadMergedWorkspaces(string[] workspaceNames, bool notifyGui = true)
        {
            _logger.LogSeparator($"MERGE: {string.Join(", ", workspaceNames)}");
            Log($"Loading merged workspaces: {string.Join(", ", workspaceNames)}");

            // Load all workspace configs
            var workspaces = new List<(string Name, Workspace Config)>();
            foreach (var name in workspaceNames)
            {
                var ws = _workspaceService.LoadWorkspace(name);
                if (ws == null)
                {
                    Log($"LoadMergedWorkspaces failed: workspace '{name}' not found");
                    return false;
                }
                workspaces.Add((name, ws));
            }

            // Load settings from first workspace (merged mode uses first workspace's settings)
            var firstWorkspaceFolder = _workspaceService.GetWorkspaceFolder(workspaceNames[0]);
            _workspaceSettings = WorkspaceSettings.LoadFromWorkspace(firstWorkspaceFolder, Log);
            Log($"Settings loaded from {workspaceNames[0]}: CreateBackupFiles={_workspaceSettings.CreateBackupFiles}");

            // Combine extensions from all workspaces
            var allExtensions = workspaces
                .SelectMany(w => w.Config.Extensions
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim()))
                .Where(ext => !string.IsNullOrEmpty(ext))
                .Distinct()
                .ToList();

            // Combine ignored directories from all workspaces
            var allIgnoredDirs = workspaces
                .SelectMany(w => (w.Config.IgnoredDirectories + ",.git")
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant()))
                .ToHashSet();

            // Build directory-to-workspace mapping (a directory can belong to multiple workspaces)
            _directoryToWorkspace.Clear();
            var allDirectories = new List<string>();

            foreach (var (name, config) in workspaces)
            {
                var activeDirs = config.InputDirectories
                    .Where(dir => !config.DisabledDirectories.Contains(dir))
                    .ToList();

                foreach (var dir in activeDirs)
                {
                    if (_directoryToWorkspace.ContainsKey(dir))
                    {
                        // Directory exists in multiple workspaces - append workspace name
                        _directoryToWorkspace[dir] += $", {name}";
                    }
                    else
                    {
                        _directoryToWorkspace[dir] = name;
                        allDirectories.Add(dir);
                    }
                }
            }

            // Set merged mode state
            _isMergedMode = true;
            _workspaceName = $"Merged: {string.Join(", ", workspaceNames)}";
            _inputDirectories = allDirectories;
            _extensions = allExtensions;
            _ignoredDirs = allIgnoredDirs;

            // Clear update state to free memory
            _updateAnalyzer = null;

            // CRITICAL: Null out handlers FIRST to release references to old workspace
            _readHandler = null;
            _writeHandler = null;
            _semanticHandler = null;
            _refactoringHandler = null;
            _maintenanceHandler = null;
            _workspaceHandler = null;
            _refactoringService = null;

            // Clear old workspace data before creating new
            _workspaceAnalysis = null;
            _callSites.Clear();

            // Force GC NOW to release old workspace memory before loading new one
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            // Re-index with merged config
            PerformIndexing();

            LogWithMemory($"Merged workspaces loaded: {_workspaceName} ({_workspaceAnalysis?.TotalFiles ?? 0} files)");
            
            // Notify GUI about workspace switch (use first workspace name for merged)
            if (notifyGui)
                SendActivity($"WORKSPACE_SWITCHED|{_workspaceName}|");
            
            return true;
        }

        private List<string> ScanFiles()
        {
            return _inputDirectories
                .Where(Directory.Exists)
                .SelectMany(dir =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
                    }
                    catch
                    {
                        return Enumerable.Empty<string>();
                    }
                })
                .Where(file =>
                {
                    var pathParts = file.Split(Path.DirectorySeparatorChar);
                    if (pathParts.Any(part => _ignoredDirs.Contains(part.ToLowerInvariant())))
                        return false;

                    var fileExtension = Path.GetExtension(file);
                    if (_extensions.Count == 0 || _extensions.Contains("*.*")) return true;
                    return _extensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
                })
                .Distinct()
                .ToList();
        }

        private void PerformIndexing()
        {
            lock (_stateLock)
            {
                // Rescan directories to discover new/deleted files
                var files = ScanFiles();

                Log($"Starting indexing of {files.Count} files...");

                // Clear previous call sites
                _codeAnalyzer.Reset();
                _callSites.Clear();

                var fileAnalyses = new List<FileAnalysis>();
                int processed = 0;

                foreach (var file in files)
                {
                    var baseDir = _inputDirectories.FirstOrDefault(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
                    if (baseDir == null) continue;

                    var analysis = _codeAnalyzer.AnalyzeFile(file, baseDir);

                    // Set source workspace when in merged mode
                    if (_isMergedMode && _directoryToWorkspace.TryGetValue(baseDir, out var sourceWorkspace))
                    {
                        analysis.SourceWorkspace = sourceWorkspace;
                    }

                    fileAnalyses.Add(analysis);

                    processed++;

                    // For large projects, log progress and allow GC periodically
                    if (processed % 50 == 0)
                    {
                        Log($"Indexed {processed}/{files.Count} files...");
                        // Allow GC to clean up Roslyn syntax trees
                        GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
                    }
                }

                // Store call sites for semantic analysis
                _callSites = _codeAnalyzer.CallSites.ToList();

                var chunkManager = new ChunkManager(150000);
                var chunks = chunkManager.CreateChunks(fileAnalyses);
                _workspaceAnalysis = _indexGenerator.BuildWorkspaceAnalysis(_workspaceName, fileAnalyses, chunks);

                // Initialize services and handlers
                _refactoringService = new RefactoringService(_workspaceAnalysis, _inputDirectories, _workspaceSettings);
                InitializeHandlers();

                LogWithMemory($"Indexed {fileAnalyses.Count} files, {_workspaceAnalysis.TypeHierarchy.Count} types, {_callSites.Count} call sites");
            }
        }

        /// <summary>
        /// Incrementally updates a single file in the index without full re-indexing.
        /// Runs in background to avoid blocking MCP responses.
        /// Uses debouncing and a semaphore to prevent memory issues from rapid/concurrent updates.
        /// </summary>
        /// <summary>
        /// Core re-index logic for a single file. Must be called under _stateLock.
        /// </summary>
        private void ReindexFile(string filePath)
        {
            if (_workspaceAnalysis == null) return;

            var baseDir = _inputDirectories.FirstOrDefault(dir =>
                filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase));

            if (baseDir == null) return;

            // Remove old analysis for this file
            var existingFile = _workspaceAnalysis.AllFiles
                .FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (existingFile != null)
            {
                _workspaceAnalysis.AllFiles.Remove(existingFile);

                // Remove old call sites from this file
                _callSites.RemoveAll(cs => cs.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

                // Remove old types from hierarchy
                foreach (var type in existingFile.Types)
                {
                    _workspaceAnalysis.TypeHierarchy.Remove(type.Name);
                }
            }

            // Re-analyze the single file
            if (File.Exists(filePath))
            {
                // Reuse analyzer instance to avoid Roslyn memory accumulation
                _updateAnalyzer ??= new CodeAnalyzer();
                _updateAnalyzer.CallSites.Clear();

                var newAnalysis = _updateAnalyzer.AnalyzeFile(filePath, baseDir);

                // Set source workspace when in merged mode
                if (_isMergedMode && _directoryToWorkspace.TryGetValue(baseDir, out var sourceWorkspace))
                {
                    newAnalysis.SourceWorkspace = sourceWorkspace;
                }

                _workspaceAnalysis.AllFiles.Add(newAnalysis);
                newAnalysis.LastIndexedUtc = DateTime.UtcNow;

                // Add new call sites
                _callSites.AddRange(_updateAnalyzer.CallSites);

                // Update type hierarchy for types in this file
                foreach (var type in newAnalysis.Types)
                {
                    var inheritance = new List<string>();
                    if (!string.IsNullOrEmpty(type.BaseType))
                        inheritance.Add(type.BaseType);
                    inheritance.AddRange(type.Interfaces);
                    _workspaceAnalysis.TypeHierarchy[type.Name] = inheritance;
                }
            }

            Log($"Updated index for: {Path.GetFileName(filePath)}");
        }

        /// <summary>
        /// Synchronously re-index a single file. Used by write tools (str_replace, write_file, etc.)
        /// so that subsequent read/semantic tools see fresh data immediately.
        /// </summary>
        private void UpdateSingleFileSync(string filePath)
        {
            try
            {
                lock (_stateLock)
                {
                    ReindexFile(filePath);
                    _lastEditTimestamp = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Log($"Error updating index (sync): {ex.Message}");
            }
        }

        private void InitializeHandlers()
        {
            if (_workspaceAnalysis == null || _refactoringService == null) return;

            _readHandler = new McpReadToolHandler(_workspaceAnalysis, _callSites, _inputDirectories, SendActivity);
            _writeHandler = new McpWriteToolHandler(_workspaceAnalysis, _refactoringService, _inputDirectories, _workspaceSettings, UpdateSingleFileSync, SendActivity, Log);
            _semanticHandler = new McpSemanticToolHandler(_workspaceAnalysis, _callSites, SendActivity, () => _lastEditTimestamp);
            _refactoringHandler = new McpRefactoringToolHandler(_workspaceAnalysis, _refactoringService, _callSites, _workspaceSettings, SendActivity, Log);
            _maintenanceHandler = new McpMaintenanceToolHandler(_workspaceAnalysis, _inputDirectories, SendActivity);
            _workspaceHandler = new McpWorkspaceToolHandler(
                _workspaceService,
                _workspaceName,
                _inputDirectories,
                () => PerformIndexing(), // Refresh callback
                () => RequestShutdown(), // Shutdown callback
                (name) => SwitchToWorkspace(name), // Switch workspace callback
                SendActivity,
                Log);
            _lessonHandler = new McpLessonToolHandler(_lessonService, _communitySyncService, SendActivity);
            
            if (_inputDirectories.Count > 0)
                _notesHandler = new McpNotesToolHandler(_inputDirectories[0], SendActivity);

            if (_inputDirectories.Count > 0)
                _gitHandler = new McpGitToolHandler(_inputDirectories[0], SendActivity);
        }

        private void RequestShutdown()
        {
            SendDisconnect();
            _cancellationTokenSource?.Cancel();
            Task.Delay(200).ContinueWith(_ => Environment.Exit(0));
        }

        public async Task StartAsync(Stream inputStream, Stream outputStream)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            Log("MCP Server starting...");

            var parentMonitorTask = StartParentProcessMonitor(token);
            StartCommandListener(token);

            _serverTask = Task.Run(async () =>
            {
                using var reader = new StreamReader(inputStream, Encoding.UTF8);
                _stdioWriter = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var line = await reader.ReadLineAsync();

                        if (line == null)
                        {
                            Log("Stdin closed (EOF detected) - client disconnected");
                            SendDisconnect();
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var response = ProcessMessage(line);
                        if (response != null)
                        {
                            await _stdioWriter.WriteLineAsync(response);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("ServerLoop", ex);
                    }
                }

                _stdioWriter?.Dispose();
                _stdioWriter = null;
                Log("MCP Server stopped.");
            }, token);

            await _serverTask;
            _cancellationTokenSource.Cancel();
        }

        private void StartCommandListener(CancellationToken token)
        {
            Task.Run(async () =>
            {
                Log("Command listener started");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(
                            CommandPipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await pipe.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(pipe);
                        var command = await reader.ReadLineAsync();

                        if (string.IsNullOrWhiteSpace(command)) continue;

                        Log($"Command received: {command}");

                        if (command.Equals("RESYNC", StringComparison.OrdinalIgnoreCase))
                        {
                            SwitchToWorkspace(_workspaceName, notifyGui: false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError("CommandListener", ex);
                    }
                }
                Log("Command listener stopped");
            }, token);
        }

        private Task StartParentProcessMonitor(CancellationToken token)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var currentProcess = Process.GetCurrentProcess();
                    Process? parentProcess = null;

                    try
                    {
                        var parentId = GetParentProcessId(currentProcess.Id);
                        if (parentId > 0)
                        {
                            parentProcess = Process.GetProcessById(parentId);
                            Log($"Monitoring parent process: {parentProcess.ProcessName} (PID: {parentId})");
                        }
                    }
                    catch
                    {
                        Log("Could not determine parent process - skipping parent monitor");
                        return;
                    }

                    if (parentProcess == null) return;

                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(10000, token);

                        try
                        {
                            if (parentProcess.HasExited)
                            {
                                Log("Parent process has exited - shutting down");
                                RequestShutdown();
                            }
                        }
                        catch
                        {
                            Log("Parent process no longer accessible - shutting down");
                            RequestShutdown();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log($"Parent monitor error: {ex.Message}");
                }
            }, token);
        }

        private static int GetParentProcessId(int processId)
        {
            try
            {
                using var query = new System.Management.ManagementObjectSearcher(
                    $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}");

                foreach (var item in query.Get())
                {
                    return Convert.ToInt32(item["ParentProcessId"]);
                }
            }
            catch { }

            return -1;
        }

        public void Stop()
        {
            SendDisconnect();
            _cancellationTokenSource?.Cancel();

            // Stop HTTP transport
            _httpTransport?.Stop();
            _httpTransport?.Dispose();
            _httpTransport = null;

            // Clean up all resources
            _updateAnalyzer = null;

            // Release handler references
            _readHandler = null;
            _writeHandler = null;
            _semanticHandler = null;
            _refactoringHandler = null;
            _maintenanceHandler = null;
            _workspaceHandler = null;
            _refactoringService = null;

            // Release workspace data
            _workspaceAnalysis = null;
            _callSites.Clear();

            Log("MCP Server stopping...");
            
            // Dispose logger last
            _logger.Dispose();
        }

        /// <summary>
        /// Start SSE transport for ChatGPT Desktop and other HTTP-based MCP clients.
        /// </summary>
        public void StartSse(int port = 52780, bool useHttps = false)
        {
            if (_httpTransport != null)
            {
                _httpTransport.Stop();
                _httpTransport.Dispose();
            }

            _httpTransport = new McpHttpTransport(port, ProcessMessage);
            _httpTransport.OnLog += msg => OnLog?.Invoke(msg);
            _httpTransport.OnClientConnected += sessionId =>
            {
                OnSseClientConnected?.Invoke(sessionId);
                SendActivity($"ChatGPT connected: {sessionId}");
            };
            _httpTransport.OnClientDisconnected += sessionId =>
            {
                OnSseClientDisconnected?.Invoke(sessionId);
            };
            _httpTransport.OnMessageReceived += method =>
            {
                OnSseMessageReceived?.Invoke(method);
            };

            _httpTransport.Start();
            Log($"MCP HTTP transport started on http://localhost:{port}/mcp");
        }

        /// <summary>
        /// Stop HTTP transport.
        /// </summary>
        public void StopSse()
        {
            _httpTransport?.Stop();
            _httpTransport?.Dispose();
            _httpTransport = null;
            Log("MCP HTTP transport stopped");
        }

        /// <summary>
        /// Process a JSON-RPC message and return the response.
        /// Used by both stdio and SSE transports.
        /// </summary>
        internal string? ProcessMessage(string message)
        {
            try
            {
                _logger.LogJsonRpc("REQUEST", message);
                
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                var method = root.GetProperty("method").GetString();
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;

                Log($"Received: {method}");

                var response = method switch
                {
                    "initialize" => HandleInitialize(id),
                    "tools/list" => HandleListTools(id),
                    "tools/call" => HandleToolCall(id, root),
                    "notifications/initialized" => null,
                    _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
                };
                
                if (response != null)
                {
                    _logger.LogJsonRpc("RESPONSE", response);
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError("ProcessMessage", ex);
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
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "codemerger-mcp", version = "2.0.0" }
                }
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        private string HandleListTools(int id)
        {
            var tools = McpToolRegistry.GetAllTools();
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
            var toolName = paramsEl.GetProperty("name").GetString() ?? "";
            var arguments = paramsEl.TryGetProperty("arguments", out var argsEl) ? argsEl : default;

            Log($"Tool call: {toolName}");

            // Periodic memory cleanup
            _toolCallsSinceGC++;
            if (_toolCallsSinceGC >= GC_INTERVAL)
            {
                _toolCallsSinceGC = 0;
                GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
            }

            // Server control tools don't require workspace
            if (toolName == "codemerger_shutdown")
                return CreateToolResponse(id, _workspaceHandler?.Shutdown() ?? HandleShutdownFallback());

            if (toolName == "codemerger_list_projects")
                return CreateToolResponse(id, _workspaceHandler?.ListWorkspaces() ?? HandleListWorkspacesFallback());

            if (toolName == "codemerger_switch_project")
                return CreateToolResponse(id, _workspaceHandler?.SwitchWorkspace(arguments) ?? "Error: Handler not initialized");

            if (toolName == "codemerger_refresh")
                return CreateToolResponse(id, _workspaceHandler?.Refresh() ?? "Error: Handler not initialized");

            if (toolName == "codemerger_build")
            {
                if (arguments.TryGetProperty("quickCheck", out var qcEl) && qcEl.GetBoolean())
                    return CreateToolResponse(id, _semanticHandler?.GetDiagnostics(arguments) ?? "Error: Handler not initialized");
                return CreateToolResponse(id, _workspaceHandler?.Build(arguments) ?? "Error: Handler not initialized");
            }

            // Lesson tool (doesn't require workspace)
            if (toolName == "codemerger_lessons")
            {
                var command = arguments.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
                if (string.IsNullOrEmpty(command))
                    return CreateToolResponse(id, "Error: 'command' parameter is required.");
                _lessonHandler ??= new McpLessonToolHandler(_lessonService, _communitySyncService, SendActivity);
                return CreateToolResponse(id, _lessonHandler.HandleCommand(command, arguments));
            }

            // Notes tool
            if (toolName == "codemerger_notes")
            {
                if (_notesHandler == null)
                    return CreateToolResponse(id, "Error: Notes handler not initialized. Select a workspace first.");
                var command = arguments.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
                if (string.IsNullOrEmpty(command))
                    return CreateToolResponse(id, "Error: 'command' parameter is required.");
                return CreateToolResponse(id, _notesHandler.HandleCommand(command, arguments));
            }

            // Git tools
            if (toolName == "codemerger_git_status" || toolName == "codemerger_git_commit" || toolName == "codemerger_git_push")
            {
                if (_gitHandler == null)
                    return CreateToolResponse(id, "Error: Git handler not initialized. Select a workspace first.");
                var action = toolName.Replace("codemerger_git_", "");
                return CreateToolResponse(id, _gitHandler.HandleCommand(action, arguments));
            }

            // Help tool (doesn't require workspace)
            if (toolName == "codemerger_help")
            {
                var topic = arguments.TryGetProperty("topic", out var topicEl) ? topicEl.GetString() : null;
                var helpText = string.IsNullOrWhiteSpace(topic)
                    ? Models.HelpManual.GetFullManual()
                    : Models.HelpManual.SearchByTopic(topic);
                return CreateToolResponse(id, helpText);
            }

            // All other tools require workspace
            if (_workspaceAnalysis == null)
            {
                return CreateToolResponse(id, "Error: No workspace indexed. Please select a workspace in CodeMerger first.");
            }

            var result = DispatchToolCall(toolName, arguments);
            return CreateToolResponse(id, result);
        }

        private string DispatchToolCall(string toolName, JsonElement arguments)
        {
            // Note: No lock here - read operations are safe, write operations
            // trigger PerformIndexing() which has its own lock for state mutation

            // Guard: handlers may be null during workspace switch or before indexing completes
            if (_readHandler == null || _writeHandler == null || _semanticHandler == null ||
                _refactoringHandler == null || _maintenanceHandler == null)
            {
                return $"Error: Workspace handlers are still initializing. Please wait a moment and retry the '{toolName}' call.";
            }
            
            var sw = Stopwatch.StartNew();
            SendActivity($"STARTED|{toolName}|");
            SendNotification("info", $"🔧 Processing {toolName}...");

            try
            {
                var result = toolName switch
                {
                    // Read tools
                    "codemerger_get_project_overview" => _readHandler.GetWorkspaceOverview(),
                    "codemerger_list_files" => _readHandler.ListFiles(arguments),
                    "codemerger_get_file" => _readHandler.GetFile(arguments),
                    "codemerger_search_code" => _readHandler.SearchCode(arguments),
                    "codemerger_get_type" => _readHandler.GetType(arguments),
                    "codemerger_get_dependencies" => _readHandler.GetDependencies(arguments),
                    "codemerger_find_implementations" => _semanticHandler.FindImplementations(arguments),
                    "codemerger_grep" => _readHandler.Grep(arguments),
                    "codemerger_get_context" => _readHandler.GetContext(arguments),
                    "codemerger_get_lines" => _readHandler.GetLines(arguments),
                    "codemerger_get_xaml_tree" => _readHandler.GetXamlTree(arguments),
                    "codemerger_get_method_body" => _semanticHandler.GetMethodBody(arguments),

                // Semantic tools
                "codemerger_find_references" => _semanticHandler.FindReferences(arguments),
                "codemerger_get_callers" => _semanticHandler.GetCallers(arguments),
                "codemerger_get_callees" => _semanticHandler.GetCallees(arguments),

                    // Write tools
                    "codemerger_str_replace" => _writeHandler.StrReplace(arguments),
                    "codemerger_replace_lines" => _writeHandler.ReplaceLines(arguments),
                    "codemerger_write_file" => _writeHandler.WriteFile(arguments),
                    "codemerger_delete_file" => _writeHandler.DeleteFile(arguments),
                    "codemerger_create_folder" => _writeHandler.CreateFolder(arguments),
                    "codemerger_grep_replace" => _writeHandler.GrepReplace(arguments),
                    "codemerger_undo" => _writeHandler.Undo(arguments),
                    "codemerger_move_file" => _writeHandler.MoveFile(arguments),

                    // Refactoring tools
                    "codemerger_rename_symbol" => _refactoringHandler.RenameSymbol(arguments),
                    "codemerger_generate_interface" => _refactoringHandler.GenerateInterface(arguments),
                    "codemerger_extract_method" => _refactoringHandler.ExtractMethod(arguments),
                    "codemerger_add_parameter" => _refactoringHandler.AddParameter(arguments),
                    "codemerger_implement_interface" => _refactoringHandler.ImplementInterface(arguments),
                    "codemerger_generate_constructor" => _refactoringHandler.GenerateConstructor(arguments),

                    // Maintenance tools
                    "codemerger_clean_backups" => _maintenanceHandler.CleanBackups(arguments),
                    "codemerger_find_duplicates" => _maintenanceHandler.FindDuplicates(arguments),

                    _ => $"Unknown tool: {toolName}"
                };

                sw.Stop();
                SendActivity($"COMPLETED|{toolName}|{sw.ElapsedMilliseconds}ms");
                SendNotification("info", $"✅ {toolName} completed ({sw.ElapsedMilliseconds}ms)");
                
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                SendActivity($"ERROR|{toolName}|{ex.Message}");
                SendNotification("error", $"❌ {toolName} failed: {ex.Message}");
                throw;
            }
        }

        // Fallbacks for when handlers aren't initialized
        private string HandleShutdownFallback()
        {
            Task.Run(async () =>
            {
                await Task.Delay(500);
                SendDisconnect();
                _cancellationTokenSource?.Cancel();
                await Task.Delay(200);
                Environment.Exit(0);
            });
            return "# Server Shutdown\n\nCodeMerger MCP server is shutting down.";
        }

        private string HandleListWorkspacesFallback()
        {
            var workspaces = _workspaceService.LoadAllWorkspaces();
            if (workspaces.Count == 0)
                return "# Available Workspaces\n\nNo workspaces found.";

            var sb = new StringBuilder();
            sb.AppendLine("# Available Workspaces\n");
            foreach (var w in workspaces)
                sb.AppendLine($"- {w.Name}");
            return sb.ToString();
        }



        private string CreateToolResponse(int id, string content)
        {
            var response = new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    content = new[] { new { type = "text", text = content } }
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
            _logger.Log(message);
        }

        private void LogWithMemory(string message)
        {
            _logger.LogWithMemory(message);
        }

        private void SendActivity(string activity)
        {
            Task.Run(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", ActivityPipeName, PipeDirection.Out);
                    pipe.Connect(100);
                    using var writer = new StreamWriter(pipe);
                    writer.WriteLine($"{_workspaceName}|{activity}");
                    writer.Flush();
                }
                catch { }
            });
        }

        /// <summary>
        /// Send MCP notification to Claude Desktop (or other MCP client).
        /// Supports both stdio and SSE transports.
        /// </summary>
        private void SendNotification(string level, string message)
        {
            var notification = new
            {
                jsonrpc = "2.0",
                method = "notifications/message",
                @params = new
                {
                    level = level,  // debug, info, notice, warning, error
                    logger = "codemerger",
                    data = message
                }
            };

            var json = JsonSerializer.Serialize(notification, JsonOptions);

            // Send via stdio if available
            if (_stdioWriter != null)
            {
                try
                {
                    lock (_stdioWriter)
                    {
                        _stdioWriter.WriteLine(json);
                    }
                }
                catch { }
            }

            // Send via SSE if available
            if (_httpTransport?.IsRunning == true)
            {
                try
                {
                    // SSE transport handles broadcasting to all connected clients
                    _httpTransport.BroadcastNotification(json);
                }
                catch { }
            }
        }

        private void SendDisconnect()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", ActivityPipeName, PipeDirection.Out);
                pipe.Connect(200);
                using var writer = new StreamWriter(pipe);
                writer.WriteLine($"{_workspaceName}|DISCONNECT");
                writer.Flush();
            }
            catch { }
        }
    }
}
