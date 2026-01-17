using System;
using System.Text;
using System.Text.Json;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles lesson-related MCP tools for self-improvement logging.
    /// </summary>
    public class McpLessonToolHandler
    {
        private readonly LessonService _lessonService;
        private readonly Action<string> _sendActivity;

        public McpLessonToolHandler(LessonService lessonService, Action<string> sendActivity)
        {
            _lessonService = lessonService;
            _sendActivity = sendActivity;
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
                       $"**Reason:** Lesson storage is full ({count}/10).\n\n" +
                       $"Ask the user to review lessons with `get_lessons` and either apply improvements or clear lessons with `delete_lesson`.";
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
            sb.AppendLine($"*Lessons stored: {newCount}/10*");

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

            var sb = new StringBuilder();
            sb.AppendLine("# Self-Improvement Lessons");
            sb.AppendLine();
            sb.AppendLine($"*{lessons.Count}/10 lessons logged*");
            sb.AppendLine();

            foreach (var lesson in lessons)
            {
                sb.AppendLine($"---");
                sb.AppendLine($"## Lesson #{lesson.Number}");
                sb.AppendLine();
                sb.AppendLine($"**Type:** {lesson.Type}");
                sb.AppendLine($"**Component:** {lesson.Component}");
                sb.AppendLine($"**Logged:** {lesson.Timestamp:yyyy-MM-dd HH:mm}");
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
            sb.AppendLine("*To apply improvements, review each lesson and use the code editing tools to implement changes.*");
            sb.AppendLine("*After improvements are applied, use `delete_lesson` to clear individual lessons or all lessons.*");

            return sb.ToString();
        }

        /// <summary>
        /// Deletes a specific lesson or all lessons.
        /// </summary>
        public string DeleteLesson(JsonElement arguments)
        {
            _sendActivity("Deleting lesson(s)");

            // Check if "all" parameter is true
            if (arguments.TryGetProperty("all", out var allEl) && allEl.GetBoolean())
            {
                var count = _lessonService.GetLessonCount();
                _lessonService.ClearAllLessons();
                return $"# Lessons Cleared\n\n**Deleted:** {count} lesson(s)\n\n*Lesson storage is now empty.*";
            }

            // Otherwise, delete by number
            if (!arguments.TryGetProperty("number", out var numberEl))
                return "Error: Either 'number' (1-10) or 'all: true' parameter is required.";

            var number = numberEl.GetInt32();

            if (number < 1 || number > 10)
                return "Error: Lesson number must be between 1 and 10.";

            var success = _lessonService.DeleteLesson(number);

            if (!success)
            {
                return $"# Lesson Not Found\n\n**Number:** {number}\n\n*Use `get_lessons` to see available lessons.*";
            }

            var remaining = _lessonService.GetLessonCount();
            return $"# Lesson Deleted\n\n**Number:** {number}\n**Remaining:** {remaining}/10";
        }
    }
}
