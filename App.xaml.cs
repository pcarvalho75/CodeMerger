using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Windows;
using CodeMerger.Services;

namespace CodeMerger
{
    public partial class App : Application
    {
        public const string HandshakePipeName = "codemerger_handshake";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Check for MCP mode first (before killing anything)
            if (e.Args.Length >= 1 && e.Args[0] == "--mcp")
            {
                RunMcpMode();
                return;
            }

            // GUI mode: kill any previous GUI instances holding pipes
            KillOtherGuiInstances();

            // Update Claude Desktop config to point to the currently running exe
            try
            {
                var claudeService = new ClaudeDesktopService();
                bool updated = claudeService.SelfHeal();
                if (updated)
                {
                    Application.Current.Properties["ConfigHealed"] = true;
                }
            }
            catch
            {
                // Don't crash on heal failure
            }
        }

        /// <summary>
        /// Kills other CodeMerger GUI instances (non-MCP) to free named pipes.
        /// MCP instances (with --mcp arg) are left alone.
        /// </summary>
        private void KillOtherGuiInstances()
        {
            var currentPid = Environment.ProcessId;
            var currentName = Process.GetCurrentProcess().ProcessName;

            foreach (var proc in Process.GetProcessesByName(currentName))
            {
                if (proc.Id == currentPid) continue;

                try
                {
                    // Check if this is an MCP instance by looking at command line
                    // If we can't determine, kill it anyway - MCP will be restarted by Claude
                    var cmdLine = GetCommandLine(proc);
                    if (cmdLine != null && cmdLine.Contains("--mcp"))
                        continue; // Leave MCP instances alone

                    proc.Kill();
                    proc.WaitForExit(2000);
                }
                catch
                {
                    // Process may have already exited
                }
            }

            // Brief pause to let OS release pipe handles
            Thread.Sleep(200);
        }

        private static string? GetCommandLine(Process process)
        {
            try
            {
                // Use WMI to get command line - only reliable way on Windows
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                foreach (var obj in searcher.Get())
                {
                    return obj["CommandLine"]?.ToString();
                }
            }
            catch
            {
                // WMI not available or access denied
            }
            return null;
        }

        private void RunMcpMode()
        {
            try
            {
                var workspaceService = new WorkspaceService();
                var mcpServer = new McpServer();

                var workspaceName = workspaceService.GetActiveWorkspace();

                if (string.IsNullOrEmpty(workspaceName))
                {
                    Console.Error.WriteLine("[MCP] No active workspace set. Please select a workspace in CodeMerger GUI first.");
                    Environment.Exit(1);
                    return;
                }

                var workspace = workspaceService.LoadWorkspace(workspaceName);

                if (workspace == null)
                {
                    Console.Error.WriteLine($"[MCP] Workspace not found: {workspaceName}");
                    Environment.Exit(1);
                    return;
                }

                var extensions = workspace.Extensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToList();

                var ignoredDirsInput = workspace.IgnoredDirectories + ",.git";
                var ignoredDirNames = ignoredDirsInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant())
                    .ToHashSet();

                // Filter out disabled directories
                var activeDirectories = workspace.InputDirectories
                    .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                    .ToList();

                // Pass filter settings - McpServer will scan files itself
                mcpServer.IndexWorkspace(workspace.Name, activeDirectories, extensions, ignoredDirNames);

                SendHandshakeToMainWindow(workspaceName);

                Console.Error.WriteLine($"[MCP] Starting server for workspace: {workspace.Name}");

                using var inputStream = Console.OpenStandardInput();
                using var outputStream = Console.OpenStandardOutput();

                // StartAsync now properly awaits until stdin closes or parent dies
                mcpServer.StartAsync(inputStream, outputStream).GetAwaiter().GetResult();

                // Server has stopped - exit cleanly
                Console.Error.WriteLine("[MCP] Server shutdown complete");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP] Error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void SendHandshakeToMainWindow(string workspaceName)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", HandshakePipeName, PipeDirection.Out);
                pipe.Connect(500);

                using var writer = new StreamWriter(pipe);
                writer.WriteLine(workspaceName);
                writer.Flush();
            }
            catch
            {
                // MainWindow not running - that's OK
            }
        }
    }
}
