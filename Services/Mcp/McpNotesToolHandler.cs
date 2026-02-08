using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles project notes tools for persistent context across sessions.
    /// Notes are stored in CODEMERGER_NOTES.md in the project root.
    /// </summary>
    public class McpNotesToolHandler
    {
        private const string NotesFileName = "CODEMERGER_NOTES.md";
        private const int MaxFileSizeBytes = 20 * 1024; // ~20KB ≈ 5000 tokens
        private const int WarnThresholdBytes = 16 * 1024; // Warn at ~80% capacity

        private readonly string _workspacePath;
        private readonly Action<string> _sendActivity;

        public McpNotesToolHandler(string workspacePath, Action<string> sendActivity)
        {
            _workspacePath = workspacePath;
            _sendActivity = sendActivity;
        }

        /// <summary>
        /// Routes a notes command to the appropriate handler method.
        /// </summary>
        public string HandleCommand(string command, JsonElement arguments)
        {
            switch (command)
            {
                case "get":
                    _sendActivity("Reading project notes...");
                    return GetNotes();

                case "add":
                    var note = arguments.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;
                    if (string.IsNullOrEmpty(note))
                        return "Error: 'note' parameter is required.";
                    var section = arguments.TryGetProperty("section", out var sectionEl) ? sectionEl.GetString() : null;
                    var (success, message, summary) = AddNote(note, section);
                    if (success && !string.IsNullOrEmpty(summary))
                        _sendActivity($"Note added: {summary}");
                    return message;

                case "update":
                    var updateSection = arguments.TryGetProperty("section", out var updateSectionEl) ? updateSectionEl.GetString() : null;
                    var content = arguments.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
                    if (string.IsNullOrEmpty(updateSection))
                        return "Error: 'section' parameter is required.";
                    if (string.IsNullOrEmpty(content))
                        return "Error: 'content' parameter is required.";
                    _sendActivity($"Updating section: {updateSection}");
                    return UpdateNote(updateSection, content).message;

                case "clear":
                    var clearSection = arguments.TryGetProperty("section", out var clearSectionEl) ? clearSectionEl.GetString() : null;
                    _sendActivity(string.IsNullOrEmpty(clearSection) ? "Clearing all notes..." : $"Clearing: {clearSection}");
                    return ClearNotes(clearSection).message;

                case "delete":
                    if (!arguments.TryGetProperty("lineNumber", out var lineNumEl) || lineNumEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        return "Error: 'lineNumber' parameter is required and must be a number.";
                    var lineNumber = lineNumEl.GetInt32();
                    var (deleteSuccess, deleteMessage, deletedNote) = DeleteNote(lineNumber);
                    if (deleteSuccess)
                        _sendActivity($"Deleted: {deletedNote}");
                    return deleteMessage;

                default:
                    return "Error: Unknown notes action. Use: get, add, update, clear, delete";
            }
        }

        private string NotesFilePath => Path.Combine(_workspacePath, NotesFileName);

        public string GetNotes()
        {
            if (!File.Exists(NotesFilePath))
            {
                return "# Project Notes\n\nNo notes yet. Use `notes` (command `add`) to start taking notes.";
            }

            var lines = File.ReadAllLines(NotesFilePath);
            var sb = new StringBuilder();
            
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Add line numbers to note lines (lines starting with "- [")
                if (line.TrimStart().StartsWith("- ["))
                {
                    sb.AppendLine($"[{i + 1}] {line}");
                }
                else
                {
                    sb.AppendLine(line);
                }
            }

            var content = sb.ToString().TrimEnd();
            var sizeInfo = GetSizeInfo(content);

            return $"{content}\n\n---\n{sizeInfo}";
        }

        public string? GetNotesRaw()
        {
            if (!File.Exists(NotesFilePath))
                return null;

            return File.ReadAllText(NotesFilePath);
        }

        public (bool success, string message, string? noteSummary) AddNote(string note, string? section = null)
        {
            var currentContent = File.Exists(NotesFilePath) 
                ? File.ReadAllText(NotesFilePath) 
                : "# Project Notes\n";

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var formattedNote = $"- [{timestamp}] {note}";

            string newContent;

            if (!string.IsNullOrEmpty(section))
            {
                newContent = AddNoteToSection(currentContent, section, formattedNote);
            }
            else
            {
                newContent = currentContent.TrimEnd() + "\n\n" + formattedNote + "\n";
            }

            var newSizeBytes = Encoding.UTF8.GetByteCount(newContent);
            if (newSizeBytes > MaxFileSizeBytes)
            {
                return (false, $"Cannot add note: would exceed limit ({newSizeBytes / 1024}KB > 20KB). Consider clearing old notes.", null);
            }

            File.WriteAllText(NotesFilePath, newContent);

            var warning = newSizeBytes > WarnThresholdBytes 
                ? $" ⚠️ Notes at {newSizeBytes * 100 / MaxFileSizeBytes}% capacity." 
                : "";

            var summary = note.Length > 60 ? note.Substring(0, 57) + "..." : note;

            return (true, $"Note added.{warning}", summary);
        }

        public (bool success, string message) UpdateNote(string section, string newContent)
        {
            if (!File.Exists(NotesFilePath))
            {
                var content = $"# Project Notes\n\n## {section}\n\n{newContent}\n";
                File.WriteAllText(NotesFilePath, content);
                return (true, $"Created notes with section '{section}'.");
            }

            var currentContent = File.ReadAllText(NotesFilePath);
            var updatedContent = ReplaceSectionContent(currentContent, section, newContent);

            var newSizeBytes = Encoding.UTF8.GetByteCount(updatedContent);
            if (newSizeBytes > MaxFileSizeBytes)
            {
                return (false, $"Cannot update: would exceed limit ({newSizeBytes / 1024}KB > 20KB).");
            }

            File.WriteAllText(NotesFilePath, updatedContent);

            var warning = newSizeBytes > WarnThresholdBytes
                ? $" ⚠️ Notes at {newSizeBytes * 100 / MaxFileSizeBytes}% capacity."
                : "";

            return (true, $"Section '{section}' updated.{warning}");
        }

        public (bool success, string message) ClearNotes(string? section = null)
        {
            if (!File.Exists(NotesFilePath))
            {
                return (true, "Notes already empty.");
            }

            if (string.IsNullOrEmpty(section))
            {
                File.Delete(NotesFilePath);
                return (true, "All notes cleared.");
            }

            var currentContent = File.ReadAllText(NotesFilePath);
            var updatedContent = RemoveSection(currentContent, section);
            File.WriteAllText(NotesFilePath, updatedContent);

            return (true, $"Section '{section}' cleared.");
        }

        public (bool success, string message, string? deletedNote) DeleteNote(int lineNumber)
        {
            if (!File.Exists(NotesFilePath))
            {
                return (false, "No notes file exists.", null);
            }

            var lines = File.ReadAllLines(NotesFilePath).ToList();
            
            if (lineNumber < 1 || lineNumber > lines.Count)
            {
                return (false, $"Invalid line number {lineNumber}. File has {lines.Count} lines.", null);
            }

            var targetLine = lines[lineNumber - 1];
            
            // Verify it's actually a note line
            if (!targetLine.TrimStart().StartsWith("- ["))
            {
                return (false, $"Line {lineNumber} is not a note. Notes start with '- [timestamp]'.", null);
            }

            lines.RemoveAt(lineNumber - 1);
            
            // Remove consecutive blank lines left behind
            for (int i = lines.Count - 1; i > 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]) && string.IsNullOrWhiteSpace(lines[i - 1]))
                    lines.RemoveAt(i);
            }
            
            File.WriteAllText(NotesFilePath, string.Join("\n", lines) + "\n");

            return (true, $"Deleted note at line {lineNumber}.", targetLine.Trim());
        }

        private string AddNoteToSection(string content, string section, string note)
        {
            var pattern = @"(^|\n)(#{1,2}\s*" + Regex.Escape(section) + @"\s*\n)";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var sectionStart = match.Index + match.Length;
                var nextHeaderMatch = Regex.Match(content.Substring(sectionStart), @"\n#{1,2}\s+\w");

                int insertPosition = nextHeaderMatch.Success 
                    ? sectionStart + nextHeaderMatch.Index 
                    : content.Length;

                return content.Substring(0, insertPosition).TrimEnd() + "\n" + note + "\n" + content.Substring(insertPosition);
            }

            return content.TrimEnd() + $"\n\n## {section}\n\n{note}\n";
        }

        private string ReplaceSectionContent(string content, string section, string newSectionContent)
        {
            var pattern = @"(^|\n)(#{1,2}\s*" + Regex.Escape(section) + @"\s*\n)";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var sectionHeaderEnd = match.Index + match.Length;
                var nextHeaderMatch = Regex.Match(content.Substring(sectionHeaderEnd), @"\n#{1,2}\s+\w");

                string before = content.Substring(0, sectionHeaderEnd);
                string after = nextHeaderMatch.Success 
                    ? content.Substring(sectionHeaderEnd + nextHeaderMatch.Index) 
                    : "";

                return before + newSectionContent.Trim() + "\n" + after;
            }

            return content.TrimEnd() + $"\n\n## {section}\n\n{newSectionContent.Trim()}\n";
        }

        private string RemoveSection(string content, string section)
        {
            var pattern = @"(\n?#{1,2}\s*" + Regex.Escape(section) + @"\s*\n)[\s\S]*?(?=\n#{1,2}\s+\w|$)";
            return Regex.Replace(content, pattern, "", RegexOptions.IgnoreCase).Trim() + "\n";
        }

        private string GetSizeInfo(string content)
        {
            var bytes = Encoding.UTF8.GetByteCount(content);
            var percent = bytes * 100 / MaxFileSizeBytes;
            var estimatedTokens = bytes / 4;

            return $"*Notes: ~{estimatedTokens} tokens ({percent}% of limit)*";
        }
    }
}
