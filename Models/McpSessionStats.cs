using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeMerger.Models
{
    /// <summary>
    /// Tracks MCP server activity statistics for the current session.
    /// </summary>
    public class McpSessionStats
    {
        private readonly List<ToolCallRecord> _callHistory = new();
        private readonly object _lock = new();
        
        public int TotalToolCalls { get; private set; }
        public int TotalErrors { get; private set; }
        public int TotalTimeouts { get; private set; }
        public double AverageResponseTime { get; private set; }
        public DateTime SessionStartTime { get; private set; }
        public DateTime LastActivityTime { get; private set; }

        public McpSessionStats()
        {
            SessionStartTime = DateTime.Now;
            LastActivityTime = DateTime.Now;
        }

        /// <summary>
        /// Record a successful tool call with its execution time.
        /// </summary>
        public void RecordToolCall(string toolName, long durationMs)
        {
            lock (_lock)
            {
                TotalToolCalls++;
                LastActivityTime = DateTime.Now;
                
                _callHistory.Add(new ToolCallRecord
                {
                    ToolName = toolName,
                    DurationMs = durationMs,
                    Timestamp = DateTime.Now
                });

                // Recalculate average
                AverageResponseTime = _callHistory.Average(c => c.DurationMs);
            }
        }

        /// <summary>
        /// Record a tool call error.
        /// </summary>
        public void RecordError()
        {
            lock (_lock)
            {
                TotalErrors++;
                LastActivityTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Record a timeout (Claude didn't respond within threshold).
        /// </summary>
        public void RecordTimeout()
        {
            lock (_lock)
            {
                TotalTimeouts++;
                LastActivityTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Reset all statistics.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                TotalToolCalls = 0;
                TotalErrors = 0;
                TotalTimeouts = 0;
                AverageResponseTime = 0;
                SessionStartTime = DateTime.Now;
                LastActivityTime = DateTime.Now;
                _callHistory.Clear();
            }
        }

        /// <summary>
        /// Get formatted summary string for display.
        /// </summary>
        public string GetSummary()
        {
            lock (_lock)
            {
                if (TotalToolCalls == 0 && TotalErrors == 0 && TotalTimeouts == 0)
                    return "No activity this session";

                var parts = new List<string>();
                
                if (TotalToolCalls > 0)
                    parts.Add($"{TotalToolCalls} call{(TotalToolCalls == 1 ? "" : "s")}");
                
                if (TotalToolCalls > 0)
                    parts.Add($"avg {AverageResponseTime:F0}ms");
                
                if (TotalTimeouts > 0)
                    parts.Add($"{TotalTimeouts} timeout{(TotalTimeouts == 1 ? "" : "s")}");
                
                if (TotalErrors > 0)
                    parts.Add($"{TotalErrors} error{(TotalErrors == 1 ? "" : "s")}");

                return string.Join(" | ", parts);
            }
        }

        /// <summary>
        /// Get the slowest tool call in this session.
        /// </summary>
        public (string ToolName, long DurationMs)? GetSlowestTool()
        {
            lock (_lock)
            {
                if (_callHistory.Count == 0)
                    return null;

                var slowest = _callHistory.OrderByDescending(c => c.DurationMs).First();
                return (slowest.ToolName, slowest.DurationMs);
            }
        }

        /// <summary>
        /// Get session duration in a human-readable format.
        /// </summary>
        public string GetSessionDuration()
        {
            var duration = DateTime.Now - SessionStartTime;
            
            if (duration.TotalMinutes < 1)
                return $"{(int)duration.TotalSeconds}s";
            
            if (duration.TotalHours < 1)
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        private class ToolCallRecord
        {
            public string ToolName { get; set; } = string.Empty;
            public long DurationMs { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
