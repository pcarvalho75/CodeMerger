using System;

namespace CodeMerger.Models
{
    public enum ActivityLogType
    {
        ToolCall,
        Error,
        Timeout,
        Connection,
        Disconnection,
        WorkspaceSwitch,
        Info
    }

    /// <summary>
    /// A single entry in the activity log with timestamp, type, tool info, and duration.
    /// </summary>
    public class ActivityLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public ActivityLogType Type { get; set; }
        public string ToolName { get; set; } = string.Empty;
        public long DurationMs { get; set; }
        public string Message { get; set; } = string.Empty;

        public string TypeIcon => Type switch
        {
            ActivityLogType.ToolCall => "🔧",
            ActivityLogType.Error => "❌",
            ActivityLogType.Timeout => "⏳",
            ActivityLogType.Connection => "🟢",
            ActivityLogType.Disconnection => "🔴",
            ActivityLogType.WorkspaceSwitch => "🔄",
            ActivityLogType.Info => "ℹ️",
            _ => "•"
        };

        public string TimeText => Timestamp.ToString("HH:mm:ss");
        public string DurationText => DurationMs > 0 ? $"{DurationMs}ms" : "";
    }
}
