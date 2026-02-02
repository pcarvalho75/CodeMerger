namespace CodeMerger.Models
{
    /// <summary>
    /// Activity types for MCP server monitoring.
    /// Used to distinguish MCP processing from Claude cloud delays.
    /// </summary>
    public enum McpActivityType
    {
        /// <summary>
        /// Tool execution started - MCP server is processing.
        /// </summary>
        STARTED,

        /// <summary>
        /// Tool execution completed - ball is in Claude's court.
        /// </summary>
        COMPLETED,

        /// <summary>
        /// Tool execution failed with error.
        /// </summary>
        ERROR
    }

    /// <summary>
    /// Structured activity message for MCP monitoring.
    /// Format: TYPE|toolName|details
    /// Examples:
    /// - STARTED|codemerger_get_lines|
    /// - COMPLETED|codemerger_get_lines|12ms
    /// - ERROR|codemerger_str_replace|File not found
    /// </summary>
    public class McpActivityMessage
    {
        public McpActivityType Type { get; set; }
        public string ToolName { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;

        /// <summary>
        /// Parse activity message from pipe-delimited string.
        /// </summary>
        public static McpActivityMessage? Parse(string message)
        {
            var parts = message.Split('|');
            if (parts.Length < 2) return null;

            if (!Enum.TryParse<McpActivityType>(parts[0], out var type))
                return null;

            return new McpActivityMessage
            {
                Type = type,
                ToolName = parts[1],
                Details = parts.Length > 2 ? parts[2] : string.Empty
            };
        }

        /// <summary>
        /// Format activity message as pipe-delimited string.
        /// </summary>
        public override string ToString()
        {
            return $"{Type}|{ToolName}|{Details}";
        }
    }
}
