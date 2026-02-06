using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using CodeMerger.Services;

namespace CodeMerger
{
    public partial class App : Application
    {
        public const string HandshakePipeName = "codemerger_handshake";

        private static Mutex? _singleInstanceMutex;

        /// <summary>
        /// Custom window message used to bring the existing GUI instance to the foreground.
        /// MainWindow registers a HwndSource hook to handle this message.
        /// </summary>
        public static readonly int WM_SHOWME = RegisterWindowMessage("CodeMerger_ShowMe");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int HWND_BROADCAST = 0xFFFF;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // MCP mode: allow multiple instances (Claude Desktop spawns these)
            if (e.Args.Length >= 1 && e.Args[0] == "--mcp")
            {
                RunMcpMode();
                return;
            }

            // GUI mode: enforce single instance via Mutex
            _singleInstanceMutex = new Mutex(true, @"Global\CodeMerger_SingleInstance", out bool createdNew);

            if (!createdNew)
            {
                // Another GUI instance is already running — bring it to front and exit
                PostMessage((IntPtr)HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }

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

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
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
