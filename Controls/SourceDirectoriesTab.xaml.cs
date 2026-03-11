using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        private static readonly Brush ChipBackground = new SolidColorBrush(Color.FromRgb(15, 52, 96));   // AccentSecondary
        private static readonly Brush ChipForeground = new SolidColorBrush(Color.FromRgb(234, 234, 234)); // TextPrimary
        private static readonly Brush ChipRemove = new SolidColorBrush(Color.FromRgb(233, 69, 96));       // AccentPrimary

        // Preset extension groups
        private static readonly string[] PresetCSharp = { ".cs", ".xaml", ".csproj", ".sln", ".slnx", ".props", ".targets" };
        private static readonly string[] PresetPython = { ".py", ".pyi", ".pyx", ".ipynb" };
        private static readonly string[] PresetWeb = { ".html", ".css", ".js", ".ts", ".tsx", ".jsx", ".vue", ".svelte" };
        private static readonly string[] PresetData = { ".json", ".xml", ".yaml", ".yml", ".csv", ".toml" };
        private static readonly string[] PresetDocs = { ".md", ".txt", ".rst" };

        /// <summary>Raised when a change requires save + rescan.</summary>
        public event Func<System.Threading.Tasks.Task>? SaveAndScanRequested;

        /// <summary>Raised when UI should be locked/unlocked (e.g. during git clone).</summary>
        public event EventHandler<bool>? UIStateRequested;

        /// <summary>Current extensions as comma-separated string (serialized from chips).</summary>
        public string Extensions => string.Join(", ", GetChipValues(extensionChips));

        /// <summary>Current ignored directories as comma-separated string (serialized from chips).</summary>
        public string IgnoredDirectories => string.Join(", ", GetChipValues(ignoredDirChips));

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

            _directoryManager.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DirectoryManager.CountText))
                    directoryCountText.Text = _directoryManager.CountText;
            };

            _gitRepositoryManager.OnProgress += msg => Dispatcher.Invoke(() => gitStatusText.Text = msg);
        }

        public void LoadWorkspace(string extensions, string ignoredDirs, List<ExternalRepository> repos)
        {
            _isLoadingWorkspace = true;
            try
            {
                PopulateChips(extensionChips, Workspace.ParseList(extensions));
                PopulateChips(ignoredDirChips, Workspace.ParseList(ignoredDirs));
                _gitRepositoryManager?.Load(repos);
            }
            finally
            {
                _isLoadingWorkspace = false;
            }
        }

        public void SetLoadingWorkspace(bool loading)
        {
            _isLoadingWorkspace = loading;
        }

        #region Tag Chips

        private void PopulateChips(ItemsControl container, List<string> values)
        {
            container.Items.Clear();
            foreach (var value in values)
                container.Items.Add(CreateChip(value, container));
        }

        private Border CreateChip(string text, ItemsControl container)
        {
            var removeButton = new Button
            {
                Content = "\u00d7",
                FontSize = 12,
                Foreground = ChipRemove,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0, MinHeight = 0
            };

            var chip = new Border
            {
                Background = ChipBackground,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 2, 4, 2),
                Tag = text,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = text,
                            Foreground = ChipForeground,
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        removeButton
                    }
                }
            };

            removeButton.Click += (s, e) =>
            {
                container.Items.Remove(chip);
                if (!_isLoadingWorkspace)
                    OnChipsChanged();
            };

            return chip;
        }

        private static List<string> GetChipValues(ItemsControl container)
        {
            return container.Items.Cast<Border>()
                .Select(b => b.Tag?.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }

        private void AddChipFromTextBox(TextBox textBox, ItemsControl container)
        {
            var raw = textBox.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;

            // Support pasting comma-separated values
            var items = Workspace.ParseList(raw);
            var existing = new HashSet<string>(GetChipValues(container), StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (!existing.Contains(item))
                {
                    container.Items.Add(CreateChip(item, container));
                    existing.Add(item);
                }
            }

            textBox.Text = "";
            OnChipsChanged();
        }

        private void AddPresetExtensions(string[] preset)
        {
            var existing = new HashSet<string>(GetChipValues(extensionChips), StringComparer.OrdinalIgnoreCase);
            foreach (var ext in preset)
            {
                if (!existing.Contains(ext))
                {
                    extensionChips.Items.Add(CreateChip(ext, extensionChips));
                    existing.Add(ext);
                }
            }
            OnChipsChanged();
        }

        private async void OnChipsChanged()
        {
            if (_isLoadingWorkspace || !IsLoaded) return;
            if (SaveAndScanRequested != null) await SaveAndScanRequested.Invoke();
        }

        #endregion

        #region Extension/IgnoredDir Handlers

        private void AddExtension_Click(object sender, RoutedEventArgs e) => AddChipFromTextBox(newExtensionTextBox, extensionChips);
        private void AddIgnoredDir_Click(object sender, RoutedEventArgs e) => AddChipFromTextBox(newIgnoredDirTextBox, ignoredDirChips);

        private void NewExtension_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddChipFromTextBox(newExtensionTextBox, extensionChips); e.Handled = true; }
        }

        private void NewIgnoredDir_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddChipFromTextBox(newIgnoredDirTextBox, ignoredDirChips); e.Handled = true; }
        }

        private void PresetCSharp_Click(object sender, RoutedEventArgs e) => AddPresetExtensions(PresetCSharp);
        private void PresetPython_Click(object sender, RoutedEventArgs e) => AddPresetExtensions(PresetPython);
        private void PresetWeb_Click(object sender, RoutedEventArgs e) => AddPresetExtensions(PresetWeb);
        private void PresetData_Click(object sender, RoutedEventArgs e) => AddPresetExtensions(PresetData);
        private void PresetDocs_Click(object sender, RoutedEventArgs e) => AddPresetExtensions(PresetDocs);

        #endregion

        #region Directory Handlers

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
                _directoryManager!.Remove(item);

            if (selectedItems.Count > 0 && SaveAndScanRequested != null)
                await SaveAndScanRequested.Invoke();
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

        #endregion

        #region Git Handlers

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

        #endregion
    }
}
