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
        private readonly CompilationService? _compilationService;

        public McpWorkspaceToolHandler(
            WorkspaceService workspaceService,
            string workspaceName,
            List<string> inputDirectories,
            Action requestReindex,
            Action requestShutdown,
            Func<string, bool> requestSwitchWorkspace,
            Action<string> sendActivity,
            Action<string> log,
            CompilationService? compilationService = null)
        {
            _workspaceService = workspaceService;
            _workspaceName = workspaceName;
            _inputDirectories = inputDirectories;
            _requestReindex = requestReindex;
            _requestShutdown = requestShutdown;
            _requestSwitchWorkspace = requestSwitchWorkspace;
            _sendActivity = sendActivity;
            _log = log;
            _compilationService = compilationService;
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

            bool autoHeal = false;
            if (arguments.TryGetProperty("autoHeal", out var healEl))
                autoHeal = healEl.GetBoolean();

            int maxHealAttempts = 3;
            if (arguments.TryGetProperty("maxHealAttempts", out var attemptsEl))
                maxHealAttempts = attemptsEl.GetInt32();

            bool healPreview = false;
            if (arguments.TryGetProperty("healPreview", out var previewEl))
                healPreview = previewEl.GetBoolean();

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
                    return "Error: No build target found (.csproj, .sln, DESCRIPTION, .Rproj, or .R files) in the workspace directories.";
                }

                sb.AppendLine($"**Project:** `{Path.GetFileName(projectFile)}`");

                // Route R projects to dedicated handler
                if (projectType.StartsWith("r-", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildR(sb, projectFile, projectType);
                }

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

                // ── Auto-Heal Logic ──────────────────────────────────────
                if ((autoHeal || healPreview) && process.ExitCode != 0 && errors.Count > 0)
                {
                    var healer = new BuildHealer(_compilationService, _log);

                    // Convert parsed errors to BuildError objects
                    var buildErrors = ConvertToBuildErrors(fullOutput);

                    if (healPreview)
                    {
                        // Preview mode: show what would be fixed without applying
                        var iteration = healer.HealIteration(buildErrors);
                        sb.AppendLine("---");
                        sb.AppendLine("## Auto-Heal Preview");
                        sb.AppendLine();

                        if (iteration.Fixes.Count > 0)
                        {
                            sb.AppendLine($"**{iteration.Fixes.Count} fixable error(s) detected:**");
                            sb.AppendLine();
                            foreach (var fix in iteration.Fixes)
                            {
                                sb.AppendLine($"- {fix.Description} (confidence: {fix.Confidence:P0})");
                                sb.AppendLine($"  File: `{Path.GetFileName(fix.FilePath)}`");
                            }
                        }
                        else
                        {
                            sb.AppendLine("No auto-fixable errors detected.");
                        }

                        if (iteration.Unfixable.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"**{iteration.Unfixable.Count} error(s) require manual attention:**");
                            foreach (var u in iteration.Unfixable.Take(10))
                            {
                                sb.AppendLine($"- [{u.Error.ErrorCode}] {u.Category}: {u.Error.Message}");
                            }
                        }

                        sb.AppendLine();
                        sb.AppendLine("*Run with `autoHeal: true` to apply these fixes and rebuild.*");
                    }
                    else if (autoHeal)
                    {
                        sb.AppendLine("---");
                        sb.AppendLine("## Auto-Heal");
                        sb.AppendLine();

                        var healSw = Stopwatch.StartNew();
                        int totalFixed = 0;
                        var currentErrors = buildErrors;
                        var allAppliedFixes = new List<HealSuggestion>();

                        for (int attempt = 1; attempt <= maxHealAttempts; attempt++)
                        {
                            sb.AppendLine($"### Attempt {attempt}");

                            var iteration = healer.HealIteration(currentErrors);

                            if (iteration.Fixes.Count == 0)
                            {
                                sb.AppendLine("No more auto-fixable errors. Stopping.");
                                sb.AppendLine();
                                break;
                            }

                            // Apply fixes (backups are created automatically by ApplyFix)
                            int applied = 0;
                            foreach (var fix in iteration.Fixes)
                            {
                                if (healer.ApplyFix(fix))
                                {
                                    sb.AppendLine($"- Fixed: {fix.Description}");
                                    allAppliedFixes.Add(fix);
                                    applied++;
                                }
                                else
                                {
                                    sb.AppendLine($"- Failed: {fix.Description}");
                                }
                            }
                            totalFixed += applied;
                            sb.AppendLine();

                            if (applied == 0)
                            {
                                sb.AppendLine("No fixes could be applied. Stopping.");
                                break;
                            }

                            // Rebuild
                            sb.AppendLine("Rebuilding...");
                            var rebuildResult = RunBuildProcess(projectFile!, configuration);

                            if (rebuildResult.exitCode == 0)
                            {
                                // Success — clean up heal backups
                                healer.CleanupHealBackups(allAppliedFixes);
                                healSw.Stop();
                                sb.AppendLine();
                                sb.AppendLine($"## Build Succeeded after {attempt} heal cycle(s)");
                                sb.AppendLine($"**Total fixes applied:** {totalFixed}");
                                sb.AppendLine($"\n*Auto-heal completed in {healSw.ElapsedMilliseconds}ms.*");
                                sb.AppendLine();
                                return sb.ToString();
                            }

                            // Parse new errors for next cycle
                            var (newErrors, newWarnings) = ParseBuildOutput(rebuildResult.output);
                            currentErrors = ConvertToBuildErrors(rebuildResult.output);
                            sb.AppendLine($"Rebuild: {newErrors.Count} errors remaining");
                            sb.AppendLine();

                            // Safety: if error count increased, rollback ALL changes and stop
                            if (newErrors.Count > errors.Count)
                            {
                                sb.AppendLine("Error count increased — rolling back all heal changes.");
                                int rolledBack = healer.RollbackFixes(allAppliedFixes);
                                healer.CleanupHealBackups(allAppliedFixes);
                                sb.AppendLine($"Rolled back {rolledBack} file(s) to pre-heal state.");
                                sb.AppendLine();
                                break;
                            }
                        }

                        // Clean up any remaining heal backups
                        healer.CleanupHealBackups(allAppliedFixes);

                        healSw.Stop();

                        if (currentErrors.Count > 0)
                        {
                            sb.AppendLine($"**{currentErrors.Count} error(s) remain after auto-heal.** Manual fixes needed.");
                        }
                        sb.AppendLine($"\n*Auto-heal completed in {healSw.ElapsedMilliseconds}ms.*");
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error running build: {ex.Message}\n\nMake sure `dotnet` is installed and available in PATH.";
            }
        }

        /// <summary>
        /// Runs R CMD check (packages), lintr (projects/scripts), or both depending on project type.
        /// Returns formatted markdown results matching the C# build output style.
        /// </summary>
        private string BuildR(StringBuilder sb, string projectFile, string projectType)
        {
            var workingDir = projectType == "r-package"
                ? Path.GetDirectoryName(projectFile)!
                : Path.GetDirectoryName(projectFile) ?? _inputDirectories.FirstOrDefault() ?? ".";

            // For r-script type, projectFile is the directory itself
            if (projectType == "r-script")
                workingDir = projectFile;

            sb.AppendLine($"**Type:** {projectType}");
            sb.AppendLine();

            bool hasErrors = false;
            var allErrors = new List<(string display, string? fullPath, int line)>();
            var allWarnings = new List<string>();
            var allNotes = new List<string>();

            // R CMD check for packages
            if (projectType == "r-package")
            {
                sb.AppendLine("## R CMD check");
                sb.AppendLine();

                var packageDir = workingDir;
                // R CMD check expects to run from the parent of the package dir
                var parentDir = Directory.GetParent(packageDir)?.FullName ?? packageDir;

                var (exitCode, output) = RunProcess("Rscript", "--vanilla -e \"devtools::check(error_on = 'never')\"", packageDir);

                if (exitCode == -99)
                {
                    sb.AppendLine("**Rscript not found.** Make sure R is installed and `Rscript` is in PATH.");
                    return sb.ToString();
                }

                var (checkErrors, checkWarnings, checkNotes) = ParseRCheckOutput(output);
                allErrors.AddRange(checkErrors);
                allWarnings.AddRange(checkWarnings);
                allNotes.AddRange(checkNotes);

                if (exitCode != 0 && checkErrors.Count == 0)
                    allErrors.Add(($"R CMD check exited with code {exitCode}", null, 0));
            }

            // lintr for all R project types
            bool hasLintr = CheckRPackageInstalled("lintr");

            if (hasLintr)
            {
                sb.AppendLine("## lintr Analysis");
                sb.AppendLine();

                // Build the lint command based on project type
                string lintCommand;
                if (projectType == "r-package")
                {
                    lintCommand = $"--vanilla -e \"results <- lintr::lint_package('{EscapeRString(workingDir)}'); for(r in results) cat(sprintf('%s:%d:%d: %s: %s\\n', r$filename, r$line_number, r$column_number, r$type, r$message))\"";
                }
                else
                {
                    // For r-project and r-script, lint the directory
                    lintCommand = $"--vanilla -e \"results <- lintr::lint_dir('{EscapeRString(workingDir)}'); for(r in results) cat(sprintf('%s:%d:%d: %s: %s\\n', r$filename, r$line_number, r$column_number, r$type, r$message))\"";
                }

                var (lintExit, lintOutput) = RunProcess("Rscript", lintCommand, workingDir);

                if (lintExit == -99)
                {
                    sb.AppendLine("**Rscript not found.** Make sure R is installed and `Rscript` is in PATH.");
                }
                else
                {
                    var (lintErrors, lintWarnings, lintStyle) = ParseLintrOutput(lintOutput, workingDir);
                    allErrors.AddRange(lintErrors);
                    allWarnings.AddRange(lintWarnings);
                    allNotes.AddRange(lintStyle);
                }
            }
            else
            {
                sb.AppendLine("*lintr package not installed. Install with: `install.packages(\"lintr\")`*");
                sb.AppendLine();

                // Fallback: at minimum do a syntax check on all .R files
                sb.AppendLine("## Syntax Check");
                sb.AppendLine();

                var rFiles = Directory.EnumerateFiles(workingDir, "*.R", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "renv" + Path.DirectorySeparatorChar))
                    .ToList();

                foreach (var rFile in rFiles)
                {
                    var escaped = EscapeRString(rFile);
                    var (synExit, synOutput) = RunProcess("Rscript", $"--vanilla -e \"tryCatch(parse('{escaped}'), error=function(e) cat('ERROR:', conditionMessage(e), '\\n'))\"", workingDir);

                    if (synOutput.Contains("ERROR:"))
                    {
                        var msg = synOutput.Replace("ERROR:", "").Trim();
                        allErrors.Add(($"`{Path.GetFileName(rFile)}` {msg}", rFile, 0));
                    }
                }
            }

            // Format results
            hasErrors = allErrors.Count > 0;

            if (!hasErrors && allWarnings.Count == 0 && allNotes.Count == 0)
            {
                sb.AppendLine("## ✅ All Checks Passed");
            }
            else if (hasErrors)
            {
                sb.AppendLine("## ❌ Issues Found");
            }
            else
            {
                sb.AppendLine("## ✅ No Errors (warnings/notes below)");
            }
            sb.AppendLine();

            sb.AppendLine($"**Errors:** {allErrors.Count}");
            sb.AppendLine($"**Warnings:** {allWarnings.Count}");
            sb.AppendLine($"**Notes/Style:** {allNotes.Count}");
            sb.AppendLine();

            if (allErrors.Count > 0)
            {
                sb.AppendLine("## Errors");
                foreach (var error in allErrors.Take(50))
                {
                    sb.AppendLine($"- ❌ {error.display}");
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
                        catch { }
                    }
                }
                sb.AppendLine();
            }

            if (allWarnings.Count > 0)
            {
                sb.AppendLine("## Warnings");
                foreach (var warning in allWarnings.Take(30))
                    sb.AppendLine($"- ⚠️ {warning}");
                if (allWarnings.Count > 30)
                    sb.AppendLine($"- ... and {allWarnings.Count - 30} more warnings");
                sb.AppendLine();
            }

            if (allNotes.Count > 0)
            {
                sb.AppendLine("## Notes/Style");
                foreach (var note in allNotes.Take(30))
                    sb.AppendLine($"- 💡 {note}");
                if (allNotes.Count > 30)
                    sb.AppendLine($"- ... and {allNotes.Count - 30} more notes");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses lintr output format: file.R:line:col: type: message
        /// Splits into errors, warnings, and style notes.
        /// </summary>
        private (List<(string display, string? fullPath, int line)> errors, List<string> warnings, List<string> style)
            ParseLintrOutput(string output, string workingDir)
        {
            var errors = new List<(string display, string? fullPath, int line)>();
            var warnings = new List<string>();
            var style = new List<string>();

            // lintr format: filename:line:col: type: message
            var pattern = new Regex(@"^(.+?):(\d+):(\d+):\s*(error|warning|style):\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match match in pattern.Matches(output))
            {
                var file = match.Groups[1].Value.Trim();
                var line = int.TryParse(match.Groups[2].Value, out var l) ? l : 0;
                var type = match.Groups[4].Value.ToLowerInvariant();
                var message = match.Groups[5].Value.Trim();
                var fileName = Path.GetFileName(file);

                // Resolve full path
                string? fullPath = null;
                if (File.Exists(file))
                    fullPath = file;
                else
                {
                    var resolved = Path.Combine(workingDir, file);
                    if (File.Exists(resolved))
                        fullPath = resolved;
                }

                var display = $"`{fileName}:{line}` {message}";

                switch (type)
                {
                    case "error":
                        errors.Add((display, fullPath, line));
                        break;
                    case "warning":
                        warnings.Add(display);
                        break;
                    default:
                        style.Add(display);
                        break;
                }
            }

            return (errors, warnings, style);
        }

        /// <summary>
        /// Parses R CMD check / devtools::check output for ERRORs, WARNINGs, and NOTEs.
        /// </summary>
        private (List<(string display, string? fullPath, int line)> errors, List<string> warnings, List<string> notes)
            ParseRCheckOutput(string output)
        {
            var errors = new List<(string display, string? fullPath, int line)>();
            var warnings = new List<string>();
            var notes = new List<string>();

            // R CMD check uses sections like:
            // * checking ...
            // ...checking ... ERROR
            // ...checking ... WARNING
            // ...checking ... NOTE
            var lines = output.Split('\n');
            string currentSection = "";

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (line.StartsWith("*") || line.StartsWith(">"))
                    currentSection = line;

                if (line.EndsWith("ERROR") || line.Contains("Error in") || line.Contains("Error:"))
                {
                    errors.Add((line, null, 0));
                }
                else if (line.EndsWith("WARNING"))
                {
                    warnings.Add(line);
                }
                else if (line.EndsWith("NOTE"))
                {
                    notes.Add(line);
                }

                // Also catch inline errors like: file.R:10:5: error message
                if (Regex.IsMatch(line, @"^\s*.+\.R:\d+:\d+:"))
                {
                    var match = Regex.Match(line, @"^(.+\.R):(\d+):(\d+):\s*(.+)$");
                    if (match.Success)
                    {
                        var file = match.Groups[1].Value.Trim();
                        var lineNum = int.TryParse(match.Groups[2].Value, out var ln) ? ln : 0;
                        var msg = match.Groups[4].Value.Trim();
                        var fullPath = File.Exists(file) ? file : null;
                        errors.Add(($"`{Path.GetFileName(file)}:{lineNum}` {msg}", fullPath, lineNum));
                    }
                }
            }

            return (errors, warnings, notes);
        }

        /// <summary>
        /// Checks if an R package is installed.
        /// </summary>
        private bool CheckRPackageInstalled(string packageName)
        {
            var (exitCode, output) = RunProcess("Rscript", $"--vanilla -e \"cat(requireNamespace('{packageName}', quietly=TRUE))\"", ".");
            return exitCode == 0 && output.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs an external process and returns exit code + combined output.
        /// Returns exitCode -99 if the executable is not found.
        /// </summary>
        private (int exitCode, string output) RunProcess(string fileName, string arguments, string workingDirectory)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                var output = new StringBuilder();
                var errorOutput = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool completed = process.WaitForExit(120000);
                if (!completed)
                {
                    process.Kill();
                    return (-1, "Process timed out after 2 minutes");
                }

                return (process.ExitCode, output.ToString() + errorOutput.ToString());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (-99, $"{fileName} not found");
            }
            catch (Exception ex)
            {
                return (-1, $"Process error: {ex.Message}");
            }
        }

        /// <summary>
        /// Escapes backslashes in file paths for use inside R strings.
        /// </summary>
        private static string EscapeRString(string path)
        {
            return path.Replace("\\", "/");
        }

        private (string? path, string type) FindProjectFile(string? explicitPath = null)
        {
            // If caller specified an explicit path, resolve and use it directly
            if (!string.IsNullOrEmpty(explicitPath))
            {
                string ClassifyFile(string p)
                {
                    if (p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                        return "solution";
                    if (Path.GetFileName(p).Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase))
                        return "r-package";
                    if (p.EndsWith(".Rproj", StringComparison.OrdinalIgnoreCase))
                        return "r-project";
                    return "project";
                }

                // Try as-is first (absolute path)
                if (File.Exists(explicitPath))
                    return (explicitPath, ClassifyFile(explicitPath));

                // Try resolving relative to each input directory
                foreach (var dir in _inputDirectories)
                {
                    var resolved = Path.GetFullPath(Path.Combine(dir, explicitPath));
                    if (File.Exists(resolved))
                        return (resolved, ClassifyFile(resolved));
                }

                // Try matching by filename across all known project files
                var allProjects = GetAllProjectFiles();
                var match = allProjects.FirstOrDefault(p =>
                    Path.GetFileName(p).Equals(explicitPath, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return (match, ClassifyFile(match));

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

            // R package: look for DESCRIPTION file (indicates an R package)
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var descFile = Path.Combine(dir, "DESCRIPTION");
                if (File.Exists(descFile))
                    return (descFile, "r-package");
                // Also check parent (common layout: package root is one level up)
                var parentDir = Directory.GetParent(dir)?.FullName;
                if (parentDir != null)
                {
                    descFile = Path.Combine(parentDir, "DESCRIPTION");
                    if (File.Exists(descFile))
                        return (descFile, "r-package");
                }
            }

            // R project: look for .Rproj files
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var rprojFiles = Directory.GetFiles(dir, "*.Rproj", SearchOption.TopDirectoryOnly);
                if (rprojFiles.Length > 0)
                    return (rprojFiles[0], "r-project");
            }

            // R scripts: look for any .R files as last resort
            foreach (var dir in _inputDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                var rFiles = Directory.GetFiles(dir, "*.R", SearchOption.TopDirectoryOnly);
                if (rFiles.Length > 0)
                    return (dir, "r-script");
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
                    // R project files
                    foreach (var f in Directory.GetFiles(dir, "*.Rproj", SearchOption.TopDirectoryOnly))
                        results.Add(f);
                    var descFile = Path.Combine(dir, "DESCRIPTION");
                    if (File.Exists(descFile))
                        results.Add(descFile);
                }
                catch { }

                // Search parent directory for .sln/.slnx and R package DESCRIPTION
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent != null && checkedParents.Add(parent) && Directory.Exists(parent))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(parent, "*.sln", SearchOption.TopDirectoryOnly))
                            results.Add(f);
                        foreach (var f in Directory.GetFiles(parent, "*.slnx", SearchOption.TopDirectoryOnly))
                            results.Add(f);
                        var parentDesc = Path.Combine(parent, "DESCRIPTION");
                        if (File.Exists(parentDesc))
                            results.Add(parentDesc);
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

        /// <summary>
        /// Convert raw build output into structured BuildError objects for the healer.
        /// </summary>
        private List<BuildError> ConvertToBuildErrors(string buildOutput)
        {
            var errors = new List<BuildError>();
            var errorPattern = new Regex(@"^(.+?)\((\d+),(\d+)\):\s*error\s+(\w+):\s*(.+)$", RegexOptions.Multiline);

            foreach (Match match in errorPattern.Matches(buildOutput))
            {
                var fullPath = match.Groups[1].Value.Trim();
                var line = int.TryParse(match.Groups[2].Value, out var l) ? l : 0;
                var col = int.TryParse(match.Groups[3].Value, out var c) ? c : 0;
                var code = match.Groups[4].Value;
                var message = match.Groups[5].Value;

                errors.Add(new BuildError
                {
                    FilePath = Path.GetFileName(fullPath),
                    FullPath = File.Exists(fullPath) ? fullPath : null,
                    Line = line,
                    Column = col,
                    ErrorCode = code,
                    Message = message,
                    Display = $"`{Path.GetFileName(fullPath)}:{line}` [{code}] {message}"
                });
            }

            return errors;
        }

        /// <summary>
        /// Run a dotnet build and return the exit code + combined output.
        /// Used by the auto-heal loop for rebuild cycles.
        /// </summary>
        private (int exitCode, string output) RunBuildProcess(string projectFile, string configuration)
        {
            try
            {
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

                using var process = new Process { StartInfo = startInfo };
                var output = new StringBuilder();
                var errorOutput = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool completed = process.WaitForExit(120000);
                if (!completed)
                {
                    process.Kill();
                    return (-1, "Build timed out");
                }

                return (process.ExitCode, output.ToString() + errorOutput.ToString());
            }
            catch (Exception ex)
            {
                return (-1, $"Build error: {ex.Message}");
            }
        }
    }
}