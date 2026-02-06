using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public enum ClaudeState { Disconnected, Connected, Restarting }
    public enum ChatGptState { Stopped, Starting, Ready, Connected, Running, TunnelLost, TunnelFailed }

    /// <summary>
    /// Single source of truth for connection state. All controls subscribe to ConnectionStateChanged.
    /// </summary>
    public class AppState
    {
        // --- Claude Connection ---
        private ClaudeState _claudeState = ClaudeState.Disconnected;
        public ClaudeState ClaudeState
        {
            get => _claudeState;
            set
            {
                if (_claudeState != value)
                {
                    _claudeState = value;
                    ConnectionStateChanged?.Invoke();
                }
            }
        }
        public string? ClaudeWorkspace { get; set; }

        // --- ChatGPT Connection ---
        private ChatGptState _chatGptState = ChatGptState.Stopped;
        public ChatGptState ChatGptState
        {
            get => _chatGptState;
            set
            {
                if (_chatGptState != value)
                {
                    _chatGptState = value;
                    ConnectionStateChanged?.Invoke();
                }
            }
        }

        // --- Connection Events ---
        public event Action? ConnectionStateChanged;

        /// <summary>Brief activity flash for ChatGPT (visual pulse, not a persistent state).</summary>
        public event Action? ChatGptActivityFlash;
        public void FlashChatGptActivity() => ChatGptActivityFlash?.Invoke();

        // --- Helpers ---
        public void SetClaudeConnected(string workspace)
        {
            ClaudeWorkspace = workspace;
            ClaudeState = ClaudeState.Connected;
        }

        public void SetClaudeDisconnected()
        {
            ClaudeWorkspace = null;
            ClaudeState = ClaudeState.Disconnected;
        }

        // Current workspace
        public Workspace? CurrentWorkspace { get; set; }

        // Session stats
        public McpSessionStats SessionStats { get; } = new();

        // Activity log
        public ObservableCollection<ActivityLogEntry> ActivityLog { get; } = new();

        // Found files
        public ObservableCollection<string> FoundFiles { get; } = new();

        // Events for cross-control communication
        public event Action<string, Brush>? StatusUpdated;
        public void UpdateStatus(string msg, Brush color) => StatusUpdated?.Invoke(msg, color);
    }
}
