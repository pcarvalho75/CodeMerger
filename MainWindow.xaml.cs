using CodeMerger.Controls;
using CodeMerger.Models;
using CodeMerger.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace CodeMerger
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<string> FoundFiles { get; set; }

        private readonly WorkspaceManager _workspaceManager = new WorkspaceManager();
        private readonly ClaudeDesktopService _claudeDesktopService = new ClaudeDesktopService();
        private readonly McpConnectionService _mcpConnectionService;
        private readonly FileScannerService _fileScannerService = new FileScannerService();
        private readonly DirectoryManager _directoryManager = new DirectoryManager();
        private readonly GitRepositoryManager _gitRepositoryManager = new GitRepositoryManager();
        private readonly WorkspaceSettingsService _settingsService = null!;
        private readonly BackupCleanupService _backupCleanupService = new BackupCleanupService();
        private readonly AppState _appState = new();
        
        // MCP Session statistics
        private readonly McpSessionStats _sessionStats = new();
        private System.Windows.Threading.DispatcherTimer? _statsUpdateTimer;
        
        // Activity log
        private bool _isSwitchingFromMcp = false; // Prevent double-load when MCP switches workspace
        private bool _isExiting = false; // True only when exiting via tray "Exit" menu
        
        private Workspace? _currentWorkspace => _workspaceManager.CurrentWorkspace;

        private bool _isScanning = false;
        private bool _isLoadingWorkspace = false;
        private int _estimatedTokens = 0;

        public MainWindow()
        {
            InitializeComponent();
            FoundFiles = new ObservableCollection<string>();

            // Initialize settings service
            _settingsService = new WorkspaceSettingsService(msg => Debug.WriteLine($"[Settings] {msg}"));

            foundFilesTab.SetSource(FoundFiles);
            headerBar.SetWorkspaceSource(_workspaceManager.Workspaces);
            headerBar.Initialize(_appState);

            // Wire up SourceDirectoriesTab
            sourceDirectoriesTab.Initialize(_directoryManager, _gitRepositoryManager);
            sourceDirectoriesTab.SaveAndScanRequested += async () =>
            {
                SaveCurrentWorkspace();
                await ScanFilesAsync();
                if (_mcpConnectionService?.IsConnected == true)
                    _mcpConnectionService.SendCommand("RESYNC");
            };
            sourceDirectoriesTab.UIStateRequested += (s, enabled) => SetUIState(enabled);

            // Wire up git repository manager error (progress is handled inside SourceDirectoriesTab)
            _gitRepositoryManager.OnError += msg => Dispatcher.Invoke(() => 
                UpdateStatus(msg, new SolidColorBrush(Color.FromRgb(233, 69, 96))));

            // Wire up LessonsTab
            lessonsTab.Initialize(() => this);
            lessonsTab.StatusUpdate += (s, msg) => UpdateStatus(msg, Brushes.OrangeRed);

            // Wire up LLMs tab
            llmsTab.Initialize(_claudeDesktopService, () => _currentWorkspace, _appState);
            llmsTab.StatusUpdate += (s, msg) => UpdateStatus(msg, Brushes.LightGreen);
            llmsTab.ProgressUpdate += (s, args) =>
            {
                statusBar.SetProgress(args.Progress, args.Visible);
            };

            // Wire up file scanner progress
            _fileScannerService.OnProgress += msg => Dispatcher.Invoke(() => UpdateStatus(msg, Brushes.Gray));

            // Wire up workspace manager events
            _workspaceManager.OnWorkspaceChanged += OnWorkspaceChanged;

            // Initialize MCP connection service
            _mcpConnectionService = new McpConnectionService(App.HandshakePipeName, McpServer.ActivityPipeName);
            _mcpConnectionService.OnConnected += OnMcpConnected;
            _mcpConnectionService.OnDisconnected += OnMcpDisconnected;
            _mcpConnectionService.OnActivity += OnMcpActivity;
            _mcpConnectionService.OnActivityParsed += OnMcpActivityParsed;
            _mcpConnectionService.OnError += OnMcpConnectionError;

            // Initialize stats update timer
            _statsUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            _statsUpdateTimer.Interval = TimeSpan.FromSeconds(3); // Update every 3 seconds
            _statsUpdateTimer.Tick += StatsUpdateTimer_Tick;
            _statsUpdateTimer.Start();

            // Wire up ActivityStrip events
            activityStrip.Initialize(_appState);
            activityStrip.TimeoutThresholdSeconds = _settingsService?.CurrentSettings?.TimeoutThresholdSeconds ?? 45;
            activityStrip.ResetStatsClicked += (s, e) =>
            {
                _sessionStats.Reset();
                activityStrip.UpdateStatsDisplay(_sessionStats);
                UpdateStatus("Session statistics reset", Brushes.Gray);
            };
            activityStrip.TimeoutDetected += (s, elapsedSeconds) =>
            {
                _sessionStats.RecordTimeout();
                activityLogTab.AddEntry(ActivityLogType.Timeout, "", 0, $"Claude hasn't responded in {elapsedSeconds}s");
                activityStrip.UpdateStatsDisplay(_sessionStats);
                activityLogTab.UpdateDisplay();
            };

            // Wire up ParametersTab
            parametersTab.Initialize(_settingsService!, _backupCleanupService, () => _currentWorkspace);
            parametersTab.SetConnectionCheck(() => _mcpConnectionService.IsConnected);
            parametersTab.TimeoutThresholdChanged += (s, seconds) =>
            {
                activityStrip.TimeoutThresholdSeconds = seconds;
            };
            parametersTab.RestartRequested += (s, e) =>
            {
                try
                {
                    int killed = _mcpConnectionService.KillServerProcesses();
                    parametersTab.HideRestartBanner();

                    if (killed > 0)
                        UpdateStatus("MCP server stopped. Settings will apply on next connection.", Brushes.LightGreen);
                    else
                        UpdateStatus("Settings saved. Will apply on next MCP connection.", Brushes.Gray);

                    _appState.ClaudeState = ClaudeState.Restarting;
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Failed to restart: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                }
            };
            parametersTab.StatusUpdate += (s, message) =>
            {
                // Determine color based on message content
                var brush = message.Contains("freed") ? Brushes.LightGreen
                          : message.Contains("Auto-cleanup") ? Brushes.LightYellow
                          : Brushes.Gray;
                UpdateStatus(message, brush);
            };

            // Wire up ActivityLogTab
            activityLogTab.Initialize(_sessionStats);
            activityLogTab.StatusUpdate += (s, msg) => UpdateStatus(msg, Brushes.LightGreen);

            UpdateStatus("Ready", Brushes.Gray);
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Register to receive the "bring to front" message from second instances
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);

            // Wire up HeaderBar events
            headerBar.WorkspaceSelectionChanged += (s, workspace) =>
            {
                if (!_isSwitchingFromMcp)
                    _workspaceManager.SelectWorkspace(workspace);
            };
            headerBar.NewWorkspaceClicked += (s, e) => NewWorkspace_Click();
            headerBar.RenameWorkspaceClicked += (s, e) => RenameWorkspace_Click();
            headerBar.DeleteWorkspaceClicked += (s, e) => DeleteWorkspace_Click();
            headerBar.StopServerClicked += (s, e) => StopServer_Click();

            // Set tray icon from the running executable
            try
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                if (icon != null) trayIcon.Icon = icon;
            }
            catch
            {
                // Fall back to no icon — tray still works
            }

            LoadWorkspaceList();

            if (_workspaceManager.Workspaces.Count == 0)
            {
                PromptCreateFirstWorkspace();
            }

            if (Application.Current.Properties.Contains("ConfigHealed") &&
                Application.Current.Properties["ConfigHealed"] is bool healed && healed)
            {
                string message = _claudeDesktopService.IsDebugRun()
                    ? "Debug run detected. Claude Desktop config updated to use debug exe."
                    : _claudeDesktopService.IsClickOnceDeployment()
                        ? "ClickOnce update detected. Updated Claude Desktop config."
                        : "Updated Claude Desktop config to match current installation.";
                UpdateStatus(message, Brushes.LightGreen);
            }

            llmsTab.RefreshClaudeDesktopStatus();
            _mcpConnectionService.Start();

            // Background sync community lessons (fire-and-forget, non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    var settings = Models.CommunityLessonSettings.Load();
                    if (!settings.CommunityLessonsEnabled)
                    {
                        Dispatcher.Invoke(() => lessonsTab.RefreshLessons());
                        return;
                    }
                    var lessonService = new LessonService();
                    var syncService = new CommunityLessonSyncService(lessonService);
                    var (synced, count, message) = await syncService.SyncIfStaleAsync(ttlHours: settings.SyncIntervalHours);
                    Dispatcher.Invoke(() =>
                    {
                        if (synced)
                            UpdateStatus($"Community lessons synced: {count} lessons", Brushes.LightBlue);
                        lessonsTab.RefreshLessons();
                    });
                }
                catch
                {
                    Dispatcher.Invoke(() => lessonsTab.RefreshLessons());
                }
            });
        }

        #region MCP Connection Events

        private void OnMcpConnected(string workspaceName)
        {
            Dispatcher.Invoke(() =>
            {
                _appState.SetClaudeConnected(workspaceName);

                activityLogTab.AddEntry(ActivityLogType.Connection, "", 0, $"Claude connected — workspace: {workspaceName}");
                UpdateStatus($"✓ Claude connected via MCP (workspace: {workspaceName})", Brushes.LightGreen);
                UpdateTrayTooltip();
            });
        }

        private void OnMcpDisconnected(string workspaceName)
        {
            Dispatcher.Invoke(() =>
            {
                _appState.SetClaudeDisconnected();

                activityLogTab.AddEntry(ActivityLogType.Disconnection, "", 0, $"Claude disconnected — workspace: {workspaceName}");
                UpdateStatus($"MCP server disconnected (workspace: {workspaceName})", Brushes.Gray);
                UpdateTrayTooltip();
            });
        }

        private void OnMcpActivity(string workspaceName, string activity)
        {
            Dispatcher.Invoke(() =>
            {
                // Update connection status since we're receiving activity
                _appState.SetClaudeConnected(workspaceName);

                UpdateStatus($"🔄 [{workspaceName}] {activity}", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            });
        }

        private void OnMcpActivityParsed(string workspaceName, McpActivityMessage activityMsg)
        {
            Dispatcher.Invoke(() =>
            {
                // Update connection status
                _appState.SetClaudeConnected(workspaceName);

                // Update MCP Activity UI based on state
                switch (activityMsg.Type)
                {
                    case McpActivityType.STARTED:
                        activityStrip.SetProcessing(activityMsg.ToolName);
                        break;

                    case McpActivityType.COMPLETED:
                        activityStrip.SetCompleted(activityMsg.ToolName, activityMsg.Details);
                    
                        // Record statistics - parse duration from Details (e.g., "12ms")
                        long durationMs = 0;
                        if (activityMsg.Details.EndsWith("ms") && 
                            long.TryParse(activityMsg.Details.Replace("ms", ""), out durationMs))
                        {
                            _sessionStats.RecordToolCall(activityMsg.ToolName, durationMs);
                            activityStrip.UpdateStatsDisplay(_sessionStats);
                        }
                    
                        // Add to activity log
                        activityLogTab.AddEntry(ActivityLogType.ToolCall, activityMsg.ToolName, durationMs, $"Completed in {activityMsg.Details}");
                        break;

                    case McpActivityType.ERROR:
                        activityStrip.SetError(activityMsg.ToolName, activityMsg.Details);
                    
                        // Record error statistics
                        _sessionStats.RecordError();
                        activityStrip.UpdateStatsDisplay(_sessionStats);
                    
                        // Add to activity log
                        activityLogTab.AddEntry(ActivityLogType.Error, activityMsg.ToolName, 0, activityMsg.Details);
                        break;

                    case McpActivityType.WORKSPACE_SWITCHED:
                        // Sync the GUI dropdown to match the workspace that MCP switched to
                        var switchedName = activityMsg.ToolName; // ToolName field carries the workspace name
                        activityLogTab.AddEntry(ActivityLogType.WorkspaceSwitch, "", 0, $"Workspace switched to: {switchedName}");
                        
                        // Find the workspace in the combo box and select it without triggering a reload
                        var matchingWorkspace = _workspaceManager.Workspaces
                            .FirstOrDefault(w => w.Name.Equals(switchedName, StringComparison.OrdinalIgnoreCase));
                        
                        if (matchingWorkspace != null && headerBar.SelectedWorkspace != matchingWorkspace)
                        {
                            _isSwitchingFromMcp = true;
                            headerBar.SwitchWorkspaceFromMcp(matchingWorkspace);
                            _isSwitchingFromMcp = false;
                        }

                        // Refresh file list and workspace data to match the new workspace
                        if (matchingWorkspace != null)
                            _workspaceManager.SelectWorkspace(matchingWorkspace);
                        
                        // Update connection status text
                        _appState.SetClaudeConnected(switchedName);
                        UpdateStatus($"🔄 Workspace switched to: {switchedName}", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
                        break;
                }
                
                // Update chart after any activity
                activityLogTab.UpdateDisplay();
            });
        }

        private void OnMcpConnectionError(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus(errorMessage, new SolidColorBrush(Color.FromRgb(255, 193, 7)));
            });
        }

        private void StatsUpdateTimer_Tick(object? sender, EventArgs e)
        {
            activityStrip.UpdateStatsDisplay(_sessionStats);
            activityLogTab.UpdateDisplay();
        }

        #endregion


        private void StopServer_Click()
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

                _appState.SetClaudeDisconnected();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to stop server: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
        }


        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WM_SHOWME)
            {
                ShowAndActivate();
                handled = true;
            }
            return IntPtr.Zero;
        }

        #region System Tray

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
        {
            ShowAndActivate();
        }

        private void TrayOpen_Click(object sender, RoutedEventArgs e)
        {
            ShowAndActivate();
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void ShowAndActivate()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            trayIcon.Dispose();
            Close();
        }

        private void UpdateTrayTooltip()
        {
            var text = headerBar.ConnectionStatusText;
            trayIcon.ToolTipText = text.StartsWith("Connected")
                ? $"CodeMerger — {text}"
                : "CodeMerger — Idle";
        }

        #endregion

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isExiting)
            {
                // Hide to tray instead of closing
                e.Cancel = true;
                Hide();
                return;
            }

            // Real exit — clean up resources
            activityStrip.StopTimer();
            _statsUpdateTimer?.Stop();
            _mcpConnectionService.Dispose();
            llmsTab.Cleanup();
        }

        private void LoadWorkspaceList()
        {
            _workspaceManager.LoadWorkspaces();

            if (_workspaceManager.Workspaces.Count > 0)
            {
                var defaultWorkspace = _workspaceManager.GetDefaultWorkspace();
                if (defaultWorkspace != null)
                {
                    headerBar.SelectWorkspace(defaultWorkspace);
                }
            }
        }

        private void PromptCreateFirstWorkspace()
        {
            MessageBox.Show("Welcome! Create your first workspace to get started.", "CodeMerger", MessageBoxButton.OK, MessageBoxImage.Information);
            NewWorkspace_Click();

            if (_workspaceManager.Workspaces.Count == 0)
            {
                Application.Current.Shutdown();
            }
        }

        private async void OnWorkspaceChanged(Workspace? workspace)
        {
            if (workspace == null) return;

            string workspaceFolder = _workspaceManager.GetWorkspaceFolder(workspace.Name);
            _gitRepositoryManager.SetWorkspaceFolder(workspaceFolder);

            // Load workspace settings
            var settings = parametersTab.LoadSettings(workspaceFolder);
            activityStrip.TimeoutThresholdSeconds = settings.TimeoutThresholdSeconds;

            await LoadWorkspaceDataAsync(workspace);

            EnsureClaudeConfig();
            llmsTab.RefreshClaudeDesktopStatus();
        }

        private void EnsureClaudeConfig()
        {
            try
            {
                _claudeDesktopService.SelfHeal();
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

                sourceDirectoriesTab.LoadWorkspace(workspace.Extensions, workspace.IgnoredDirectories, workspace.ExternalRepositories);

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

        private void NewWorkspace_Click()
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

                headerBar.SelectWorkspace(workspace);
            }
        }

        private void RenameWorkspace_Click()
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
                        headerBar.SelectWorkspace(renamed);
                    }
                }
                else
                {
                    MessageBox.Show("Could not rename workspace. Name may already exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void DeleteWorkspace_Click()
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
                    headerBar.SelectWorkspace(_workspaceManager.Workspaces.FirstOrDefault());
                }
            }
        }

        private void SaveCurrentWorkspace()
        {
            if (_currentWorkspace == null || _isLoadingWorkspace) return;

            _currentWorkspace.InputDirectories = _directoryManager.GetAllPaths().ToList();
            _currentWorkspace.DisabledDirectories = _directoryManager.GetDisabledPaths().ToList();
            _currentWorkspace.Extensions = sourceDirectoriesTab.Extensions;
            _currentWorkspace.IgnoredDirectories = sourceDirectoriesTab.IgnoredDirectories;
            _currentWorkspace.ExternalRepositories = _gitRepositoryManager.ToList();

            _workspaceManager.SaveCurrent();
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


        private async Task ScanFilesAsync()
        {
            if (!IsLoaded || _isScanning) return;
            _isScanning = true;

            FoundFiles.Clear();
            foundFilesTab.UpdateHeader(null);

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
                    sourceDirectoriesTab.Extensions,
                    sourceDirectoriesTab.IgnoredDirectories);

                foreach (var file in result.Files)
                {
                    FoundFiles.Add(file);
                }

                _estimatedTokens = result.EstimatedTokens;
                foundFilesTab.UpdateHeader(result.Files.Count);
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

        private void UpdateStatus(string message, Brush color)
        {
            statusBar.UpdateStatus(message, color);
        }

        private void SetUIState(bool isEnabled)
        {
            mainGrid.IsEnabled = isEnabled;
            statusBar.SetScanningState(isEnabled);
        }

    }
}
