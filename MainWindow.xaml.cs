using CodeMerger.Models;
using CodeMerger.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<string> FoundFiles { get; set; }

        private readonly WorkspaceManager _workspaceManager = new WorkspaceManager();
        private readonly ClaudeDesktopService _claudeDesktopService = new ClaudeDesktopService();
        private readonly McpConnectionService _mcpConnectionService;
        private readonly FileScannerService _fileScannerService = new FileScannerService();
        private readonly DirectoryManager _directoryManager = new DirectoryManager();
        private readonly GitRepositoryManager _gitRepositoryManager = new GitRepositoryManager();
        
        // ChatGPT Desktop support
        private TunnelService? _tunnelService;
        private McpServer? _mcpServerForSse;
        private bool _isChatGptConnected = false;
        
        private Workspace? _currentWorkspace => _workspaceManager.CurrentWorkspace;

        private string _statusText = string.Empty;
        private Brush _statusForeground = Brushes.White;
        private bool _isScanning = false;
        private bool _isLoadingWorkspace = false;
        private int _estimatedTokens = 0;

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public Brush StatusForeground
        {
            get => _statusForeground;
            set { _statusForeground = value; OnPropertyChanged(nameof(StatusForeground)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            FoundFiles = new ObservableCollection<string>();

            inputDirListBox.ItemsSource = _directoryManager.Directories;
            fileListBox.ItemsSource = FoundFiles;
            gitRepoListBox.ItemsSource = _gitRepositoryManager.Repositories;
            projectComboBox.ItemsSource = _workspaceManager.Workspaces;

            // Bind directory count text
            _directoryManager.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DirectoryManager.CountText))
                    directoryCountText.Text = _directoryManager.CountText;
            };

            // Wire up git repository manager events
            _gitRepositoryManager.OnProgress += msg => Dispatcher.Invoke(() => gitStatusText.Text = msg);
            _gitRepositoryManager.OnError += msg => Dispatcher.Invoke(() => 
                UpdateStatus(msg, new SolidColorBrush(Color.FromRgb(233, 69, 96))));

            // Wire up file scanner progress
            _fileScannerService.OnProgress += msg => Dispatcher.Invoke(() => UpdateStatus(msg, Brushes.Gray));

            // Wire up workspace manager events
            _workspaceManager.OnWorkspaceChanged += OnWorkspaceChanged;

            // Initialize MCP connection service
            _mcpConnectionService = new McpConnectionService(App.HandshakePipeName, McpServer.ActivityPipeName);
            _mcpConnectionService.OnConnected += OnMcpConnected;
            _mcpConnectionService.OnDisconnected += OnMcpDisconnected;
            _mcpConnectionService.OnActivity += OnMcpActivity;
            _mcpConnectionService.OnError += OnMcpConnectionError;

            UpdateStatus("Ready", Brushes.Gray);
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadWorkspaceList();

            if (_workspaceManager.Workspaces.Count == 0)
            {
                PromptCreateFirstWorkspace();
            }

            if (Application.Current.Properties.Contains("ConfigHealed"))
            {
                bool healed = (bool)Application.Current.Properties["ConfigHealed"];
                if (healed)
                {
                    string message = _claudeDesktopService.IsClickOnceDeployment()
                        ? "ClickOnce update detected. Updated Claude Desktop config."
                        : "Updated Claude Desktop config to match current installation.";
                    UpdateStatus(message, Brushes.LightGreen);
                }
            }

            RefreshClaudeDesktopStatus();
            _mcpConnectionService.Start();
        }

        #region MCP Connection Events

        private void OnMcpConnected(string workspaceName)
        {
            Dispatcher.Invoke(() =>
            {
                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165)); // AccentSuccess
                connectionStatusText.Text = $"Connected: {workspaceName}";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                stopServerButton.Visibility = Visibility.Visible;

                UpdateStatus($"✓ Claude connected via MCP (workspace: {workspaceName})", Brushes.LightGreen);
            });
        }

        private void OnMcpDisconnected(string workspaceName)
        {
            Dispatcher.Invoke(() =>
            {
                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160)); // Gray
                connectionStatusText.Text = "Disconnected";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                stopServerButton.Visibility = Visibility.Collapsed;

                UpdateStatus($"MCP server disconnected (workspace: {workspaceName})", Brushes.Gray);
            });
        }

        private void OnMcpActivity(string workspaceName, string activity)
        {
            Dispatcher.Invoke(() =>
            {
                // Update connection status since we're receiving activity
                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165)); // AccentSuccess
                connectionStatusText.Text = $"Connected: {workspaceName}";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                stopServerButton.Visibility = Visibility.Visible;

                UpdateStatus($"🔄 [{workspaceName}] {activity}", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            });
        }

        private void OnMcpConnectionError(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus(errorMessage, new SolidColorBrush(Color.FromRgb(255, 193, 7)));
            });
        }

        #endregion

        private void RefreshClaudeDesktopStatus()
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
                var expectedPath = _claudeDesktopService.GetStableExePath();

                if (string.Equals(configuredPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    var activeWorkspace = _workspaceManager.GetActiveWorkspaceName();
                    claudeConfigStatus.Text = $"Ready ✓";
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

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int killed = _mcpConnectionService.KillServerProcesses();

                if (killed == 0)
                {
                    UpdateStatus("No MCP server process found.", Brushes.Gray);
                }
                else
                {
                    UpdateStatus($"Stopped {killed} MCP server process(es). You can now recompile.", Brushes.LightGreen);
                }

                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                connectionStatusText.Text = killed == 0 ? "Not connected" : "Server stopped";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                stopServerButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to stop server: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
        }

        private void ClaudeDownload_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenDownloadPage();
        }

        private void ClaudeAddConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _claudeDesktopService.DeployStableCopy();
                _claudeDesktopService.EnsureConfigured();
                UpdateStatus("Added CodeMerger to Claude Desktop config.", Brushes.LightGreen);
                RefreshClaudeDesktopStatus();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to update config: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
        }

        private void ClaudeOpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenConfigFolder();
        }

        #region ChatGPT Desktop / SSE Transport

        private const int DefaultSsePort = 52780;

        private async void ChatGptStart_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null)
            {
                UpdateStatus("Select a workspace first", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            chatGptStartButton.IsEnabled = false;
            UpdateStatus("Starting ChatGPT server...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));

            try
            {
                // Create and index McpServer for SSE
                _mcpServerForSse = new McpServer();
                _mcpServerForSse.OnLog += msg => Dispatcher.Invoke(() => UpdateStatus(msg, Brushes.Gray));

                var extensions = _currentWorkspace.Extensions
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToList();

                var ignoredDirs = (_currentWorkspace.IgnoredDirectories + ",.git")
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant())
                    .ToHashSet();

                var activeDirectories = _currentWorkspace.InputDirectories
                    .Where(dir => !_currentWorkspace.DisabledDirectories.Contains(dir))
                    .ToList();

                _mcpServerForSse.IndexWorkspace(_currentWorkspace.Name, activeDirectories, extensions, ignoredDirs);

                // Wire up SSE events
                _mcpServerForSse.OnSseClientConnected += sessionId => Dispatcher.Invoke(() =>
                {
                        _isChatGptConnected = true;
                        chatGptConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 163, 127)); // ChatGPT green
                        chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                        chatGptStatusText.Text = "Connected";
                        chatGptStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                        UpdateStatus($"✓ ChatGPT connected (session: {sessionId})", Brushes.LightGreen);
                    });

                _mcpServerForSse.OnSseClientDisconnected += sessionId => Dispatcher.Invoke(() =>
                {
                        _isChatGptConnected = false;
                        chatGptConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                        chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow - running but no client
                        chatGptStatusText.Text = "Running";
                        chatGptStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                        UpdateStatus($"ChatGPT disconnected (session: {sessionId})", Brushes.Gray);
                    });

                // Activity flash when ChatGPT makes requests
                _mcpServerForSse.OnSseMessageReceived += method => Dispatcher.Invoke(async () =>
                {
                    _isChatGptConnected = true; // Message received = connected
                    chatGptConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 255, 150));
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 255, 150));
                    chatGptStatusText.Text = method;
                
                    await Task.Delay(300);
                
                    if (_isChatGptConnected)
                    {
                        chatGptConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                        chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 163, 127));
                        chatGptStatusText.Text = "Connected";
                    }
                });

                // Start HTTP transport (tunnel will provide HTTPS)
                _mcpServerForSse.StartSse(DefaultSsePort, useHttps: false);

                // Update UI to show starting state
                chatGptStatusText.Text = "Starting tunnel...";
                chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow

                // Now start the tunnel
                _tunnelService = new TunnelService();
                _tunnelService.OnLog += msg => Dispatcher.Invoke(() => UpdateStatus(msg, Brushes.Gray));
                _tunnelService.OnDownloadProgress += progress => Dispatcher.Invoke(() =>
                {
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.Value = progress;
                    UpdateStatus($"Downloading cloudflared... {progress}%", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
                    if (progress >= 100)
                    {
                        progressBar.Visibility = Visibility.Collapsed;
                    }
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
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165)); // Green
                    UpdateStatus($"✓ ChatGPT server ready! URL copied to clipboard", Brushes.LightGreen);
                });
                _tunnelService.OnDisconnected += () => Dispatcher.Invoke(() =>
                {
                    // Tunnel disconnected but server may still be running
                    if (_mcpServerForSse != null && _mcpServerForSse.IsSseRunning)
                    {
                        publicUrlTextBox.Text = "Tunnel disconnected - restart server";
                        copyUrlButton.IsEnabled = false;
                        chatGptStatusText.Text = "Tunnel lost";
                        chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(233, 69, 96)); // Red
                        UpdateStatus("Tunnel disconnected. Click Stop then Start again.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                    }
                });
                _tunnelService.OnError += msg => Dispatcher.Invoke(() =>
                {
                    UpdateStatus(msg, new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                    // Don't stop server on tunnel error - let user retry
                    chatGptStatusText.Text = "Tunnel failed";
                    chatGptIndicator.Fill = new SolidColorBrush(Color.FromRgb(233, 69, 96)); // Red
                    chatGptStartButton.IsEnabled = false;
                    chatGptStopButton.Visibility = Visibility.Visible;
                });

                bool tunnelSuccess = await _tunnelService.StartAsync(DefaultSsePort);

                if (tunnelSuccess)
                {
                    // Success - update UI
                    chatGptStartButton.Visibility = Visibility.Collapsed;
                    chatGptStopButton.Visibility = Visibility.Visible;
                }
                else
                {
                    // Tunnel failed - stop everything
                    StopChatGptServer();
                    chatGptStartButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to start: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                StopChatGptServer();
                chatGptStartButton.IsEnabled = true;
            }
        }

        private void ChatGptStop_Click(object sender, RoutedEventArgs e)
        {
            StopChatGptServer();
        }

        private void StopChatGptServer()
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
            chatGptConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
            publicUrlTextBox.Text = "Click 'Start ChatGPT Server' to get URL";
            copyUrlButton.IsEnabled = false;
            progressBar.Visibility = Visibility.Collapsed;

            UpdateStatus("ChatGPT server stopped", Brushes.Gray);
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var url = publicUrlTextBox.Text;
            if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
            {
                Clipboard.SetText(url);
                UpdateStatus($"Copied to clipboard: {url}", Brushes.LightGreen);
            }
        }

        private void TunnelHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(TunnelService.GetInstallInstructions(), "ChatGPT Desktop Setup", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            // Get log file path from the SSE server if available, otherwise use default location
            var logPath = _mcpServerForSse?.LogFilePath ?? 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                    "CodeMerger", "codemerger-mcp.log");
            
            if (File.Exists(logPath))
            {
                // Open in default text editor
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show($"Log file not found at:\n{logPath}\n\nThe log is created when the MCP server starts.", 
                    "Log File", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _mcpConnectionService.Dispose();
            _tunnelService?.Dispose();
            _mcpServerForSse?.Stop();
        }

        private void LoadWorkspaceList()
        {
            _workspaceManager.LoadWorkspaces();

            if (_workspaceManager.Workspaces.Count > 0)
            {
                var defaultWorkspace = _workspaceManager.GetDefaultWorkspace();
                if (defaultWorkspace != null)
                {
                    projectComboBox.SelectedItem = defaultWorkspace;
                }
            }
        }

        private void PromptCreateFirstWorkspace()
        {
            MessageBox.Show("Welcome! Create your first workspace to get started.", "CodeMerger", MessageBoxButton.OK, MessageBoxImage.Information);
            NewWorkspace_Click(null, null);

            if (_workspaceManager.Workspaces.Count == 0)
            {
                Application.Current.Shutdown();
            }
        }

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (projectComboBox.SelectedItem is Workspace selected)
            {
                _workspaceManager.SelectWorkspace(selected);
            }
        }

        private async void OnWorkspaceChanged(Workspace? workspace)
        {
            if (workspace == null) return;

            string workspaceFolder = _workspaceManager.GetWorkspaceFolder(workspace.Name);
            _gitRepositoryManager.SetWorkspaceFolder(workspaceFolder);

            await LoadWorkspaceDataAsync(workspace);

            EnsureClaudeConfig();
            RefreshClaudeDesktopStatus();
        }

        private void EnsureClaudeConfig()
        {
            try
            {
                _claudeDesktopService.DeployStableCopy();
                _claudeDesktopService.EnsureConfigured();
            }
            catch
            {
                // Silently fail
            }
        }

        private async Task LoadWorkspaceDataAsync(Workspace workspace)
        {
            if (workspace == null) return;

            _isLoadingWorkspace = true;

            try
            {
                _directoryManager.Load(workspace.InputDirectories, workspace.DisabledDirectories);
                _gitRepositoryManager.Load(workspace.ExternalRepositories);

                extensionsTextBox.Text = workspace.Extensions;
                ignoredDirsTextBox.Text = workspace.IgnoredDirectories;

                string workspaceFolder = _workspaceManager.GetWorkspaceFolder(workspace.Name);
                outputFileTextBox.Text = workspaceFolder;

                await _gitRepositoryManager.UpdateAllAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading workspace: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
            finally
            {
                _isLoadingWorkspace = false;
                await ScanFilesAsync();
            }
        }

        private void NewWorkspace_Click(object? sender, RoutedEventArgs? e)
        {
            var dialog = new InputDialog("New Workspace", "Enter workspace name:");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string name = dialog.ResponseText.Trim();

                var workspace = _workspaceManager.CreateWorkspace(name);
                if (workspace == null)
                {
                    MessageBox.Show("A workspace with that name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                projectComboBox.SelectedItem = workspace;
            }
        }

        private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null) return;

            var dialog = new InputDialog("Rename Workspace", "Enter new name:", _currentWorkspace.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string newName = dialog.ResponseText.Trim();

                if (_workspaceManager.RenameWorkspace(_currentWorkspace.Name, newName))
                {
                    // Reload to refresh the ComboBox display
                    var renamed = _workspaceManager.Workspaces.FirstOrDefault(w => w.Name == newName);
                    if (renamed != null)
                    {
                        projectComboBox.SelectedItem = renamed;
                    }
                }
                else
                {
                    MessageBox.Show("Could not rename workspace. Name may already exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null) return;

            var result = MessageBox.Show(
                $"Delete workspace '{_currentWorkspace.Name}' and all its output files?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _workspaceManager.DeleteWorkspace(_currentWorkspace.Name);

                if (_workspaceManager.Workspaces.Count == 0)
                {
                    PromptCreateFirstWorkspace();
                }
                else
                {
                    projectComboBox.SelectedItem = _workspaceManager.Workspaces.FirstOrDefault();
                }
            }
        }

        private void SaveCurrentWorkspace()
        {
            if (_currentWorkspace == null || _isLoadingWorkspace) return;

            _currentWorkspace.InputDirectories = _directoryManager.GetAllPaths().ToList();
            _currentWorkspace.DisabledDirectories = _directoryManager.GetDisabledPaths().ToList();
            _currentWorkspace.Extensions = extensionsTextBox.Text;
            _currentWorkspace.IgnoredDirectories = ignoredDirsTextBox.Text;
            _currentWorkspace.ExternalRepositories = _gitRepositoryManager.ToList();

            _workspaceManager.SaveCurrent();
        }

        private async void AddDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                string? folderPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    _directoryManager.Add(folderPath);
                    SaveCurrentWorkspace();
                    await ScanFilesAsync();
                }
            }
        }

        private async void RemoveDirectory_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = inputDirListBox.SelectedItems.Cast<SelectableItem>().ToList();
            foreach (var item in selectedItems)
            {
                _directoryManager.Remove(item);
            }
            
            if (selectedItems.Count > 0)
            {
                SaveCurrentWorkspace();
                await ScanFilesAsync();
            }
        }

        private async void SelectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            _directoryManager.SelectAll();
            SaveCurrentWorkspace();
            await ScanFilesAsync();
        }

        private async void DeselectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            _directoryManager.DeselectAll();
            SaveCurrentWorkspace();
            await ScanFilesAsync();
        }

        #region Git Repository Management

        private async void AddGitRepo_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null)
            {
                UpdateStatus("No workspace selected.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            string url = gitUrlTextBox.Text.Trim();
            SetUIState(false);

            if (await _gitRepositoryManager.AddRepositoryAsync(url))
            {
                SaveCurrentWorkspace();
                gitUrlTextBox.Text = "https://github.com/user/repo";
                await ScanFilesAsync();
            }

            SetUIState(true);
        }

        private async void RefreshGitRepo_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var repo = button?.DataContext as ExternalRepository;
            if (repo == null) return;

            SetUIState(false);

            if (await _gitRepositoryManager.RefreshRepositoryAsync(repo))
            {
                SaveCurrentWorkspace();
                await ScanFilesAsync();
            }

            SetUIState(true);
        }

        private async void RemoveGitRepo_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var repo = button?.DataContext as ExternalRepository;
            if (repo == null) return;

            var result = MessageBox.Show(
                $"Remove '{repo.Name}' and delete local files?",
                "Remove Repository",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_gitRepositoryManager.RemoveRepository(repo))
                {
                    SaveCurrentWorkspace();
                    await ScanFilesAsync();
                }
            }
        }

        private async void GitRepoCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingWorkspace) return;
            SaveCurrentWorkspace();
            await ScanFilesAsync();
        }

        #endregion

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null) return;

            string folder = _workspaceManager.GetCurrentWorkspaceFolder();

            if (Directory.Exists(folder))
            {
                Process.Start("explorer.exe", folder);
            }
            else
            {
                MessageBox.Show("Output folder does not exist yet.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void UpdateRecommendation()
        {
            if (_estimatedTokens < 100000)
            {
                recommendationBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                recommendationBanner.Visibility = Visibility.Visible;
                recommendationText.Text = $"Workspace size: ~{_estimatedTokens:N0} tokens. Claude will access files dynamically via MCP.";
            }
        }

        // Keep for compatibility - hidden in UI
        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            // Legacy feature - MCP now handles file access dynamically
            UpdateStatus("Legacy merge feature is no longer needed. Use MCP.", Brushes.Gray);
        }

        // Keep for compatibility - hidden in UI
        private void McpServer_Click(object sender, RoutedEventArgs e)
        {
            // MCP is now managed by Claude Desktop automatically
            UpdateStatus("MCP is managed automatically by Claude Desktop.", Brushes.Gray);
        }

        private async Task ScanFilesAsync()
        {
            if (!IsLoaded || _isScanning) return;
            _isScanning = true;

            FoundFiles.Clear();

            var selectedDirs = _directoryManager.GetSelectedPaths().ToList();
            var enabledRepos = _gitRepositoryManager.Repositories.Where(r => r.IsEnabled).ToList();

            if (selectedDirs.Count == 0 && enabledRepos.Count == 0)
            {
                UpdateStatus("Add directories or enable repositories to get started.", Brushes.Gray);
                _isScanning = false;
                UpdateRecommendation();
                return;
            }

            UpdateStatus("Scanning...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            SetUIState(false);

            try
            {
                var result = await _fileScannerService.ScanAsync(
                    selectedDirs,
                    enabledRepos,
                    _gitRepositoryManager.GitService,
                    extensionsTextBox.Text,
                    ignoredDirsTextBox.Text);

                foreach (var file in result.Files)
                {
                    FoundFiles.Add(file);
                }

                _estimatedTokens = result.EstimatedTokens;
                UpdateStatus($"Found {result.Files.Count} files (~{_estimatedTokens:N0} tokens)", Brushes.Gray);
                UpdateRecommendation();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Scan error: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
            finally
            {
                SetUIState(true);
                _isScanning = false;
            }
        }

        private async void Filters_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            SaveCurrentWorkspace();
            await ScanFilesAsync();
        }

        private async void DirectoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isLoadingWorkspace) return;
            _directoryManager.NotifySelectionChanged();
            SaveCurrentWorkspace();
            await ScanFilesAsync();
        }

        private void UpdateStatus(string message, Brush color)
        {
            StatusText = message;
            StatusForeground = color;
        }

        private void SetUIState(bool isEnabled)
        {
            mainGrid.IsEnabled = isEnabled;
            progressBar.Visibility = isEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (isEnabled)
            {
                fileStatusLabel.Text = "";
                progressBar.Value = 0;
            }
        }
    }
}
