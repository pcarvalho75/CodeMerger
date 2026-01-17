using System;

namespace CodeMerger.Models
{
    /// <summary>
    /// Represents a self-improvement observation logged by Claude.
    /// </summary>
    public class Lesson
    {
        public int Number { get; set; }
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string Observation { get; set; } = string.Empty;
        public string Proposal { get; set; } = string.Empty;
        public string? SuggestedCode { get; set; }
    }
}
