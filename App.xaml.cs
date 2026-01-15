using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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

            // Self-heal Claude Desktop config if needed
            try
            {
                var claudeService = new ClaudeDesktopService();
                bool healed = claudeService.SelfHeal();
                if (healed)
                {
                    Application.Current.Properties["ConfigHealed"] = true;
                }
            }
            catch
            {
                // Don't crash on heal failure
            }

            // Check for MCP mode
            if (e.Args.Length >= 1 && e.Args[0] == "--mcp")
            {
                RunMcpMode();
                return;
            }
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
