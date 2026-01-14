using CodeMerger.Models;
using CodeMerger.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<SelectableItem> InputDirectories { get; set; }
        public ObservableCollection<string> FoundFiles { get; set; }
        public ObservableCollection<ExternalRepository> ExternalRepositories { get; set; }

        private readonly ProjectService _projectService = new ProjectService();
        private readonly CodeAnalyzer _codeAnalyzer = new CodeAnalyzer();
        private readonly IndexGenerator _indexGenerator = new IndexGenerator();
        private readonly McpServer _mcpServer = new McpServer();
        private readonly ClaudeDesktopService _claudeDesktopService = new ClaudeDesktopService();
        
        private Project? _currentProject;
        private GitService? _gitService;
        private CancellationTokenSource? _handshakeListenerCts;
        private CancellationTokenSource? _activityListenerCts;

        private string _statusText = string.Empty;
        private Brush _statusForeground = Brushes.White;
        private bool _isScanning = false;
        private bool _isLoadingProject = false;
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
            InputDirectories = new ObservableCollection<SelectableItem>();
            FoundFiles = new ObservableCollection<string>();
            ExternalRepositories = new ObservableCollection<ExternalRepository>();

            inputDirListBox.ItemsSource = InputDirectories;
            fileListBox.ItemsSource = FoundFiles;
            gitRepoListBox.ItemsSource = ExternalRepositories;

            _mcpServer.OnLog += OnMcpLog;

            UpdateStatus("Ready", Brushes.Gray);
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadProjectList();

            if (projectComboBox.Items.Count == 0)
            {
                PromptCreateFirstProject();
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
            StartHandshakeListener();
            StartActivityListener();
        }

        private void StartHandshakeListener()
        {
            _handshakeListenerCts = new CancellationTokenSource();
            var token = _handshakeListenerCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(App.HandshakePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipe.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(pipe);
                        string? message = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(message))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OnHandshakeReceived(message);
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(500, token);
                    }
                }
            }, token);
        }

        private void StartActivityListener()
        {
            _activityListenerCts = new CancellationTokenSource();
            var token = _activityListenerCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(McpServer.ActivityPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipe.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(pipe);
                        string? message = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(message))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OnActivityReceived(message);
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(100, token);
                    }
                }
            }, token);
        }

        private void OnHandshakeReceived(string projectName)
        {
            // Update connection indicator
            connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165)); // AccentSuccess
            connectionStatusText.Text = $"Connected: {projectName}";
            connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
            stopServerButton.Visibility = Visibility.Visible;
            
            UpdateStatus($"✓ Claude connected via MCP (project: {projectName})", Brushes.LightGreen);
        }

        private void OnActivityReceived(string message)
        {
            var parts = message.Split('|', 2);
            if (parts.Length == 2)
            {
                var projectName = parts[0];
                var activity = parts[1];

                // Handle disconnect notification
                if (activity == "DISCONNECT")
                {
                    connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160)); // Gray
                    connectionStatusText.Text = "Disconnected";
                    connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                    stopServerButton.Visibility = Visibility.Collapsed;
                    UpdateStatus($"MCP server disconnected (project: {projectName})", Brushes.Gray);
                    return;
                }

                // Update connection status since we're clearly connected if receiving activity
                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 217, 165)); // AccentSuccess
                connectionStatusText.Text = $"Connected: {projectName}";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 217, 165));
                stopServerButton.Visibility = Visibility.Visible;

                UpdateStatus($"🔄 [{projectName}] {activity}", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            }
            else
            {
                UpdateStatus($"🔄 {message}", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            }
        }

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
                    var activeProject = _projectService.GetActiveProject();
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
                var currentProcessId = Process.GetCurrentProcess().Id;
                var currentProcessName = Process.GetCurrentProcess().ProcessName;

                // Find all CodeMerger processes except this one (the GUI)
                var mcpProcesses = Process.GetProcessesByName(currentProcessName)
                    .Where(p => p.Id != currentProcessId)
                    .ToList();

                if (mcpProcesses.Count == 0)
                {
                    UpdateStatus("No MCP server process found.", Brushes.Gray);
                    connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                    connectionStatusText.Text = "Not connected";
                    connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                    stopServerButton.Visibility = Visibility.Collapsed;
                    return;
                }

                foreach (var process in mcpProcesses)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    catch
                    {
                        // Process may have already exited
                    }
                }

                connectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                connectionStatusText.Text = "Server stopped";
                connectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160));
                stopServerButton.Visibility = Visibility.Collapsed;
                UpdateStatus($"Stopped {mcpProcesses.Count} MCP server process(es). You can now recompile.", Brushes.LightGreen);
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
            _handshakeListenerCts?.Cancel();
            _handshakeListenerCts?.Dispose();
            _handshakeListenerCts = null;

            _activityListenerCts?.Cancel();
            _activityListenerCts?.Dispose();
            _activityListenerCts = null;

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

        private void LoadProjectList()
        {
            var projects = _projectService.LoadAllProjects();
            projectComboBox.ItemsSource = projects;

            if (projects.Count > 0)
            {
                var activeProject = _projectService.GetActiveProject();
                var projectToSelect = projects.Find(p => p.Name == activeProject) ?? projects[0];
                projectComboBox.SelectedItem = projectToSelect;
            }
        }

        private void PromptCreateFirstProject()
        {
            MessageBox.Show("Welcome! Create your first project to get started.", "CodeMerger", MessageBoxButton.OK, MessageBoxImage.Information);
            NewProject_Click(null, null);

            if (projectComboBox.Items.Count == 0)
            {
                Application.Current.Shutdown();
            }
        }

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (projectComboBox.SelectedItem is Project selected)
            {
                if (_gitService != null)
                {
                    _gitService.OnProgress -= OnGitProgress;
                }

                _currentProject = selected;
                _projectService.SetActiveProject(_currentProject.Name);

                string projectFolder = _projectService.GetProjectFolder(_currentProject.Name);
                _gitService = new GitService(projectFolder);
                _gitService.OnProgress += OnGitProgress;

                LoadProjectData(_currentProject);

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

        private async void LoadProjectData(Project project)
        {
            if (project == null) return;

            _isLoadingProject = true;

            try
            {
                InputDirectories.Clear();
                foreach (var dir in project.InputDirectories)
                {
                    bool isSelected = !project.DisabledDirectories.Contains(dir);
                    InputDirectories.Add(new SelectableItem(dir, isSelected));
                }

                ExternalRepositories.Clear();
                foreach (var repo in project.ExternalRepositories)
                {
                    ExternalRepositories.Add(repo);
                }

                extensionsTextBox.Text = project.Extensions;
                ignoredDirsTextBox.Text = project.IgnoredDirectories;

                string projectFolder = _projectService.GetProjectFolder(project.Name);
                outputFileTextBox.Text = projectFolder;

                await UpdateExternalReposAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading project: {ex.Message}", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
            }
            finally
            {
                _isLoadingProject = false;
                UpdateDirectoryCount();
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

        private void NewProject_Click(object? sender, RoutedEventArgs? e)
        {
            var dialog = new InputDialog("New Project", "Enter project name:");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string name = dialog.ResponseText.Trim();

                if (_projectService.ProjectExists(name))
                {
                    MessageBox.Show("A project with that name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var project = new Project { Name = name };
                _projectService.SaveProject(project);

                LoadProjectList();
                projectComboBox.SelectedItem = ((List<Project>)projectComboBox.ItemsSource).Find(p => p.Name == name);
            }
        }

        private void RenameProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;

            var dialog = new InputDialog("Rename Project", "Enter new name:", _currentProject.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                string newName = dialog.ResponseText.Trim();

                if (_projectService.RenameProject(_currentProject.Name, newName))
                {
                    LoadProjectList();
                    projectComboBox.SelectedItem = ((List<Project>)projectComboBox.ItemsSource).Find(p => p.Name == newName);
                }
                else
                {
                    MessageBox.Show("Could not rename project. Name may already exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void DeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;

            var result = MessageBox.Show(
                $"Delete project '{_currentProject.Name}' and all its output files?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _projectService.DeleteProject(_currentProject.Name);
                _currentProject = null;

                LoadProjectList();

                if (projectComboBox.Items.Count == 0)
                {
                    PromptCreateFirstProject();
                }
            }
        }

        private void SaveCurrentProject()
        {
            if (_currentProject == null || _isLoadingProject) return;

            _currentProject.InputDirectories = InputDirectories.Select(item => item.Path).ToList();

            _currentProject.DisabledDirectories = InputDirectories
                .Where(item => !item.IsSelected)
                .Select(item => item.Path)
                .ToList();

            _currentProject.Extensions = extensionsTextBox.Text;
            _currentProject.IgnoredDirectories = ignoredDirsTextBox.Text;
            _currentProject.ExternalRepositories = ExternalRepositories.ToList();

            _projectService.SaveProject(_currentProject);
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
                if (!string.IsNullOrEmpty(folderPath) && !InputDirectories.Any(item => item.Path == folderPath))
                {
                    InputDirectories.Add(new SelectableItem(folderPath, true));
                    SaveCurrentProject();
                    UpdateDirectoryCount();
                    await ScanFilesAsync();
                }
            }
        }

        private async void RemoveDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (inputDirListBox.SelectedItem != null)
            {
                var selectedItems = inputDirListBox.SelectedItems.Cast<SelectableItem>().ToList();
                foreach (var item in selectedItems)
                {
                    InputDirectories.Remove(item);
                }
                SaveCurrentProject();
                UpdateDirectoryCount();
                await ScanFilesAsync();
            }
        }

        private async void SelectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in InputDirectories)
            {
                item.IsSelected = true;
            }
            SaveCurrentProject();
            UpdateDirectoryCount();
            await ScanFilesAsync();
        }

        private async void DeselectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in InputDirectories)
            {
                item.IsSelected = false;
            }
            SaveCurrentProject();
            UpdateDirectoryCount();
            await ScanFilesAsync();
        }

        private void UpdateDirectoryCount()
        {
            int total = InputDirectories.Count;
            int active = InputDirectories.Count(item => item.IsSelected);

            if (total == 0)
            {
                directoryCountText.Text = "";
            }
            else if (active == total)
            {
                directoryCountText.Text = $"({total})";
            }
            else
            {
                directoryCountText.Text = $"({active}/{total} active)";
            }
        }

        #region Git Repository Management

        private async void AddGitRepo_Click(object sender, RoutedEventArgs e)
        {
            if (_gitService == null || _currentProject == null)
            {
                UpdateStatus("No project selected.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
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
                SaveCurrentProject();

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
                SaveCurrentProject();

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
                    SaveCurrentProject();

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
            if (_isLoadingProject) return;
            SaveCurrentProject();
            await ScanFilesAsync();
        }

        #endregion

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;

            string folder = _projectService.GetProjectFolder(_currentProject.Name);

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
                recommendationText.Text = $"Project size: ~{_estimatedTokens:N0} tokens. Claude will access files dynamically via MCP.";
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

            if (_currentProject == null)
            {
                UpdateStatus("No project selected.", new SolidColorBrush(Color.FromRgb(233, 69, 96)));
                return;
            }

            string projectFolder = _projectService.GetProjectFolder(_currentProject.Name);
            var filesToMerge = FoundFiles.ToList();

            SetUIState(false);
            UpdateStatus("Analyzing files...", new SolidColorBrush(Color.FromRgb(100, 200, 255)));
            progressBar.Value = 0;
            progressBar.Maximum = filesToMerge.Count + 2;

            try
            {
                var fileAnalyses = new List<FileAnalysis>();
                var selectedDirs = InputDirectories.Where(item => item.IsSelected).Select(item => item.Path).ToList();

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

                var projectAnalysis = _indexGenerator.BuildProjectAnalysis(_currentProject.Name, fileAnalyses, chunks);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressBar.Value = filesToMerge.Count + 1;
                    fileStatusLabel.Text = "Generating index...";
                });

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(projectFolder);

                    string masterIndexPath = Path.Combine(projectFolder, $"{_currentProject.Name}_master_index.txt");
                    string masterIndex = _indexGenerator.GenerateMasterIndex(projectAnalysis);
                    File.WriteAllText(masterIndexPath, masterIndex, new UTF8Encoding(false));

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var chunk = chunks[i];
                        string chunkPath = Path.Combine(projectFolder, $"{_currentProject.Name}_chunk_{chunk.ChunkNumber}.txt");
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

            var selectedDirs = InputDirectories.Where(item => item.IsSelected).ToList();
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
                var extensions = extensionsTextBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => ext.Trim())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToList();

                var ignoredDirsInput = ignoredDirsTextBox.Text + ",.git";
                var ignoredDirNames = ignoredDirsInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(dir => dir.Trim().ToLowerInvariant())
                    .ToHashSet();

                List<string> allFoundFiles = new List<string>();

                await Task.Run(() =>
                {
                    foreach (var item in selectedDirs)
                    {
                        var dir = item.Path;
                        if (!Directory.Exists(dir)) continue;

                        var allFilesInDir = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Where(file =>
                            {
                                var pathParts = file.Split(Path.DirectorySeparatorChar);
                                if (pathParts.Any(part => ignoredDirNames.Contains(part.ToLowerInvariant())))
                                {
                                    return false;
                                }

                                var fileExtension = Path.GetExtension(file);
                                if (extensions.Count == 0 || extensions.Contains("*.*")) return true;
                                return extensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
                            });
                        allFoundFiles.AddRange(allFilesInDir);
                    }

                    if (_gitService != null)
                    {
                        foreach (var repo in enabledRepos)
                        {
                            var repoFiles = _gitService.GetRepositoryFiles(repo, extensionsTextBox.Text, ignoredDirsTextBox.Text);
                            allFoundFiles.AddRange(repoFiles);
                        }
                    }
                });

                var distinctFiles = allFoundFiles.Distinct().OrderBy(f => f);
                foreach (var file in distinctFiles)
                {
                    FoundFiles.Add(file);
                }

                long totalBytes = 0;
                foreach (var file in FoundFiles)
                {
                    try { totalBytes += new FileInfo(file).Length; } catch { }
                }
                _estimatedTokens = (int)(totalBytes / 4);

                UpdateStatus($"Found {FoundFiles.Count} files (~{_estimatedTokens:N0} tokens)", Brushes.Gray);
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
            SaveCurrentProject();
            await ScanFilesAsync();
        }

        private async void DirectoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isLoadingProject) return;
            SaveCurrentProject();
            UpdateDirectoryCount();
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
