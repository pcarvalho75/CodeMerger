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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<string> InputDirectories { get; set; }
        public ObservableCollection<string> FoundFiles { get; set; }

        private readonly ProjectService _projectService = new ProjectService();
        private readonly CodeAnalyzer _codeAnalyzer = new CodeAnalyzer();
        private readonly IndexGenerator _indexGenerator = new IndexGenerator();
        private readonly McpServer _mcpServer = new McpServer();
        private Project? _currentProject;

        private string _statusText = string.Empty;
        private Brush _statusForeground = Brushes.Black;
        private bool _isScanning = false;
        private bool _isLoadingProject = false;
        private int _estimatedTokens = 0;
        private Process? _mcpProcess;
        private NamedPipeServerStream? _pipeServer;

        private const int MCP_RECOMMENDED_THRESHOLD = 500000; // 500k tokens

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
            InputDirectories = new ObservableCollection<string>();
            FoundFiles = new ObservableCollection<string>();
            inputDirListBox.ItemsSource = InputDirectories;
            fileListBox.ItemsSource = FoundFiles;

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
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            StopMcpServer();
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
                _currentProject = selected;
                LoadProjectData(_currentProject);
                projectStatusText.Text = $"Last modified: {_currentProject.LastModifiedDate:g}";
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
                    InputDirectories.Add(dir);
                }

                extensionsTextBox.Text = project.Extensions;
                ignoredDirsTextBox.Text = project.IgnoredDirectories;

                string projectFolder = _projectService.GetProjectFolder(project.Name);
                outputFileTextBox.Text = projectFolder;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading project data: {ex.Message}", Brushes.Red);
            }
            finally
            {
                _isLoadingProject = false;
                await ScanFilesAsync();
            }
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

            _currentProject.InputDirectories = InputDirectories.ToList();
            _currentProject.Extensions = extensionsTextBox.Text;
            _currentProject.IgnoredDirectories = ignoredDirsTextBox.Text;

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
                if (!string.IsNullOrEmpty(folderPath) && !InputDirectories.Contains(folderPath))
                {
                    InputDirectories.Add(folderPath);
                    SaveCurrentProject();
                    await ScanFilesAsync();
                }
            }
        }

        private async void RemoveDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (inputDirListBox.SelectedItem != null)
            {
                var selectedItems = inputDirListBox.SelectedItems.Cast<string>().ToList();
                foreach (var item in selectedItems)
                {
                    InputDirectories.Remove(item);
                }
                SaveCurrentProject();
                await ScanFilesAsync();
            }
        }

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

                await Task.Run(() =>
                {
                    for (int i = 0; i < filesToMerge.Count; i++)
                    {
                        var file = filesToMerge[i];
                        var baseDir = InputDirectories.FirstOrDefault(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
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
                UpdateStatus("Indexing project for MCP...", Brushes.Blue);

                // Index the project
                _mcpServer.IndexProject(_currentProject.Name, InputDirectories.ToList(), FoundFiles.ToList());

                // Create named pipe for communication
                string pipeName = $"codemerger_mcp_{_currentProject.Name}_{Environment.ProcessId}";
                
                // Generate config for Claude Desktop
                var config = GenerateClaudeConfig(pipeName);

                // Update UI
                mcpButton.Content = "Stop MCP Server";
                mcpButton.Style = (Style)FindResource("McpStopButton");
                mcpStatusPanel.Visibility = Visibility.Visible;
                mcpConfigText.Text = $"Add this to Claude Desktop config (claude_desktop_config.json):\n\n{config}";

                // Start the pipe server
                _pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                
                UpdateStatus($"MCP Server ready. Waiting for Claude Desktop connection on pipe: {pipeName}", Brushes.DarkGreen);

                // Wait for connection and start serving
                await Task.Run(async () =>
                {
                    await _pipeServer.WaitForConnectionAsync();
                    await _mcpServer.StartAsync(_pipeServer, _pipeServer);
                });
            }
            catch (Exception ex)
            {
                UpdateStatus($"MCP Error: {ex.Message}", Brushes.Red);
                StopMcpServer();
            }
        }

        private void StopMcpServer()
        {
            _mcpServer.Stop();
            
            _pipeServer?.Dispose();
            _pipeServer = null;

            mcpButton.Content = "Start MCP Server";
            mcpButton.Style = (Style)FindResource("McpButton");
            mcpStatusPanel.Visibility = Visibility.Collapsed;

            UpdateStatus("MCP Server stopped.", Brushes.Black);
        }

        private string GenerateClaudeConfig(string pipeName)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "CodeMerger.exe";
            
            return $@"{{
  ""mcpServers"": {{
    ""codemerger"": {{
      ""command"": ""{exePath.Replace("\\", "\\\\")}"",
      ""args"": [""--mcp"", ""{_currentProject?.Name ?? "project"}""]
    }}
  }}
}}";
        }

        private void CopyConfig_Click(object sender, RoutedEventArgs e)
        {
            var config = mcpConfigText.Text;
            if (!string.IsNullOrEmpty(config))
            {
                // Extract just the JSON part
                var jsonStart = config.IndexOf('{');
                if (jsonStart >= 0)
                {
                    Clipboard.SetText(config.Substring(jsonStart));
                    UpdateStatus("Config copied to clipboard!", Brushes.Green);
                }
            }
        }

        private async Task ScanFilesAsync()
        {
            if (!IsLoaded || _isScanning) return;
            _isScanning = true;

            FoundFiles.Clear();
            if (InputDirectories.Count == 0)
            {
                UpdateStatus("Ready. Please add input directories.", Brushes.Black);
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
                    foreach (var dir in InputDirectories)
                    {
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
                });

                var distinctFiles = allFoundFiles.Distinct().OrderBy(f => f);
                foreach (var file in distinctFiles)
                {
                    FoundFiles.Add(file);
                }

                // Calculate estimated tokens
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
