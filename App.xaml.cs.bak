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
        private const string HandshakePipeName = "codemerger_handshake";

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
                    // Store flag so MainWindow can show message
                    Application.Current.Properties["ConfigHealed"] = true;
                }
            }
            catch
            {
                // Don't crash on heal failure - not critical
            }

            // Check for MCP mode (no project name needed - reads from active_project.txt)
            if (e.Args.Length >= 1 && e.Args[0] == "--mcp")
            {
                RunMcpMode();
                return;
            }

            // Normal GUI mode - let XAML handle it via StartupUri
        }

        private void RunMcpMode()
        {
            try
            {
                var projectService = new ProjectService();
                var mcpServer = new McpServer();

                // Get active project from settings
                var projectName = projectService.GetActiveProject();

                if (string.IsNullOrEmpty(projectName))
                {
                    Console.Error.WriteLine("[MCP] No active project set. Please select a project in CodeMerger GUI first.");
                    Environment.Exit(1);
                    return;
                }

                // Load project
                var project = projectService.LoadProject(projectName);

                if (project == null)
                {
                    Console.Error.WriteLine($"[MCP] Project not found: {projectName}");
                    Environment.Exit(1);
                    return;
                }

                // Get all files
                var extensions = project.Extensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToList();

                var ignoredDirsInput = project.IgnoredDirectories + ",.git";
                var ignoredDirNames = ignoredDirsInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant())
                    .ToHashSet();

                var allFiles = project.InputDirectories
                    .Where(Directory.Exists)
                    .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    .Where(file =>
                    {
                        var pathParts = file.Split(Path.DirectorySeparatorChar);
                        if (pathParts.Any(part => ignoredDirNames.Contains(part.ToLowerInvariant())))
                            return false;

                        var fileExtension = Path.GetExtension(file);
                        if (extensions.Count == 0 || extensions.Contains("*.*")) return true;
                        return extensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
                    })
                    .Distinct()
                    .ToList();

                // Index project
                mcpServer.IndexProject(project.Name, project.InputDirectories, allFiles);

                // Notify MainWindow if it's running (handshake)
                SendHandshakeToMainWindow(projectName);

                // Run MCP server on stdio
                Console.Error.WriteLine($"[MCP] Starting server for project: {project.Name}");
                Console.Error.WriteLine($"[MCP] Indexed {allFiles.Count} files");

                using var inputStream = Console.OpenStandardInput();
                using var outputStream = Console.OpenStandardOutput();

                mcpServer.StartAsync(inputStream, outputStream).GetAwaiter().GetResult();

                // Keep running until input stream closes
                while (true)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP] Error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void SendHandshakeToMainWindow(string projectName)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", HandshakePipeName, PipeDirection.Out);
                pipe.Connect(500); // 500ms timeout

                using var writer = new StreamWriter(pipe);
                writer.WriteLine(projectName);
                writer.Flush();
            }
            catch
            {
                // MainWindow not running or not listening - that's OK
            }
        }
    }
}
