using CodeMerger.Models;
using CodeMerger.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<string> FoundFiles { get; set; }
        public ObservableCollection<ExternalRepository> ExternalRepositories { get; set; }

        private readonly WorkspaceService _workspaceService = new WorkspaceService();
        private readonly CodeAnalyzer _codeAnalyzer = new CodeAnalyzer();
        private readonly IndexGenerator _indexGenerator = new IndexGenerator();
        private readonly McpServer _mcpServer = new McpServer();
        private readonly ClaudeDesktopService _claudeDesktopService = new ClaudeDesktopService();
        private readonly McpConnectionService _mcpConnectionService;
        private readonly FileScannerService _fileScannerService = new FileScannerService();
        private readonly DirectoryManager _directoryManager = new DirectoryManager();
        
        private Workspace? _currentWorkspace;
        private GitService? _gitService;

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
            ExternalRepositories = new ObservableCollection<ExternalRepository>();

            inputDirListBox.ItemsSource = _directoryManager.Directories;
            fileListBox.ItemsSource = FoundFiles;
            gitRepoListBox.ItemsSource = ExternalRepositories;

            // Bind directory count text
            _directoryManager.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DirectoryManager.CountText))
                    directoryCountText.Text = _directoryManager.CountText;
            };

            _mcpServer.OnLog += OnMcpLog;

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

            if (projectComboBox.Items.Count == 0)
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
                var currentPath = _claudeDesktopService.GetCurrentExePath();

                if (string.Equals(configuredPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    var activeWorkspace = _workspaceService.GetActiveWorkspace();
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
                var exePath = _claudeDesktopService.GetCurrentExePath();
                _claudeDesktopService.EnsureConfigured(exePath);
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

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _mcpConnectionService.Dispose();

            if (_gitService != null)
            {
                _gitService.OnProgress -= OnGitProgress;
            }
        }

        private void OnMcpLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus(message, Brushes.LightGreen);
            });
        }

        private void LoadWorkspaceList()
        {
            var workspaces = _workspaceService.LoadAllWorkspaces();
            projectComboBox.ItemsSource = workspaces;

            if (workspaces.Count > 0)
            {
                var activeWorkspace = _workspaceService.GetActiveWorkspace();
                var workspaceToSelect = workspaces.Find(w => w.Name == activeWorkspace) ?? workspaces[0];
                projectComboBox.SelectedItem = workspaceToSelect;
            }
        }

        private void PromptCreateFirstWorkspace()
        {
            MessageBox.Show("Welcome! Create your first workspace to get started.", "CodeMerger", MessageBoxButton.OK, MessageBoxImage.Information);
            NewWorkspace_Click(null, null);

            if (projectComboBox.Items.Count == 0)
            {
                Application.Current.Shutdown();
            }
        }

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (projectComboBox.SelectedItem is Workspace selected)
            {
                if (_gitService != null)
                {
                    _gitService.OnProgress -= OnGitProgress;
                }

                _currentWorkspace = selected;
                _workspaceService.SetActiveWorkspace(_currentWorkspace.Name);

                string workspaceFolder = _workspaceService.GetWorkspaceFolder(_currentWorkspace.Name);
                _gitService = new GitService(workspaceFolder);
                _gitService.OnProgress += OnGitProgress;

                LoadWorkspaceData(_currentWorkspace);

                EnsureClaudeConfig();
                RefreshClaudeDesktopStatus();
            }
        }

        private void OnGitProgress(string message)
        {
            Dispatcher.Invoke(() =>
            {
                gitStatusText.Text = message;
            });
        }

        private void EnsureClaudeConfig()
        {
            try
            {
                var exePath = _claudeDesktopService.GetCurrentExePath();
                _claudeDesktopService.EnsureConfigured(exePath);
            }
            catch
            {
                // Silently fail
            }
        }

        private async void LoadWorkspaceData(Workspace workspace)
        {
            if (workspace == null) return;

            _isLoadingWorkspace = true;

            try
            {
                _directoryManager.Load(workspace.InputDirectories, workspace.DisabledDirectories);

                ExternalRepositories.Clear();
                foreach (var repo in workspace.ExternalRepositories)
                {
                    ExternalRepositories.Add(repo);
                }

                extensionsTextBox.Text = workspace.Extensions;
                ignoredDirsTextBox.Text = workspace.IgnoredDirectories;

                string workspaceFolder = _workspaceService.GetWorkspaceFolder(workspace.Name);
                outputFileTextBox.Text = workspaceFolder;

                await UpdateExternalReposAsync();
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

        private async Task UpdateExternalReposAsync()
        {
            if (_gitService == null || ExternalRepositories.Count == 0) return;

            foreach (var repo in ExternalRepositories.Where(r => r.IsEnabled))
            {
                try
                {
                    gitStatusText.Text = $"Updating {repo.Name}...";
                    await _gitService.CloneOrPullAsync(repo.Url);
                    repo.LastUpdated = DateTime.Now;
                }
                catch (Exception ex)
                {
                    gitStatusText.Text = $"Failed to update {repo.Name}: {ex.Message}";
                }
            }

            gitStatusText.Text = ExternalRepositories.Count > 0
                ? $"{ExternalRepositories.Count} repository(s) loaded"
                : "";
        }

        private void NewWorkspace_Click(object? sender, RoutedEventArgs? e)
        {
            var dialog = new InputDialog("New Workspace", "Enter workspace name:");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string name = dialog.ResponseText.Trim();

                if (_workspaceService.WorkspaceExists(name))
                {
                    MessageBox.Show("A workspace with that name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var workspace = new Workspace { Name = name };
                _workspaceService.SaveWorkspace(workspace);

                LoadWorkspaceList();
                projectComboBox.SelectedItem = ((List<Workspace>)projectComboBox.ItemsSource).Find(w => w.Name == name);
            }
        }

        private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null) return;

            var dialog = new InputDialog("Rename Workspace", "Enter new name:", _currentWorkspace.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string newName = dialog.ResponseText.Trim();

                if (_workspaceService.RenameWorkspace(_currentWorkspace.Name, newName))
                {
                    LoadWorkspaceList();
                    projectComboBox.SelectedItem = ((List<Workspace>)projectComboBox.ItemsSource).Find(w => w.Name == newName);
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
                _workspaceService.DeleteWorkspace(_currentWorkspace.Name);
                _currentWorkspace = null;

                LoadWorkspaceList();

                if (projectComboBox.Items.Count == 0)
                {
                    PromptCreateFirstWorkspace();
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
            _currentWorkspace.ExternalRepositories = ExternalRepositories.ToList();

            _workspaceService.SaveWorkspace(_currentWorkspace);
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
            if (_gitService == null || _currentWorkspace == null)
            {
                UpdateStatus("No workspace selected.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            string url = gitUrlTextBox.Text.Trim();

            if (!GitService.IsValidGitUrl(url))
            {
                UpdateStatus("Invalid Git URL. Use https://github.com/user/repo format.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            if (ExternalRepositories.Any(r => r.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                UpdateStatus("Repository already added.", new SolidColorBrush(Color.FromRgb(255, 193, 7)));
                return;
            }

            SetUIState(false);
            gitStatusText.Text = "Cloning repository...";

            try
            {
                var repo = await _gitService.CloneOrPullAsync(url);
                ExternalRepositories.Add(repo);
                SaveCurrentWorkspace();

                gitStatusText.Text = $"Cloned {repo.Name} ({repo.Branch})";
                gitUrlTextBox.Text = "https://github.com/user/repo";

                await ScanFilesAsync();
            }
            catch (Exception ex)
            {
                gitStatusText.Text = $"Clone failed: {ex.Message}";
                UpdateStatus($"Failed to clone: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
            finally
            {
                SetUIState(true);
            }
        }

        private async void RefreshGitRepo_Click(object sender, RoutedEventArgs e)
        {
            if (_gitService == null) return;

            var button = sender as Button;
            var repo = button?.DataContext as ExternalRepository;
            if (repo == null) return;

            SetUIState(false);
            gitStatusText.Text = $"Updating {repo.Name}...";

            try
            {
                await _gitService.CloneOrPullAsync(repo.Url);
                repo.LastUpdated = DateTime.Now;
                SaveCurrentWorkspace();

                gitStatusText.Text = $"Updated {repo.Name}";
                await ScanFilesAsync();
            }
            catch (Exception ex)
            {
                gitStatusText.Text = $"Update failed: {ex.Message}";
            }
            finally
            {
                SetUIState(true);
            }
        }

        private async void RemoveGitRepo_Click(object sender, RoutedEventArgs e)
        {
            if (_gitService == null) return;

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
                try
                {
                    _gitService.DeleteRepository(repo);
                    ExternalRepositories.Remove(repo);
                    SaveCurrentWorkspace();

                    gitStatusText.Text = $"Removed {repo.Name}";
                    await ScanFilesAsync();
                }
                catch (Exception ex)
                {
                    gitStatusText.Text = $"Remove failed: {ex.Message}";
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

            string folder = _workspaceService.GetWorkspaceFolder(_currentWorkspace.Name);

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
        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (FoundFiles.Count == 0)
            {
                UpdateStatus("No files to merge.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            if (_currentWorkspace == null)
            {
                UpdateStatus("No workspace selected.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            string workspaceFolder = _workspaceService.GetWorkspaceFolder(_currentWorkspace.Name);
            var filesToMerge = FoundFiles.ToList();

            SetUIState(false);
            UpdateStatus("Analyzing files...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            progressBar.Value = 0;
            progressBar.Maximum = filesToMerge.Count + 2;

            try
            {
                var fileAnalyses = new List<FileAnalysis>();
                var selectedDirs = _directoryManager.GetSelectedPaths().ToList();

                var enabledRepos = ExternalRepositories.Where(r => r.IsEnabled).ToList();
                foreach (var repo in enabledRepos)
                {
                    selectedDirs.Add(repo.LocalPath);
                }

                await Task.Run(() =>
                {
                    for (int i = 0; i < filesToMerge.Count; i++)
                    {
                        var file = filesToMerge[i];
                        var baseDir = selectedDirs.FirstOrDefault(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
                        if (baseDir == null) continue;

                        var analysis = _codeAnalyzer.AnalyzeFile(file, baseDir);
                        fileAnalyses.Add(analysis);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            progressBar.Value = i + 1;
                            fileStatusLabel.Text = $"Analyzing: {analysis.FileName}";
                        });
                    }
                });

                UpdateStatus("Creating chunks...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
                var chunkManager = new ChunkManager(150000);
                var chunks = chunkManager.CreateChunks(fileAnalyses);

                var workspaceAnalysis = _indexGenerator.BuildWorkspaceAnalysis(_currentWorkspace.Name, fileAnalyses, chunks);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressBar.Value = filesToMerge.Count + 1;
                    fileStatusLabel.Text = "Generating index...";
                });

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(workspaceFolder);

                    string masterIndexPath = Path.Combine(workspaceFolder, $"{_currentWorkspace.Name}_master_index.txt");
                    string masterIndex = _indexGenerator.GenerateMasterIndex(workspaceAnalysis);
                    File.WriteAllText(masterIndexPath, masterIndex, new UTF8Encoding(false));

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var chunk = chunks[i];
                        string chunkPath = Path.Combine(workspaceFolder, $"{_currentWorkspace.Name}_chunk_{chunk.ChunkNumber}.txt");
                        string chunkContent = _indexGenerator.GenerateChunkContent(chunk, chunks.Count, fileAnalyses);
                        File.WriteAllText(chunkPath, chunkContent, new UTF8Encoding(false));

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            fileStatusLabel.Text = $"Writing chunk {i + 1} of {chunks.Count}...";
                        });
                    }
                });

                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressBar.Value = progressBar.Maximum;
                });

                int totalTokens = fileAnalyses.Sum(f => f.EstimatedTokens);
                UpdateStatus($"Generated {chunks.Count} chunk(s) with ~{totalTokens:N0} tokens", Brushes.LightGreen);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
            finally
            {
                SetUIState(true);
            }
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
            var enabledRepos = ExternalRepositories.Where(r => r.IsEnabled).ToList();

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
                    _gitService,
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
