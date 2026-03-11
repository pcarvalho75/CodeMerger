using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger.Controls
{
    public partial class StatusBar : UserControl
    {
        private AppState? _appState;

        public StatusBar()
        {
            InitializeComponent();
        }

        /// <summary>Subscribe to AppState for automatic workspace info updates.</summary>
        public void Initialize(AppState appState)
        {
            _appState = appState;
            _appState.ConnectionStateChanged += OnConnectionStateChanged;
        }

        private void OnConnectionStateChanged()
        {
            if (_appState == null) return;

            switch (_appState.ClaudeState)
            {
                case ClaudeState.Connected:
                    workspaceInfoLabel.Text = $"📂 {_appState.ClaudeWorkspace}";
                    break;
                case ClaudeState.Error:
                    workspaceInfoLabel.Text = $"⚠ {_appState.ErrorMessage}";
                    break;
                case ClaudeState.Connecting:
                    workspaceInfoLabel.Text = "Connecting...";
                    break;
                case ClaudeState.Disconnected:
                case ClaudeState.Restarting:
                default:
                    workspaceInfoLabel.Text = "";
                    break;
            }
        }

        /// <summary>Update the status bar message and color.</summary>
        public void UpdateStatus(string message, Brush color)
        {
            statusText.Text = message;
            statusText.Foreground = color;
        }

        /// <summary>Set progress bar visibility and value.</summary>
        public void SetProgress(double value, bool visible)
        {
            progressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = value;
        }

        /// <summary>Set the file status label text.</summary>
        public void SetFileStatus(string text)
        {
            fileStatusLabel.Text = text;
        }

        /// <summary>Set UI state for scanning operations.</summary>
        public void SetScanningState(bool isEnabled)
        {
            progressBar.Visibility = isEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (isEnabled)
            {
                fileStatusLabel.Text = "";
                progressBar.Value = 0;
            }
        }

        /// <summary>Set workspace health info (name, file count, etc.). Called by INDEXED activity to enrich display.</summary>
        public void SetWorkspaceInfo(string info)
        {
            workspaceInfoLabel.Text = info;
        }
    }
}
