using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CodeMerger.Services;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public partial class SourceDirectoriesTab : UserControl
    {
        private DirectoryManager? _directoryManager;
        private GitRepositoryManager? _gitRepositoryManager;
        private bool _isLoadingWorkspace;

        /// <summary>Raised when a change requires save + rescan.</summary>
        public event Func<System.Threading.Tasks.Task>? SaveAndScanRequested;

        /// <summary>Raised when UI should be locked/unlocked (e.g. during git clone).</summary>
        public event EventHandler<bool>? UIStateRequested;

        /// <summary>Current extensions filter text.</summary>
        public string Extensions => extensionsTextBox.Text;

        /// <summary>Current ignored directories filter text.</summary>
        public string IgnoredDirectories => ignoredDirsTextBox.Text;

        public SourceDirectoriesTab()
        {
            InitializeComponent();
        }

        public void Initialize(DirectoryManager directoryManager, GitRepositoryManager gitRepositoryManager)
        {
            _directoryManager = directoryManager;
            _gitRepositoryManager = gitRepositoryManager;

            inputDirListBox.ItemsSource = _directoryManager.Directories;
            gitRepoListBox.ItemsSource = _gitRepositoryManager.Repositories;

            // Wire directory count updates
            _directoryManager.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DirectoryManager.CountText))
                    directoryCountText.Text = _directoryManager.CountText;
            };

            // Wire git progress
            _gitRepositoryManager.OnProgress += msg => Dispatcher.Invoke(() => gitStatusText.Text = msg);
        }

        /// <summary>
        /// Load workspace data into the UI (called during workspace switch).
        /// </summary>
        public void LoadWorkspace(string extensions, string ignoredDirs, List<ExternalRepository> repos)
        {
            _isLoadingWorkspace = true;
            try
            {
                extensionsTextBox.Text = extensions;
                ignoredDirsTextBox.Text = ignoredDirs;
                _gitRepositoryManager?.Load(repos);
            }
            finally
            {
                _isLoadingWorkspace = false;
            }
        }

        /// <summary>
        /// Set the loading workspace guard (used during workspace switch to suppress events).
        /// </summary>
        public void SetLoadingWorkspace(bool loading)
        {
            _isLoadingWorkspace = loading;
        }

        // --- Directory handlers ---

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
                string? folderPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    _directoryManager!.Add(folderPath);
                    if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
                }
            }
        }

        private async void RemoveDirectory_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = inputDirListBox.SelectedItems.Cast<SelectableItem>().ToList();
            foreach (var item in selectedItems)
            {
                _directoryManager!.Remove(item);
            }

            if (selectedItems.Count > 0)
            {
                if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
            }
        }

        private async void SelectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            _directoryManager!.SelectAll();
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }

        private async void DeselectAllDirectories_Click(object sender, RoutedEventArgs e)
        {
            _directoryManager!.DeselectAll();
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }

        private async void DirectoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isLoadingWorkspace) return;
            _directoryManager!.NotifySelectionChanged();
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }

        // --- Filter handlers ---

        private async void Filters_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }

        // --- Git handlers ---

        private async void AddGitRepo_Click(object sender, RoutedEventArgs e)
        {
            if (_gitRepositoryManager == null) return;

            string url = gitUrlTextBox.Text.Trim();
            UIStateRequested?.Invoke(this, false);

            if (await _gitRepositoryManager.AddRepositoryAsync(url))
            {
                gitUrlTextBox.Text = "https://github.com/user/repo";
                if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
            }

            UIStateRequested?.Invoke(this, true);
        }

        private async void RefreshGitRepo_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var repo = button?.DataContext as ExternalRepository;
            if (repo == null) return;

            UIStateRequested?.Invoke(this, false);

            if (await _gitRepositoryManager!.RefreshRepositoryAsync(repo))
            {
                if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
            }

            UIStateRequested?.Invoke(this, true);
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
                if (_gitRepositoryManager!.RemoveRepository(repo))
                {
                    if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
                }
            }
        }

        private async void GitRepoCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingWorkspace) return;
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }
    }
}
