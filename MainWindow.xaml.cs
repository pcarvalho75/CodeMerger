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
        private Project? _currentProject;
        private CancellationTokenSource? _mcpCancellation;
        private GitService? _gitService;

        private string _statusText = string.Empty;
        private Brush _statusForeground = Brushes.Black;
        private bool _isScanning = false;
        private bool _isLoadingProject = false;
        private int _estimatedTokens = 0;
        private Process? _mcpProcess;
        private NamedPipeServerStream? _pipeServer;
        private readonly ClaudeDesktopService _claudeDesktopService = new ClaudeDesktopService();

        private const int MCP_RECOMMENDED_THRESHOLD = 500000;

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

            UpdateStatus("Ready.", Brushes.Black);
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

            // Check if paths were auto-healed (ClickOnce update detection)
            if (Application.Current.Properties.Contains("ConfigHealedCount"))
            {
                int count = (int)Application.Current.Properties["ConfigHealedCount"];
                if (count > 0)
                {
                    string message = _claudeDesktopService.IsClickOnceDeployment()
                        ? $"ClickOnce update detected. Updated {count} config entry(s). Please restart Claude Desktop."
                        : $"Updated {count} Claude Desktop config entry(s) to match current installation.";
                    UpdateStatus(message, Brushes.DarkGreen);
                }
            }

            RefreshClaudeDesktopStatus();
        }

        private void RefreshClaudeDesktopStatus()
        {
            if (_claudeDesktopService.IsClaudeDesktopInstalled())
            {
                claudeInstallStatus.Text = "Installed ✓";
                claudeInstallStatus.Foreground = Brushes.Green;
                claudeDownloadButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                claudeInstallStatus.Text = "Not installed";
                claudeInstallStatus.Foreground = Brushes.Gray;
                claudeDownloadButton.Visibility = Visibility.Visible;
            }

            if (_currentProject == null)
            {
                claudeConfigStatus.Text = "No project selected";
                claudeConfigStatus.Foreground = Brushes.Gray;
                claudeAddConfigButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_claudeDesktopService.IsProjectConfigured(_currentProject.Name))
            {
                var configuredPath = _claudeDesktopService.GetEntryPath(_currentProject.Name);
                var currentPath = _claudeDesktopService.GetCurrentExePath();

                if (string.Equals(configuredPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    claudeConfigStatus.Text = $"Configured ✓ ({_currentProject.Name})";
                    claudeConfigStatus.Foreground = Brushes.Green;
                }
                else
                {
                    claudeConfigStatus.Text = "Path mismatch (will auto-fix)";
                    claudeConfigStatus.Foreground = Brushes.Orange;
                }
                claudeAddConfigButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                claudeConfigStatus.Text = $"Not configured ({_currentProject.Name})";
                claudeConfigStatus.Foreground = Brushes.Gray;
                claudeAddConfigButton.Visibility = Visibility.Visible;
            }
        }

        private void ClaudeDownload_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenDownloadPage();
        }

        private void ClaudeAddConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null)
            {
                UpdateStatus("No project selected.", Brushes.Red);
                return;
            }

            try
            {
                var exePath = _claudeDesktopService.GetCurrentExePath();
                _claudeDesktopService.UpsertProjectEntry(_currentProject.Name, exePath);
                UpdateStatus($"Added '{_currentProject.Name}' to Claude Desktop config.", Brushes.Green);
                RefreshClaudeDesktopStatus();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to update config: {ex.Message}", Brushes.Red);
            }
        }

        private void ClaudeOpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            _claudeDesktopService.OpenConfigFolder();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            StopMcpServer();

            // Unsubscribe from GitService events to prevent leaks
            if (_gitService != null)
            {
                _gitService.OnProgress -= OnGitProgress;
            }
        }

        private void OnMcpLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus(message, Brushes.DarkGreen);
            });
        }

        private void LoadProjectList()
        {
            var projects = _projectService.LoadAllProjects();
            projectComboBox.ItemsSource = projects;

            if (projects.Count > 0)
            {
                projectComboBox.SelectedIndex = 0;
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
                // Unsubscribe from previous GitService if exists
                if (_gitService != null)
                {
                    _gitService.OnProgress -= OnGitProgress;
                }

                _currentProject = selected;

                // Initialize GitService for this project
                string projectFolder = _projectService.GetProjectFolder(_currentProject.Name);
                _gitService = new GitService(projectFolder);
                _gitService.OnProgress += OnGitProgress;

                LoadProjectData(_currentProject);
                projectStatusText.Text = $"Last modified: {_currentProject.LastModifiedDate:g}";

                AutoUpdateClaudeConfig();
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

        private void AutoUpdateClaudeConfig()
        {
            if (_currentProject == null) return;

            try
            {
                var exePath = _claudeDesktopService.GetCurrentExePath();
                _claudeDesktopService.UpsertProjectEntry(_currentProject.Name, exePath);
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

                // Load external repositories
                ExternalRepositories.Clear();
                foreach (var repo in project.ExternalRepositories)
                {
                    ExternalRepositories.Add(repo);
                }

                extensionsTextBox.Text = project.Extensions;
                ignoredDirsTextBox.Text = project.IgnoredDirectories;

                string projectFolder = _projectService.GetProjectFolder(project.Name);
                outputFileTextBox.Text = projectFolder;

                // Auto-update external repos
                await UpdateExternalReposAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading project data: {ex.Message}", Brushes.Red);
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

            // Save external repositories
            _currentProject.ExternalRepositories = ExternalRepositories.ToList();

            _projectService.SaveProject(_currentProject);
            projectStatusText.Text = $"Saved: {_currentProject.LastModifiedDate:g}";
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
                UpdateStatus("No project selected.", Brushes.Red);
                return;
            }

            string url = gitUrlTextBox.Text.Trim();

            if (!GitService.IsValidGitUrl(url))
            {
                UpdateStatus("Invalid Git URL. Use https://github.com/user/repo format.", Brushes.Red);
                return;
            }

            // Check if already added
            if (ExternalRepositories.Any(r => r.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                UpdateStatus("Repository already added.", Brushes.Orange);
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
                UpdateStatus($"Failed to clone: {ex.Message}", Brushes.Red);
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
                MessageBox.Show("Output folder does not exist yet. Run Merge first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void UpdateRecommendation()
        {
            if (_estimatedTokens < 100000)
            {
                recommendationBanner.Visibility = Visibility.Collapsed;
            }
            else if (_estimatedTokens < MCP_RECOMMENDED_THRESHOLD)
            {
                recommendationBanner.Visibility = Visibility.Visible;
                recommendationBanner.Background = new SolidColorBrush(Color.FromRgb(212, 237, 218));
                recommendationBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(195, 230, 203));
                recommendationText.Text = $"Project size: ~{_estimatedTokens:N0} tokens. Recommended: Generate Chunks and upload to Claude Project.";
            }
            else
            {
                recommendationBanner.Visibility = Visibility.Visible;
                recommendationBanner.Background = new SolidColorBrush(Color.FromRgb(255, 243, 205));
                recommendationBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 238, 186));
                recommendationText.Text = $"Large project: ~{_estimatedTokens:N0} tokens. Recommended: Use MCP Server for dynamic access (better for large codebases).";
            }
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (FoundFiles.Count == 0)
            {
                UpdateStatus("No files to merge. Please add directories and check filters.", Brushes.Red);
                return;
            }

            if (_currentProject == null)
            {
                UpdateStatus("No project selected.", Brushes.Red);
                return;
            }

            int estimatedChunks = (int)Math.Ceiling(_estimatedTokens / 150000.0);

            if (estimatedChunks > 5)
            {
                var result = MessageBox.Show(
                    $"This project is large (~{_estimatedTokens:N0} tokens) and will generate {estimatedChunks} chunks.\n\nContinue?",
                    "Large Project Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    UpdateStatus("Merge cancelled.", Brushes.Black);
                    return;
                }
            }

            string projectFolder = _projectService.GetProjectFolder(_currentProject.Name);
            var filesToMerge = FoundFiles.ToList();

            SetUIState(false);
            UpdateStatus("Analyzing files...", Brushes.Blue);
            progressBar.Value = 0;
            progressBar.Maximum = filesToMerge.Count + 2;

            try
            {
                var fileAnalyses = new List<FileAnalysis>();
                var selectedDirs = InputDirectories.Where(item => item.IsSelected).Select(item => item.Path).ToList();

                // Add enabled external repo paths
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

                UpdateStatus("Creating chunks...", Brushes.Blue);
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
                UpdateStatus($"Success! Generated {chunks.Count} chunk(s) with ~{totalTokens:N0} tokens. Output: {projectFolder}", Brushes.Green);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}", Brushes.Red);
            }
            finally
            {
                SetUIState(true);
            }
        }

        private async void McpServer_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpServer.IsRunning)
            {
                StopMcpServer();
            }
            else
            {
                await StartMcpServerAsync();
            }
        }

        private async Task StartMcpServerAsync()
        {
            if (_currentProject == null || FoundFiles.Count == 0)
            {
                UpdateStatus("No project or files to serve.", Brushes.Red);
                return;
            }

            try
            {
                if (!_claudeDesktopService.IsProjectConfigured(_currentProject.Name))
                {
                    var exePath = _claudeDesktopService.GetCurrentExePath();
                    _claudeDesktopService.UpsertProjectEntry(_currentProject.Name, exePath);
                    RefreshClaudeDesktopStatus();
                }

                UpdateStatus("Indexing project for MCP...", Brushes.Blue);

                var selectedDirs = InputDirectories.Where(item => item.IsSelected).Select(item => item.Path).ToList();

                // Add enabled external repo paths
                foreach (var repo in ExternalRepositories.Where(r => r.IsEnabled))
                {
                    selectedDirs.Add(repo.LocalPath);
                }

                _mcpServer.IndexProject(_currentProject.Name, selectedDirs, FoundFiles.ToList());

                string pipeName = $"codemerger_mcp_{_currentProject.Name}_{Environment.ProcessId}";

                mcpButton.Content = "Stop MCP Server";
                mcpButton.Style = (Style)FindResource("McpStopButton");
                mcpStatusPanel.Visibility = Visibility.Visible;
                mcpConfigText.Text = $"MCP server ready for project: {_currentProject.Name}\n\n" +
                                     $"Claude Desktop will launch CodeMerger automatically when needed.\n" +
                                     $"Make sure to restart Claude Desktop if it was already running.";

                _mcpCancellation = new CancellationTokenSource();

                _pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                UpdateStatus($"MCP Server ready. Waiting for connection...", Brushes.DarkGreen);

                // Capture project name for use in Task
                string currentProjectName = _currentProject.Name;

                await Task.Run(async () =>
                {
                    await _pipeServer.WaitForConnectionAsync(_mcpCancellation.Token);

                    // Notify UI of successful connection (handshake)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("✓ MCP Server connected to Claude Desktop!", Brushes.DarkGreen);
                        mcpConfigText.Text = $"✅ Connected to Claude Desktop\n\nProject: {currentProjectName}";
                    });

                    await _mcpServer.StartAsync(_pipeServer, _pipeServer);
                });
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                UpdateStatus($"MCP Error: {ex.Message}", Brushes.Red);
                StopMcpServer();
            }
        }

        private void StopMcpServer()
        {
            _mcpCancellation?.Cancel();

            // Dispose pipe BEFORE waiting for cancellation to prevent file locks
            try
            {
                _pipeServer?.Dispose();
            }
            catch { }
            _pipeServer = null;

            _mcpServer.Stop();

            _mcpCancellation?.Dispose();
            _mcpCancellation = null;

            mcpButton.Content = "Start MCP Server";
            mcpButton.Style = (Style)FindResource("McpButton");
            mcpStatusPanel.Visibility = Visibility.Collapsed;

            UpdateStatus("MCP Server stopped.", Brushes.Black);
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
                UpdateStatus("Ready. Please add input directories or enable at least one source.", Brushes.Black);
                _isScanning = false;
                UpdateRecommendation();
                return;
            }

            UpdateStatus("Scanning...", Brushes.Blue);
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
                    // Scan local directories
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

                    // Scan external repositories
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

                UpdateStatus($"Found {FoundFiles.Count} files (~{_estimatedTokens:N0} tokens).", Brushes.Black);
                UpdateRecommendation();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error scanning files: {ex.Message}", Brushes.Red);
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
