using System;
using System.IO;
using System.Linq;
using System.Windows;
using CodeMerger.Services;

namespace CodeMerger
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Check for MCP mode
            if (e.Args.Length >= 2 && e.Args[0] == "--mcp")
            {
                RunMcpMode(e.Args[1]);
                return;
            }

            // Normal GUI mode - let XAML handle it via StartupUri
        }

        private void RunMcpMode(string projectName)
        {
            try
            {
                var projectService = new ProjectService();
                var codeAnalyzer = new CodeAnalyzer();
                var mcpServer = new McpServer();

                // Load project
                var projects = projectService.LoadAllProjects();
                var project = projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

                if (project == null)
                {
                    Console.Error.WriteLine($"Project not found: {projectName}");
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
    }
}
