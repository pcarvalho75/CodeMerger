using System;
using System.Text;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles lesson-related MCP tools for self-improvement logging.
    /// </summary>
    public class McpLessonToolHandler
    {
        private readonly LessonService _lessonService;
        private readonly CommunityLessonSyncService _communitySyncService;
        private readonly Action<string> _sendActivity;

        public McpLessonToolHandler(LessonService lessonService, CommunityLessonSyncService communitySyncService, Action<string> sendActivity)
        {
            _lessonService = lessonService;
            _communitySyncService = communitySyncService;
            _sendActivity = sendActivity;
        }

        /// <summary>
        /// Routes a lesson command to the appropriate handler method.
        /// </summary>
        public string HandleCommand(string command, JsonElement arguments)
        {
            return command switch
            {
                "log" => LogLesson(arguments),
                "get" => GetLessons(),
                "delete" => DeleteLesson(arguments),
                "sync" => SyncLessons(),
                "submit" => SubmitLesson(arguments),
                _ => "Error: Unknown lesson command. Use: log, get, delete, sync, submit"
            };
        }

        /// <summary>
        /// Logs a new lesson observation.
        /// </summary>
        public string LogLesson(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("type", out var typeEl))
                return "Error: 'type' parameter is required.";

            if (!arguments.TryGetProperty("component", out var componentEl))
                return "Error: 'component' parameter is required.";

            if (!arguments.TryGetProperty("observation", out var observationEl))
                return "Error: 'observation' parameter is required.";

            if (!arguments.TryGetProperty("proposal", out var proposalEl))
                return "Error: 'proposal' parameter is required.";

            var type = typeEl.GetString() ?? "";
            var component = componentEl.GetString() ?? "";
            var observation = observationEl.GetString() ?? "";
            var proposal = proposalEl.GetString() ?? "";
            var suggestedCode = arguments.TryGetProperty("suggestedCode", out var codeEl) ? codeEl.GetString() : null;

            _sendActivity("Logging lesson");

            if (!_lessonService.CanLogMore())
            {
                var count = _lessonService.GetLessonCount();
                return $"# Lesson Not Logged\n\n" +
                       $"**Reason:** Local lesson storage is full ({count}/100).\n\n" +
                       $"Ask the user to review lessons with `lessons` (command `get`) and either apply improvements or clear with `lessons` (command `delete`).";
            }

            var success = _lessonService.LogLesson(type, component, observation, proposal, suggestedCode);

            if (!success)
            {
                return "# Lesson Not Logged\n\n**Reason:** Failed to save lesson.";
            }

            var newCount = _lessonService.GetLessonCount();
            var sb = new StringBuilder();
            sb.AppendLine("# Lesson Logged");
            sb.AppendLine();
            sb.AppendLine($"**Type:** {type}");
            sb.AppendLine($"**Component:** {component}");
            sb.AppendLine($"**Observation:** {observation}");
            sb.AppendLine();
            sb.AppendLine($"*Local lessons stored: {newCount}/100*");

            return sb.ToString();
        }

        /// <summary>
        /// Gets all logged lessons.
        /// </summary>
        public string GetLessons()
        {
            _sendActivity("Getting lessons");

            var lessons = _lessonService.GetLessons();

            if (lessons.Count == 0)
            {
                return "# Self-Improvement Lessons\n\n*No lessons logged yet.*\n\n" +
                       "Lessons are logged when Claude observes something that could be improved:\n" +
                       "- Tool descriptions that are unclear\n" +
                       "- Missing functionality\n" +
                       "- Workflow inefficiencies\n" +
                       "- Error handling improvements\n" +
                       "- New tool ideas";
            }

            var localCount = _lessonService.GetLessonCount();
            var communityCount = _lessonService.GetCommunityLessons().Count;

            var sb = new StringBuilder();
            sb.AppendLine("# Self-Improvement Lessons");
            sb.AppendLine();
            sb.AppendLine($"*Local: {localCount}/100 | Community: {communityCount}*");
            sb.AppendLine();

            foreach (var lesson in lessons)
            {
                var sourceTag = lesson.Source == LessonSource.Community ? " 🌐" : " 📌";
                sb.AppendLine($"---");
                sb.AppendLine($"## Lesson #{lesson.Number}{sourceTag}");
                sb.AppendLine();
                sb.AppendLine($"**Type:** {lesson.Type}");
                sb.AppendLine($"**Component:** {lesson.Component}");
                sb.AppendLine($"**Logged:** {lesson.Timestamp:yyyy-MM-dd HH:mm}");
                if (lesson.Source == LessonSource.Community && !string.IsNullOrEmpty(lesson.ContributedBy))
                    sb.AppendLine($"**Contributed by:** {lesson.ContributedBy}");
                sb.AppendLine();
                sb.AppendLine($"**Observation:** {lesson.Observation}");
                sb.AppendLine();
                sb.AppendLine($"**Proposal:** {lesson.Proposal}");

                if (!string.IsNullOrEmpty(lesson.SuggestedCode))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Suggested Code:**");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(lesson.SuggestedCode);
                    sb.AppendLine("```");
                }

                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*📌 = Local lesson (deletable) | 🌐 = Community lesson (read-only)*");

            return sb.ToString();
        }

        /// <summary>
        /// Deletes a specific lesson or all local lessons.
        /// Community lessons cannot be deleted.
        /// </summary>
        public string DeleteLesson(JsonElement arguments)
        {
            _sendActivity("Deleting lesson(s)");

            // Check if "all" parameter is true
            if (arguments.TryGetProperty("all", out var allEl) && allEl.GetBoolean())
            {
                var count = _lessonService.GetLessonCount();
                _lessonService.ClearAllLessons();
                return $"# Local Lessons Cleared\n\n**Deleted:** {count} local lesson(s)\n\n*Community lessons are not affected.*";
            }

            // Otherwise, delete by displayed number
            if (!arguments.TryGetProperty("number", out var numberEl))
                return "Error: Either 'number' or 'all: true' parameter is required.";

            var number = numberEl.GetInt32();
            var (success, message) = _lessonService.DeleteLesson(number);

            if (!success)
            {
                return $"# Lesson Not Deleted\n\n{message}\n\n*Use `lessons` (command `get`) to see available lessons.*";
            }

            var remaining = _lessonService.GetLessonCount();
            return $"# Lesson Deleted\n\n{message}\n**Remaining local:** {remaining}/100";
        }

        /// <summary>
        /// Syncs community lessons from the remote repository.
        /// </summary>
        public string SyncLessons()
        {
            _sendActivity("Syncing community lessons...");
            try
            {
                var (synced, count, message) = _communitySyncService.ForceSyncAsync().GetAwaiter().GetResult();
                return $"# Community Lessons Sync\n\n{message}";
            }
            catch (Exception ex)
            {
                return $"# Community Lessons Sync\n\n**Error:** {ex.Message}";
            }
        }

        /// <summary>
        /// Submits a local lesson to the community repository as a GitHub Issue.
        /// </summary>
        public string SubmitLesson(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("number", out var numberEl))
                return "Error: 'number' parameter is required (lesson number from `lessons` with command `get`).";

            var number = numberEl.GetInt32();
            var all = _lessonService.GetLessons();

            if (number < 1 || number > all.Count)
                return $"Error: Lesson #{number} not found.";

            var lesson = all[number - 1];
            if (lesson.Source == LessonSource.Community)
                return $"Error: Lesson #{number} is already a community lesson.";

            var settings = CommunityLessonSettings.Load();
            if (string.IsNullOrEmpty(settings.GitHubToken))
                return "Error: GitHub sign-in required. Open CodeMerger Settings > Community Lessons and click 'Sign in with GitHub'.";

            _sendActivity($"Submitting lesson #{number} to community...");

            try
            {
                var repoOwner = "pcarvalho75";
                var repoName = "CodeMerger";

                // Parse owner/repo from settings URL if available
                if (!string.IsNullOrEmpty(settings.RepoUrl))
                {
                    var uri = settings.RepoUrl.TrimEnd('/');
                    var parts = uri.Split('/');
                    if (parts.Length >= 2)
                    {
                        repoOwner = parts[parts.Length - 2];
                        repoName = parts[parts.Length - 1];
                    }
                }

                var contributor = !string.IsNullOrEmpty(settings.GitHubUsername)
                    ? $"@{settings.GitHubUsername}" : "Anonymous";

                var title = $"[Lesson] {lesson.Type}: {lesson.Component}";
                var body = $"## Observation\n{lesson.Observation}\n\n" +
                           $"## Proposal\n{lesson.Proposal}\n\n" +
                           $"**Type:** {lesson.Type}\n" +
                           $"**Component:** {lesson.Component}\n" +
                           $"**Contributed by:** {contributor}\n" +
                           $"**Logged:** {lesson.Timestamp:yyyy-MM-dd HH:mm}\n";

                if (!string.IsNullOrEmpty(lesson.SuggestedCode))
                    body += $"\n## Suggested Code\n```csharp\n{lesson.SuggestedCode}\n```\n";

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"token {settings.GitHubToken}");
                client.DefaultRequestHeaders.Add("User-Agent", "CodeMerger");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var payload = JsonSerializer.Serialize(new
                {
                    title,
                    body,
                    labels = new[] { "lesson", lesson.Type }
                });

                var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var response = client.PostAsync($"https://api.github.com/repos/{repoOwner}/{repoName}/issues", content)
                    .GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(responseJson);
                    var issueUrl = doc.RootElement.GetProperty("html_url").GetString();
                    return $"# Lesson Submitted\n\nLesson #{number} submitted as a GitHub Issue.\n**URL:** {issueUrl}";
                }
                else
                {
                    var errorBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return $"# Submission Failed\n\n**Status:** {(int)response.StatusCode}\n**Response:** {errorBody}";
                }
            }
            catch (Exception ex)
            {
                return $"# Submission Failed\n\n**Error:** {ex.Message}";
            }
        }
    }
}
