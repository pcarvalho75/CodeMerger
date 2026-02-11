using CodeMerger.Models;
using CodeMerger.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger.Controls
{
    public partial class LLMsTab : UserControl
    {
        private const int DefaultSsePort = 52780;

        private ClaudeDesktopService _claudeDesktopService = null!;
        private Func<Workspace?> _getCurrentWorkspace = null!;
        private AppState? _appState;

        private TunnelService? _tunnelService;
        private McpServer? _mcpServerForSse;

        /// <summary>Raised when a status message should be shown. Args = message text.</summary>
        public event EventHandler<string>? StatusUpdate;

        /// <summary>Raised when the progress bar needs updating. Args = (progress 0-100, visible).</summary>
        public event EventHandler<(double Progress, bool Visible)>? ProgressUpdate;

        public LLMsTab()
        {
            InitializeComponent();
        }

        public void Initialize(ClaudeDesktopService claudeDesktopService, Func<Workspace?> getCurrentWorkspace, AppState appState)
        {
            _claudeDesktopService = claudeDesktopService;
            _getCurrentWorkspace = getCurrentWorkspace;
            _appState = appState;
        }

        public void RefreshClaudeDesktopStatus()
        {
            if (_claudeDesktopService.IsClaudeDesktopInstalled())
            {
                claudeInstallStatus.Text = "Installed ✓";
                claudeInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                claudeDownloadButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                claudeInstallStatus.Text = "Not installed";
                claudeInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                claudeDownloadButton.Visibility = Visibility.Visible;
            }

            if (_claudeDesktopService.IsConfigured())
            {
                var configuredPath = _claudeDesktopService.GetConfiguredExePath();
                var currentExePath = _claudeDesktopService.GetCurrentExePath();
                var stableExePath = _claudeDesktopService.GetStableExePath();

                bool isCorrect = string.Equals(configuredPath, currentExePath, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(configuredPath, stableExePath, StringComparison.OrdinalIgnoreCase);

                if (isCorrect)
                {
                    bool isDebug = _claudeDesktopService.IsDebugRun();
                    claudeConfigStatus.Text = isDebug ? "Ready ✓ (Debug)" : "Ready ✓";
                    claudeConfigStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                }
                else
                {
                    claudeConfigStatus.Text = "Path mismatch";
                    claudeConfigStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                }
                claudeAddConfigButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                claudeConfigStatus.Text = "Not configured";
                claudeConfigStatus.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                claudeAddConfigButton.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Get the SSE server's log file path (if running).</summary>
        public string? GetSseLogFilePath() => _mcpServerForSse?.LogFilePath;

        public void StopChatGptServer()
        {
            // Stop tunnel first
            _tunnelService?.Stop();
            _tunnelService?.Dispose();
            _tunnelService = null;

            // Stop SSE server
            _mcpServerForSse?.StopSse();
            _mcpServerForSse?.Stop();
            _mcpServerForSse = null;

            // Reset UI
            chatGptStartButton.Visibility = Visibility.Visible;
            chatGptStartButton.IsEnabled = true;
            chatGptStopButton.Visibility = Visibility.Collapsed;
            chatGptStatusText.Text = "Stopped";
            chatGptStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
            chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
            if (_appState != null) _appState.ChatGptState = ChatGptState.Stopped;
            publicUrlTextBox.Text = "Click 'Start Server' to generate URL";
            copyUrlButton.IsEnabled = false;
            ProgressUpdate?.Invoke(this, (0, false));

            RaiseStatus("ChatGPT server stopped", Brushes.Gray);
        }

        public void Cleanup()
        {
            _tunnelService?.Dispose();
            _mcpServerForSse?.Stop();
        }

        #region Click Handlers

        private void ClaudeDownload_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenDownloadPage();
        }

        private void ClaudeAddConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _claudeDesktopService.SelfHeal();
                RaiseStatus("Added CodeMerger to Claude Desktop config.", Brushes.LightGreen);
                RefreshClaudeDesktopStatus();
            }
            catch (Exception ex)
            {
                RaiseStatus($"Failed to update config: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
        }

        private void ClaudeOpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenConfigFolder();
        }

        private async void ChatGptStart_Click(object sender, RoutedEventArgs e)
        {
            var workspace = _getCurrentWorkspace();
            if (workspace == null)
            {
                RaiseStatus("Select a workspace first", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            chatGptStartButton.IsEnabled = false;
            RaiseStatus("Starting ChatGPT server...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));

            try
            {
                // Create and index McpServer for SSE
                _mcpServerForSse = new McpServer();
                _mcpServerForSse.OnLog += msg => Dispatcher.Invoke(() => RaiseStatus(msg, Brushes.Gray));

                var extensions = workspace.Extensions
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToList();

                var ignoredDirs = (workspace.IgnoredDirectories + ",.git")
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant())
                    .ToHashSet();

                var activeDirectories = workspace.InputDirectories
                    .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                    .ToList();

                _mcpServerForSse.IndexWorkspace(workspace.Name, activeDirectories, extensions, ignoredDirs);

                // Wire up SSE events
                _mcpServerForSse.OnSseClientConnected += sessionId => Dispatcher.Invoke(() =>
                {
                    if (_appState != null) _appState.ChatGptState = ChatGptState.Connected;
                    var green = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                    chatGptIndicator.Fill = green;
                    chatGptStatusText.Text = "Connected";
                    chatGptStatusText.Foreground = green;
                    RaiseStatus($"✓ ChatGPT connected (session: {sessionId})", Brushes.LightGreen);
                });

                _mcpServerForSse.OnSseClientDisconnected += sessionId => Dispatcher.Invoke(() =>
                {
                    if (_appState != null) _appState.ChatGptState = ChatGptState.Running;
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    chatGptStatusText.Text = "Running";
                    chatGptStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                    RaiseStatus($"ChatGPT disconnected (session: {sessionId})", Brushes.Gray);
                });

                // Activity flash when ChatGPT makes requests
                _mcpServerForSse.OnSseMessageReceived += method => Dispatcher.Invoke(async () =>
                {
                    if (_appState != null) _appState.ChatGptState = ChatGptState.Connected;
                    var bright = new SolidColorBrush(Color.FromRgb(0, 255, 150));
                    _appState?.FlashChatGptActivity();
                    chatGptIndicator.Fill = bright;
                    chatGptStatusText.Text = method;

                    await Task.Delay(300);

                    if (_appState?.ChatGptState == ChatGptState.Connected)
                    {
                        var green = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                        chatGptIndicator.Fill = green;
                        chatGptStatusText.Text = "Connected";
                    }
                });

                // Start HTTP transport (tunnel will provide HTTPS)
                _mcpServerForSse.StartSse(DefaultSsePort, useHttps: false);

                // Update UI to show starting state
                chatGptStatusText.Text = "Starting tunnel...";
                chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                if (_appState != null) _appState.ChatGptState = ChatGptState.Starting;

                // Now start the tunnel
                _tunnelService = new TunnelService();
                _tunnelService.OnLog += msg => Dispatcher.Invoke(() => RaiseStatus(msg, Brushes.Gray));
                _tunnelService.OnDownloadProgress += progress => Dispatcher.Invoke(() =>
                {
                    ProgressUpdate?.Invoke(this, (progress, progress < 100));
                    RaiseStatus($"Downloading cloudflared... {progress}%", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
                });
                _tunnelService.OnUrlAvailable += url => Dispatcher.Invoke(() =>
                {
                    var sseUrl = _tunnelService.GetSseUrl();
                    publicUrlTextBox.Text = sseUrl ?? url;
                    copyUrlButton.IsEnabled = true;

                    // Auto-copy to clipboard
                    if (!string.IsNullOrEmpty(sseUrl))
                    {
                        Clipboard.SetText(sseUrl);
                    }

                    chatGptStatusText.Text = "Ready";
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                    if (_appState != null) _appState.ChatGptState = ChatGptState.Ready;
                    RaiseStatus($"✓ ChatGPT server ready! URL copied to clipboard", Brushes.LightGreen);
                });
                _tunnelService.OnDisconnected += () => Dispatcher.Invoke(() =>
                {
                    if (_mcpServerForSse != null && _mcpServerForSse.IsSseRunning)
                    {
                        publicUrlTextBox.Text = "Tunnel disconnected - restart server";
                        copyUrlButton.IsEnabled = false;
                        chatGptStatusText.Text = "Tunnel lost";
                        chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(233, 69, 96));
                        if (_appState != null) _appState.ChatGptState = ChatGptState.TunnelLost;
                        RaiseStatus("Tunnel disconnected. Click Stop then Start again.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                    }
                });
                _tunnelService.OnError += msg => Dispatcher.Invoke(() =>
                {
                    RaiseStatus(msg, new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                    chatGptStatusText.Text = "Tunnel failed";
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(233, 69, 96));
                    if (_appState != null) _appState.ChatGptState = ChatGptState.TunnelFailed;
                    chatGptStartButton.IsEnabled = false;
                    chatGptStopButton.Visibility = Visibility.Visible;
                });

                bool tunnelSuccess = await _tunnelService.StartAsync(DefaultSsePort);

                if (tunnelSuccess)
                {
                    chatGptStartButton.Visibility = Visibility.Collapsed;
                    chatGptStopButton.Visibility = Visibility.Visible;
                }
                else
                {
                    StopChatGptServer();
                    chatGptStartButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                RaiseStatus($"Failed to start: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                StopChatGptServer();
                chatGptStartButton.IsEnabled = true;
            }
        }

        private void ChatGptStop_Click(object sender, RoutedEventArgs e)
        {
            StopChatGptServer();
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var url = publicUrlTextBox.Text;
            if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
            {
                Clipboard.SetText(url);
                RaiseStatus($"Copied to clipboard: {url}", Brushes.LightGreen);
            }
        }

        private void TunnelHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(TunnelService.GetInstallInstructions(), "ChatGPT Desktop Setup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        private void RaiseStatus(string message, Brush color)
        {
            StatusUpdate?.Invoke(this, message);
        }
    }
}
