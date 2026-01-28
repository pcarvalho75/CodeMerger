using System;
using System.IO;
using System.Text;

namespace CodeMerger.Services
{
    /// <summary>
    /// Thread-safe file logger for MCP server operations.
    /// Writes to a log file that can be easily copied for debugging.
    /// </summary>
    public class McpLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly object _lock = new object();
        private readonly StreamWriter _writer;
        private bool _disposed;

        /// <summary>
        /// Gets the full path to the log file.
        /// </summary>
        public string LogFilePath => _logFilePath;

        /// <summary>
        /// Event fired when a message is logged (for UI updates).
        /// </summary>
        public event Action<string>? OnLog;

        /// <summary>
        /// Creates a new logger that writes to the specified directory.
        /// </summary>
        /// <param name="logDirectory">Directory for the log file. If null, uses AppData/Local/CodeMerger.</param>
        /// <param name="clearOnStart">If true, clears the log file on startup.</param>
        public McpLogger(string? logDirectory = null, bool clearOnStart = true)
        {
            // Default to AppData/Local/CodeMerger
            if (string.IsNullOrEmpty(logDirectory))
            {
                logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodeMerger");
            }

            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, "codemerger-mcp.log");

            // Open file for writing (clear or append based on setting)
            var fileMode = clearOnStart ? FileMode.Create : FileMode.Append;
            var fileStream = new FileStream(_logFilePath, fileMode, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };

            // Write header
            _writer.WriteLine($"╔══════════════════════════════════════════════════════════════════╗");
            _writer.WriteLine($"║  CodeMerger MCP Log - Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine($"║  Log file: {_logFilePath}");
            _writer.WriteLine($"╚══════════════════════════════════════════════════════════════════╝");
            _writer.WriteLine();
        }

        /// <summary>
        /// Logs a message with timestamp.
        /// </summary>
        public void Log(string message)
        {
            if (_disposed) return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var formattedMessage = $"[{timestamp}] {message}";

            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(formattedMessage);
                }
                catch { }
            }

            // Fire event for UI
            OnLog?.Invoke($"[MCP] {message}");
        }

        /// <summary>
        /// Logs a message with timestamp and current memory usage.
        /// </summary>
        public void LogWithMemory(string message)
        {
            var memMB = GC.GetTotalMemory(false) / 1024 / 1024;
            Log($"{message} (Mem: {memMB}MB)");
        }

        /// <summary>
        /// Logs tool call information.
        /// </summary>
        public void LogToolCall(string toolName, string? result = null)
        {
            Log($"Tool: {toolName}");
            if (!string.IsNullOrEmpty(result) && result.Length < 500)
            {
                Log($"  Result: {result.Replace("\n", " ").Substring(0, Math.Min(result.Length, 200))}...");
            }
        }

        /// <summary>
        /// Logs an error with stack trace.
        /// </summary>
        public void LogError(string context, Exception ex)
        {
            Log($"ERROR in {context}: {ex.Message}");
            Log($"  Stack: {ex.StackTrace?.Replace("\n", "\n         ")}");
        }

        /// <summary>
        /// Logs a separator line for visual grouping.
        /// </summary>
        public void LogSeparator(string? label = null)
        {
            if (_disposed) return;

            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(label))
                    {
                        _writer.WriteLine("────────────────────────────────────────────────────────────────────");
                    }
                    else
                    {
                        _writer.WriteLine($"──────────────────────── {label} ────────────────────────");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Logs a JSON-RPC message (request or response).
        /// </summary>
        public void LogJsonRpc(string direction, string json)
        {
            if (_disposed) return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            lock (_lock)
            {
                try
                {
                    _writer.WriteLine($"[{timestamp}] {direction}:");
                    
                    // Pretty print if not too long
                    if (json.Length < 2000)
                    {
                        _writer.WriteLine($"  {json}");
                    }
                    else
                    {
                        _writer.WriteLine($"  {json.Substring(0, 500)}... ({json.Length} chars total)");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Gets the contents of the log file.
        /// </summary>
        public string GetLogContents()
        {
            lock (_lock)
            {
                _writer.Flush();
            }

            try
            {
                // Read with sharing since we're also writing
                using var reader = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(reader, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                return $"Error reading log: {ex.Message}";
            }
        }

        /// <summary>
        /// Gets the last N lines of the log file.
        /// </summary>
        public string GetRecentLog(int lines = 100)
        {
            lock (_lock)
            {
                _writer.Flush();
            }

            try
            {
                var allLines = File.ReadAllLines(_logFilePath);
                var startIndex = Math.Max(0, allLines.Length - lines);
                return string.Join(Environment.NewLine, allLines.Skip(startIndex));
            }
            catch (Exception ex)
            {
                return $"Error reading log: {ex.Message}";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine($"═══════════════════════════════════════════════════════════════════");
                    _writer.WriteLine($"  Log ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _writer.WriteLine($"═══════════════════════════════════════════════════════════════════");
                    _writer.Dispose();
                }
                catch { }
            }
        }
    }
}
