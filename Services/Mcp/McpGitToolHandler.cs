using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles git operations for the MCP server.
    /// </summary>
    public class McpGitToolHandler
    {
        private readonly string _workspacePath;
        private readonly Action<string> _sendActivity;

        public McpGitToolHandler(string workspacePath, Action<string> sendActivity)
        {
            _workspacePath = workspacePath;
            _sendActivity = sendActivity;
        }

        /// <summary>
        /// Routes a git command to the appropriate handler method.
        /// </summary>
        public string HandleCommand(string action, JsonElement arguments)
        {
            switch (action)
            {
                case "status":
                    return GetStatus();

                case "commit":
                    var commitMsg = arguments.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                    if (string.IsNullOrEmpty(commitMsg))
                        return "Error: 'message' parameter is required.";
                    var shouldPush = arguments.TryGetProperty("push", out var pushEl) && pushEl.GetBoolean();
                    return shouldPush ? CommitAndPush(commitMsg) : Commit(commitMsg);

                case "push":
                    return Push();

                default:
                    return "Error: Unknown git action. Use: status, commit, push";
            }
        }

        public string GetStatus()
        {
            if (!IsGitRepo())
                return "Error: Not a git repository.";

            _sendActivity("Checking git status...");

            var status = RunGit("status --porcelain");
            var branch = RunGit("branch --show-current").Trim();

            if (string.IsNullOrWhiteSpace(status))
                return $"# Git Status\n\n**Branch:** `{branch}`\n\n✅ Working tree clean - nothing to commit.";

            var sb = new StringBuilder();
            sb.AppendLine("# Git Status");
            sb.AppendLine();
            sb.AppendLine($"**Branch:** `{branch}`");
            sb.AppendLine();
            sb.AppendLine("**Changes:**");
            sb.AppendLine("```");
            sb.AppendLine(status.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("*Legend: M=modified, A=added, D=deleted, ??=untracked*");

            return sb.ToString();
        }

        public string Commit(string message)
        {
            if (!IsGitRepo())
                return "Error: Not a git repository.";

            if (string.IsNullOrWhiteSpace(message))
                return "Error: Commit message is required.";

            _sendActivity("Committing changes...");

            // Stage all changes
            RunGit("add -A");
            
            // Check if there's anything to commit
            var status = RunGit("status --porcelain");
            if (string.IsNullOrWhiteSpace(status))
                return "Nothing to commit - working tree clean.";

            // Commit
            var commitResult = RunGit($"commit -m \"{EscapeMessage(message)}\"");

            if (commitResult.Contains("nothing to commit"))
                return "Nothing to commit - working tree clean.";

            var sb = new StringBuilder();
            sb.AppendLine("# Git Commit");
            sb.AppendLine();
            sb.AppendLine($"**Message:** {message}");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(commitResult.Trim());
            sb.AppendLine("```");

            return sb.ToString();
        }

        public string Push()
        {
            if (!IsGitRepo())
                return "Error: Not a git repository.";

            _sendActivity("Pushing to remote...");

            var result = RunGit("push");
            var branch = RunGit("branch --show-current").Trim();

            if (result.Contains("error:") || result.Contains("fatal:"))
                return $"# Git Push Failed\n\n```\n{result.Trim()}\n```";

            var sb = new StringBuilder();
            sb.AppendLine("# Git Push");
            sb.AppendLine();
            sb.AppendLine($"**Branch:** `{branch}`");
            sb.AppendLine();
            if (string.IsNullOrWhiteSpace(result) || result.Contains("Everything up-to-date"))
            {
                sb.AppendLine("✅ Already up to date.");
            }
            else
            {
                sb.AppendLine("✅ Pushed successfully.");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(result.Trim());
                sb.AppendLine("```");
            }

            return sb.ToString();
        }

        public string CommitAndPush(string message)
        {
            if (!IsGitRepo())
                return "Error: Not a git repository.";

            if (string.IsNullOrWhiteSpace(message))
                return "Error: Commit message is required.";

            var sb = new StringBuilder();

            // Stage all
            _sendActivity("Staging changes...");
            RunGit("add -A");

            // Check status
            var status = RunGit("status --porcelain");
            if (string.IsNullOrWhiteSpace(status))
                return "Nothing to commit - working tree clean.";

            // Commit
            _sendActivity("Committing...");
            var commitResult = RunGit($"commit -m \"{EscapeMessage(message)}\"");

            sb.AppendLine("# Git Commit & Push");
            sb.AppendLine();
            sb.AppendLine($"**Message:** {message}");
            sb.AppendLine();

            if (commitResult.Contains("nothing to commit"))
            {
                sb.AppendLine("Nothing to commit - working tree clean.");
                return sb.ToString();
            }

            sb.AppendLine("**Commit:**");
            sb.AppendLine("```");
            sb.AppendLine(commitResult.Trim());
            sb.AppendLine("```");
            sb.AppendLine();

            // Push
            _sendActivity("Pushing...");
            var pushResult = RunGit("push");

            if (pushResult.Contains("error:") || pushResult.Contains("fatal:"))
            {
                sb.AppendLine("**Push failed:**");
                sb.AppendLine("```");
                sb.AppendLine(pushResult.Trim());
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine("✅ Pushed successfully.");
            }

            return sb.ToString();
        }

        private bool IsGitRepo()
        {
            var gitDir = Path.Combine(_workspacePath, ".git");
            return Directory.Exists(gitDir);
        }

        private string RunGit(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = _workspacePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return "Error: Failed to start git process.";

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(30000);

                return string.IsNullOrEmpty(output) ? error : output;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string EscapeMessage(string message)
        {
            return message.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }
    }
}
