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
                var projectService = new ProjectService();
                var mcpServer = new McpServer();

                var projectName = projectService.GetActiveProject();

                if (string.IsNullOrEmpty(projectName))
                {
                    Console.Error.WriteLine("[MCP] No active project set. Please select a project in CodeMerger GUI first.");
                    Environment.Exit(1);
                    return;
                }

                var project = projectService.LoadProject(projectName);

                if (project == null)
                {
                    Console.Error.WriteLine($"[MCP] Project not found: {projectName}");
                    Environment.Exit(1);
                    return;
                }

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

                mcpServer.IndexProject(project.Name, project.InputDirectories, allFiles);

                SendHandshakeToMainWindow(projectName);

                Console.Error.WriteLine($"[MCP] Starting server for project: {project.Name}");
                Console.Error.WriteLine($"[MCP] Indexed {allFiles.Count} files");

                using var inputStream = Console.OpenStandardInput();
                using var outputStream = Console.OpenStandardOutput();

                mcpServer.StartAsync(inputStream, outputStream).GetAwaiter().GetResult();

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
                pipe.Connect(500);

                using var writer = new StreamWriter(pipe);
                writer.WriteLine(projectName);
                writer.Flush();
            }
            catch
            {
                // MainWindow not running - that's OK
            }
        }
    }
}
