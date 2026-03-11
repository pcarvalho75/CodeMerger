using System;
using System.Windows.Media;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public enum ClaudeState { Disconnected, Connecting, Connected, Restarting, Error }
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
                    TrayStateChanged?.Invoke();
                }
            }
        }
        public string? ClaudeWorkspace { get; set; }
        public string? ErrorMessage { get; set; }

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

        /// <summary>Raised when tray icon appearance should update (state change or activity pulse).</summary>
        public event Action? TrayStateChanged;

        /// <summary>Brief activity flash for Claude (visual pulse on tray icon).</summary>
        public event Action? ClaudeActivityFlash;
        public void FlashClaudeActivity() => ClaudeActivityFlash?.Invoke();

        /// <summary>Brief activity flash for ChatGPT (visual pulse, not a persistent state).</summary>
        public event Action? ChatGptActivityFlash;
        public void FlashChatGptActivity() => ChatGptActivityFlash?.Invoke();

        // --- Helpers ---
        public void SetClaudeConnecting()
        {
            ErrorMessage = null;
            ClaudeState = ClaudeState.Connecting;
        }

        public void SetClaudeConnected(string workspace)
        {
            ErrorMessage = null;
            ClaudeWorkspace = workspace;
            ClaudeState = ClaudeState.Connected;
        }

        public void SetClaudeDisconnected()
        {
            ErrorMessage = null;
            ClaudeWorkspace = null;
            ClaudeState = ClaudeState.Disconnected;
        }

        public void SetClaudeError(string message)
        {
            ErrorMessage = message;
            ClaudeState = ClaudeState.Error;
        }

        // Current workspace
        public Workspace? CurrentWorkspace { get; set; }

        // Events for cross-control communication
        public event Action<string, Brush>? StatusUpdated;
        public void UpdateStatus(string msg, Brush color) => StatusUpdated?.Invoke(msg, color);
    }
}
