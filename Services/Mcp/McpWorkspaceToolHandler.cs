using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        private readonly string _workspaceName;
        private readonly List<string> _inputDirectories;
        private readonly Action _requestReindex;
        private readonly Action _requestShutdown;
        private readonly Action<string> _sendActivity;
        private readonly Action<string> _log;

        public McpWorkspaceToolHandler(
            WorkspaceService workspaceService,
            string workspaceName,
            List<string> inputDirectories,
            Action requestReindex,
            Action requestShutdown,
            Action<string> sendActivity,
            Action<string> log)
        {
            _workspaceService = workspaceService;
            _workspaceName = workspaceName;
            _inputDirectories = inputDirectories;
            _requestReindex = requestReindex;
            _requestShutdown = requestShutdown;
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

            sb.AppendLine();
            sb.AppendLine("*Use `codemerger_switch_project` to switch to a different workspace.*");

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

            _workspaceService.SetActiveWorkspace(workspaceName);

            // Schedule restart
            Task.Run(async () =>
            {
                await Task.Delay(500);
                _requestShutdown();
            });

            return $"# Switching Workspace\n\nSwitching to workspace **{workspaceName}**.\n\nThe server will restart automatically. Please use any CodeMerger tool to reconnect with the new workspace loaded.";
        }
    }
}
