using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles server control and workspace management MCP tools.
    /// </summary>
    public class McpWorkspaceToolHandler
    {
        private readonly WorkspaceService _workspaceService;
        private string _workspaceName;
        private readonly List<string> _inputDirectories;
        private readonly Action _requestReindex;
        private readonly Action _requestShutdown;
        private readonly Func<string, bool> _requestSwitchWorkspace;
        private readonly Action<string> _sendActivity;
        private readonly Action<string> _log;

        public McpWorkspaceToolHandler(
            WorkspaceService workspaceService,
            string workspaceName,
            List<string> inputDirectories,
            Action requestReindex,
            Action requestShutdown,
            Func<string, bool> requestSwitchWorkspace,
            Action<string> sendActivity,
            Action<string> log)
        {
            _workspaceService = workspaceService;
            _workspaceName = workspaceName;
            _inputDirectories = inputDirectories;
            _requestReindex = requestReindex;
            _requestShutdown = requestShutdown;
            _requestSwitchWorkspace = requestSwitchWorkspace;
            _sendActivity = sendActivity;
            _log = log;
        }

        public string Refresh()
        {
            _log("Refresh requested by user");
            _sendActivity("Refreshing workspace index...");

            try
            {
                _requestReindex();

                return "# Workspace Refreshed\n\nThe workspace index has been refreshed. All files have been re-analyzed and the index is now up to date.";
            }
            catch (Exception ex)
            {
                return $"# Refresh Failed\n\n**Error:** {ex.Message}";
            }
        }

        public string Shutdown()
        {
            _log("Shutdown requested by user");
            _sendActivity("Shutting down...");

            // Schedule shutdown after returning response
            Task.Run(async () =>
            {
                await Task.Delay(500);
                _requestShutdown();
            });

            return "# Server Shutdown\n\nCodeMerger MCP server is shutting down. You can now recompile the project in Visual Studio.\n\nTo reconnect, simply start a new conversation or ask me to use a CodeMerger tool.";
        }

        public string ListWorkspaces()
        {
            _sendActivity("Listing workspaces");

            var workspaces = _workspaceService.LoadAllWorkspaces();
            var activeWorkspace = _workspaceService.GetActiveWorkspace();

            if (workspaces.Count == 0)
            {
                return "# Available Workspaces\n\nNo workspaces found. Please create a workspace in the CodeMerger GUI first.";
            }

            // Build map of directory -> workspaces that use it
            var directoryToWorkspaces = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var workspace in workspaces)
            {
                if (workspace.InputDirectories == null) continue;
                foreach (var dir in workspace.InputDirectories)
                {
                    var normalizedDir = Path.GetFullPath(dir);
                    if (!directoryToWorkspaces.ContainsKey(normalizedDir))
                        directoryToWorkspaces[normalizedDir] = new List<string>();
                    directoryToWorkspaces[normalizedDir].Add(workspace.Name);
                }
            }

            // Find shared directories (used by more than one workspace)
            var sharedDirs = directoryToWorkspaces
                .Where(kvp => kvp.Value.Count > 1)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var sb = new StringBuilder();
            sb.AppendLine("# Available Workspaces");
            sb.AppendLine();
            sb.AppendLine($"**Currently loaded:** {_workspaceName}");
            sb.AppendLine();
            sb.AppendLine("| Workspace | Directories | Status |");
            sb.AppendLine("|-----------|-------------|--------|");

            foreach (var workspace in workspaces.OrderBy(w => w.Name))
            {
                var dirCount = workspace.InputDirectories?.Count ?? 0;
                var status = workspace.Name == _workspaceName ? "✓ Loaded" :
                             workspace.Name == activeWorkspace ? "Active" : "";
                sb.AppendLine($"| {workspace.Name} | {dirCount} | {status} |");
            }

            // Show shared directories section if any exist
            if (sharedDirs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Shared Directories");
                sb.AppendLine();
                sb.AppendLine("*These directories are used by multiple workspaces - changes affect all listed workspaces.*");
                sb.AppendLine();

                foreach (var kvp in sharedDirs.OrderBy(k => k.Key))
                {
                    var dirName = Path.GetFileName(kvp.Key.TrimEnd('\\', '/'));
                    var workspaceList = string.Join(", ", kvp.Value.OrderBy(w => w));
                    sb.AppendLine($"- **{dirName}** → {workspaceList}");
                    sb.AppendLine($"  - `{kvp.Key}`");
                }
            }

            sb.AppendLine();
            sb.AppendLine("*Use `codemerger_switch_project` to switch to a different workspace (hot-swap, no restart needed).*");

            return sb.ToString();
        }

        public string SwitchWorkspace(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("projectName", out var workspaceNameEl))
            {
                return "Error: 'projectName' parameter is required.";
            }

            var workspaceName = workspaceNameEl.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                return "Error: Workspace name cannot be empty.";
            }

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
                    // Validate each workspace exists
                    var missing = new List<string>();
                    foreach (var name in names)
                    {
                        if (_workspaceService.LoadWorkspace(name) == null)
                            missing.Add(name);
                    }

                    if (missing.Count > 0)
                    {
                        var available = _workspaceService.LoadAllWorkspaces();
                        return $"Error: Workspace(s) not found: {string.Join(", ", missing)}\n\nAvailable workspaces:\n" +
                               string.Join("\n", available.Select(w => $"- {w.Name}"));
                    }

                    _log($"Merging workspaces: {string.Join(", ", names)}");
                    _sendActivity($"Merging: {string.Join(", ", names)}");

                    var mergeSuccess = _requestSwitchWorkspace(workspaceName);

                    if (mergeSuccess)
                    {
                        _workspaceName = $"Merged: {string.Join(", ", names)}";
                        return $"# Workspaces Merged\n\n" +
                               $"Successfully merged **{names.Length}** workspaces: {string.Join(", ", names)}.\n\n" +
                               $"Shared directories are deduplicated. Each file tracks its source workspace.";
                    }
                    else
                    {
                        return $"# Merge Failed\n\nFailed to merge workspaces. Check server logs for details.";
                    }
                }
            }

            var workspace = _workspaceService.LoadWorkspace(workspaceName);
            if (workspace == null)
            {
                var available = _workspaceService.LoadAllWorkspaces();
                return $"Error: Workspace '{workspaceName}' not found.\n\nAvailable workspaces:\n" +
                       string.Join("\n", available.Select(w => $"- {w.Name}"));
            }

            if (workspaceName == _workspaceName)
            {
                return $"Workspace '{workspaceName}' is already loaded.";
            }

            _log($"Switching to workspace: {workspaceName}");
            _sendActivity($"Switching to: {workspaceName}");

            // Hot-swap: switch workspace without restarting
            var success = _requestSwitchWorkspace(workspaceName);

            if (success)
            {
                _workspaceName = workspaceName; // Update local reference
                return $"# Workspace Switched\n\n" +
                       $"Successfully switched to workspace **{workspaceName}**.\n\n" +
                       $"The workspace has been re-indexed and is ready to use. No restart required.";
            }
            else
            {
                return $"# Switch Failed\n\nFailed to switch to workspace '{workspaceName}'. Check server logs for details.";
            }
        }

        /// <summary>
        /// Runs dotnet build on the project and returns real compilation results.
        /// </summary>
        public string Build(JsonElement arguments)
        {
            string configuration = "Debug";
            if (arguments.TryGetProperty("configuration", out var configEl))
                configuration = configEl.GetString() ?? "Debug";

            bool verboseOutput = false;
            if (arguments.TryGetProperty("verbose", out var verboseEl))
                verboseOutput = verboseEl.GetBoolean();

            _sendActivity($"Building ({configuration})...");

            var sb = new StringBuilder();
            sb.AppendLine("# Build Results");
            sb.AppendLine();

            try
            {
                // Find project or solution file
                string? explicitPath = null;
                if (arguments.TryGetProperty("path", out var pathEl))
                    explicitPath = pathEl.GetString();

                var (projectFile, projectType) = FindProjectFile(explicitPath);

                if (projectFile == null)
                {
                    var available = GetAllProjectFiles();
                    if (available.Count > 0)
                    {
                        var listing = string.Join("\n", available.Select(p => $"  - `{Path.GetFileName(p)}` ({p})"));
                        return $"Error: Could not resolve '{explicitPath}'. Available build targets:\n{listing}";
                    }
                    return "Error: No .csproj or .sln file found in the workspace directories.";
                }

                sb.AppendLine($"**Project:** `{Path.GetFileName(projectFile)}`");
                sb.AppendLine($"**Configuration:** {configuration}");
                sb.AppendLine();

                // Run dotnet build
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{projectFile}\" --configuration {configuration} --no-incremental",
                    WorkingDirectory = Path.GetDirectoryName(projectFile),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _log($"Running: dotnet {startInfo.Arguments}");

                using var process = new Process { StartInfo = startInfo };
                var output = new StringBuilder();
                var errorOutput = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait with timeout (2 minutes)
                bool completed = process.WaitForExit(120000);

                if (!completed)
                {
                    process.Kill();
                    return "Error: Build timed out after 2 minutes.";
                }

                var fullOutput = output.ToString() + errorOutput.ToString();

                // Parse build results
                var (errors, warnings) = ParseBuildOutput(fullOutput);

                if (process.ExitCode == 0)
                {
                    sb.AppendLine("## ✅ Build Succeeded");
                }
                else
                {
                    sb.AppendLine("## ❌ Build Failed");
                }
                sb.AppendLine();

                sb.AppendLine($"**Errors:** {errors.Count}");
                sb.AppendLine($"**Warnings:** {warnings.Count}");
                sb.AppendLine();

                if (errors.Count > 0)
                {
                    sb.AppendLine("## Errors");
                    foreach (var error in errors.Take(50))
                    {
                        sb.AppendLine($"- ❌ {error.display}");
                        // Show source context if we have file + line info
                        if (error.fullPath != null && error.line > 0)
                        {
                            try
                            {
                                var sourceLines = File.ReadAllLines(error.fullPath);
                                int startLine = Math.Max(0, error.line - 2);
                                int endLine = Math.Min(sourceLines.Length - 1, error.line + 1);
                                sb.AppendLine("  ```");
                                for (int i = startLine; i <= endLine; i++)
                                {
                                    var marker = (i == error.line - 1) ? ">>>" : "   ";
                                    sb.AppendLine($"  {marker} {i + 1}: {sourceLines[i]}");
                                }
                                sb.AppendLine("  ```");
                            }
                            catch { /* skip if file can't be read */ }
                        }
                    }
                    if (errors.Count > 50)
                        sb.AppendLine($"- ... and {errors.Count - 50} more errors");
                    sb.AppendLine();
                }

                if (warnings.Count > 0)
                {
                    sb.AppendLine("## Warnings");
                    foreach (var warning in warnings.Take(30))
                    {
                        sb.AppendLine($"- ⚠️ {warning}");
                    }
                    if (warnings.Count > 30)
                        sb.AppendLine($"- ... and {warnings.Count - 30} more warnings");
                    sb.AppendLine();
                }

                if (verboseOutput)
                {
                    sb.AppendLine("## Full Output");
                    sb.AppendLine("```");
                    sb.AppendLine(fullOutput.Length > 10000 ? fullOutput.Substring(0, 10000) + "\n... (truncated)" : fullOutput);
                    sb.AppendLine("```");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error running build: {ex.Message}\n\nMake sure `dotnet` is installed and available in PATH.";
            }
        }

        private (string? path, string type) FindProjectFile(string? explicitPath = null)
        {
            // If caller specified an explicit path, resolve and use it directly
            if (!string.IsNullOrEmpty(explicitPath))
            {
                bool IsSolution(string p) => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

                // Try as-is first (absolute path)
                if (File.Exists(explicitPath))
                    return (explicitPath, IsSolution(explicitPath) ? "solution" : "project");

                // Try resolving relative to each input directory
                foreach (var dir in _inputDirectories)
                {
                    var resolved = Path.GetFullPath(Path.Combine(dir, explicitPath));
                    if (File.Exists(resolved))
                        return (resolved, IsSolution(resolved) ? "solution" : "project");
                }

                // Try matching by filename across all known project files
                var allProjects = GetAllProjectFiles();
                var match = allProjects.FirstOrDefault(p =>
                    Path.GetFileName(p).Equals(explicitPath, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return (match, IsSolution(match) ? "solution" : "project");

                return (null, "none");
            }

            // Auto-detect: first look for .sln/.slnx in input directories
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var slnFiles = Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly))
                    .ToArray();
                if (slnFiles.Length > 0)
                    return (slnFiles[0], "solution");
            }

            // Check parent directories for .sln/.slnx (common layout: solution root contains .sln, subdirs contain .csproj)
            var checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in _inputDirectories)
            {
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent != null && checkedParents.Add(parent) && Directory.Exists(parent))
                {
                    var slnFiles = Directory.GetFiles(parent, "*.sln", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(parent, "*.slnx", SearchOption.TopDirectoryOnly))
                        .ToArray();
                    if (slnFiles.Length > 0)
                        return (slnFiles[0], "solution");
                }
            }

            // Then look for .csproj files in input directories
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
                if (csprojFiles.Length > 0)
                    return (csprojFiles[0], "project");
            }

            // Search one level deep
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);
                    if (csprojFiles.Length > 0)
                        return (csprojFiles[0], "project");
                }
                catch { }
            }

            return (null, "none");
        }

        /// <summary>
        /// Discovers all .sln and .csproj files in and around the workspace directories.
        /// </summary>
        public List<string> GetAllProjectFiles()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;

                // Search within input directory
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

                // Search parent directory for .sln/.slnx
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

            return results.OrderBy(f => f).ToList();
        }

        private (List<(string display, string? fullPath, int line)> errors, List<string> warnings) ParseBuildOutput(string output)
        {
            var errors = new List<(string display, string? fullPath, int line)>();
            var warnings = new List<string>();

            // MSBuild/dotnet build output patterns:
            // file.cs(line,col): error CS1234: message
            // file.cs(line,col): warning CS1234: message
            var errorPattern = new Regex(@"^(.+?)\((\d+),(\d+)\):\s*error\s+(\w+):\s*(.+)$", RegexOptions.Multiline);
            var warningPattern = new Regex(@"^(.+?)\((\d+),(\d+)\):\s*warning\s+(\w+):\s*(.+)$", RegexOptions.Multiline);

            foreach (Match match in errorPattern.Matches(output))
            {
                var fullPath = match.Groups[1].Value.Trim();
                var lineNum = int.TryParse(match.Groups[2].Value, out var l) ? l : 0;
                var file = Path.GetFileName(fullPath);
                var code = match.Groups[4].Value;
                var message = match.Groups[5].Value;
                var resolvedPath = File.Exists(fullPath) ? fullPath : null;
                errors.Add(($"`{file}:{lineNum}` [{code}] {message}", resolvedPath, lineNum));
            }

            foreach (Match match in warningPattern.Matches(output))
            {
                var file = Path.GetFileName(match.Groups[1].Value);
                var line = match.Groups[2].Value;
                var code = match.Groups[4].Value;
                var message = match.Groups[5].Value;
                warnings.Add($"`{file}:{line}` [{code}] {message}");
            }

            // Also catch general error lines (like MSBuild errors)
            var generalErrorPattern = new Regex(@"^\s*error\s+(\w+):\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match match in generalErrorPattern.Matches(output))
            {
                var code = match.Groups[1].Value;
                var message = match.Groups[2].Value;
                var errorText = $"[{code}] {message}";
                if (!errors.Any(e => e.display.Contains(message)))
                    errors.Add((errorText, null, 0));
            }

            return (errors, warnings);
        }
    }
}
