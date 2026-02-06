using System;
using System.Collections;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public partial class HeaderBar : UserControl
    {
        private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0, 217, 165));
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(136, 146, 160));
        private static readonly Brush ChatGptGreenBrush = new SolidColorBrush(Color.FromRgb(16, 163, 127));
        private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(233, 69, 96));
        private static readonly Brush BrightGreenBrush = new SolidColorBrush(Color.FromRgb(0, 255, 150));

        private AppState? _appState;

        // Events for MainWindow to handle
        public event EventHandler<Workspace>? WorkspaceSelectionChanged;
        public event EventHandler? NewWorkspaceClicked;
        public event EventHandler? RenameWorkspaceClicked;
        public event EventHandler? DeleteWorkspaceClicked;
        public event EventHandler? StopServerClicked;

        public HeaderBar()
        {
            InitializeComponent();
        }

        public void Initialize(AppState appState)
        {
            _appState = appState;
            _appState.ConnectionStateChanged += () => Dispatcher.Invoke(OnConnectionStateChanged);
            _appState.ChatGptActivityFlash += () => Dispatcher.Invoke(OnChatGptActivityFlash);
        }

        // --- Public API for MainWindow ---

        /// <summary>Current connection status text (for tray tooltip).</summary>
        public string ConnectionStatusText => connectionStatusText.Text;

        /// <summary>Bind workspace list to the ComboBox.</summary>
        public void SetWorkspaceSource(IEnumerable source)
        {
            projectComboBox.ItemsSource = source;
        }

        /// <summary>Select a workspace in the ComboBox.</summary>
        public void SelectWorkspace(Workspace? workspace)
        {
            projectComboBox.SelectedItem = workspace;
        }

        /// <summary>Get currently selected workspace.</summary>
        public Workspace? SelectedWorkspace => projectComboBox.SelectedItem as Workspace;

        /// <summary>Temporarily switch workspace while controls are disabled (MCP-driven switch).</summary>
        public void SwitchWorkspaceFromMcp(Workspace workspace)
        {
            projectComboBox.IsEnabled = true;
            projectComboBox.SelectedItem = workspace;
            projectComboBox.IsEnabled = false;
        }

        // --- Connection State Handler (subscribes to AppState) ---

        private void OnConnectionStateChanged()
        {
            if (_appState == null) return;

            // Claude indicator
            switch (_appState.ClaudeState)
            {
                case ClaudeState.Connected:
                    connectionIndicator.Fill = GreenBrush;
                    connectionStatusText.Text = $"Connected: {_appState.ClaudeWorkspace}";
                    connectionStatusText.Foreground = GreenBrush;
                    stopServerButton.Visibility = Visibility.Visible;
                    SetProjectControlsEnabled(false);
                    break;

                case ClaudeState.Restarting:
                    connectionIndicator.Fill = GrayBrush;
                    connectionStatusText.Text = "Restarting...";
                    connectionStatusText.Foreground = GrayBrush;
                    stopServerButton.Visibility = Visibility.Collapsed;
                    break;

                case ClaudeState.Disconnected:
                default:
                    connectionIndicator.Fill = GrayBrush;
                    connectionStatusText.Text = "Disconnected";
                    connectionStatusText.Foreground = GrayBrush;
                    stopServerButton.Visibility = Visibility.Collapsed;
                    SetProjectControlsEnabled(true);
                    break;
            }

            // ChatGPT indicator
            switch (_appState.ChatGptState)
            {
                case ChatGptState.Connected:
                    chatGptConnectionIndicator.Fill = ChatGptGreenBrush;
                    break;
                case ChatGptState.Starting:
                case ChatGptState.Running:
                    chatGptConnectionIndicator.Fill = YellowBrush;
                    break;
                case ChatGptState.Ready:
                    chatGptConnectionIndicator.Fill = GreenBrush;
                    break;
                case ChatGptState.TunnelLost:
                case ChatGptState.TunnelFailed:
                    chatGptConnectionIndicator.Fill = RedBrush;
                    break;
                case ChatGptState.Stopped:
                default:
                    chatGptConnectionIndicator.Fill = GrayBrush;
                    break;
            }
        }

        private async void OnChatGptActivityFlash()
        {
            chatGptConnectionIndicator.Fill = BrightGreenBrush;
            await Task.Delay(300);
            if (_appState?.ChatGptState == ChatGptState.Connected)
                chatGptConnectionIndicator.Fill = ChatGptGreenBrush;
        }

        /// <summary>Enable/disable project controls (combobox + buttons).</summary>
        private void SetProjectControlsEnabled(bool enabled)
        {
            projectComboBox.IsEnabled = enabled;
            projectComboBox.ToolTip = enabled ? null : "Project is locked while Claude is connected";
            newWorkspaceButton.IsEnabled = enabled;
            renameWorkspaceButton.IsEnabled = enabled;
            deleteWorkspaceButton.IsEnabled = enabled;
        }

        // --- Internal event handlers (route to MainWindow via events) ---

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (projectComboBox.SelectedItem is Workspace selected)
                WorkspaceSelectionChanged?.Invoke(this, selected);
        }

        private void NewWorkspace_Click(object sender, RoutedEventArgs e) => NewWorkspaceClicked?.Invoke(this, EventArgs.Empty);
        private void RenameWorkspace_Click(object sender, RoutedEventArgs e) => RenameWorkspaceClicked?.Invoke(this, EventArgs.Empty);
        private void DeleteWorkspace_Click(object sender, RoutedEventArgs e) => DeleteWorkspaceClicked?.Invoke(this, EventArgs.Empty);
        private void StopServer_Click(object sender, RoutedEventArgs e) => StopServerClicked?.Invoke(this, EventArgs.Empty);
    }
}
