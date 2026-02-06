using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CodeMerger.Services;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public partial class ParametersTab : UserControl
    {
        private bool _isLoadingSettings;
        private WorkspaceSettingsService? _settingsService;
        private BackupCleanupService? _backupCleanupService;
        private Func<Workspace?>? _getCurrentWorkspace;

        // Events for MainWindow orchestration
        public event EventHandler? RestartRequested;
        public event EventHandler<int>? TimeoutThresholdChanged;
        public event EventHandler<string>? StatusUpdate;

        public ParametersTab()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Inject dependencies after construction.
        /// </summary>
        public void Initialize(
            WorkspaceSettingsService settingsService,
            BackupCleanupService backupCleanupService,
            Func<Workspace?> getCurrentWorkspace)
        {
            _settingsService = settingsService;
            _backupCleanupService = backupCleanupService;
            _getCurrentWorkspace = getCurrentWorkspace;
        }

        /// <summary>
        /// Load settings for the given workspace folder and update all UI controls.
        /// Returns the loaded settings so caller can use them (e.g. for timeout threshold).
        /// </summary>
        public WorkspaceSettings LoadSettings(string workspaceFolder)
        {
            _isLoadingSettings = true;
            try
            {
                var settings = _settingsService!.LoadSettings(workspaceFolder);

                createBackupFilesCheckBox.IsChecked = settings.CreateBackupFiles;
                autoCleanupCheckBox.IsChecked = settings.AutoCleanupEnabled;
                backupRetentionTextBox.Text = settings.BackupRetentionHours.ToString();
                maxBackupsTextBox.Text = settings.MaxBackupsPerFile.ToString();
                timeoutThresholdTextBox.Text = settings.TimeoutThresholdSeconds.ToString();
                showSessionStatsCheckBox.IsChecked = settings.ShowSessionStatistics;
                sessionStatsPanel.Visibility = settings.ShowSessionStatistics ? Visibility.Visible : Visibility.Collapsed;

                // Hide restart banner when loading new workspace
                settingsRestartBanner.Visibility = Visibility.Collapsed;

                // Run auto-cleanup if enabled
                var workspace = _getCurrentWorkspace?.Invoke();
                if (settings.AutoCleanupEnabled && workspace != null)
                {
                    var activeDirectories = workspace.InputDirectories
                        .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                        .ToList();

                    var (deleted, bytesFreed) = _backupCleanupService!.RunAutoCleanup(activeDirectories, settings);
                    if (deleted > 0)
                    {
                        StatusUpdate?.Invoke(this, $"Auto-cleanup: removed {deleted} old backup files ({bytesFreed / 1024.0:F1} KB freed)");
                    }
                }

                return settings;
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        /// <summary>
        /// Show the restart banner (called when MCP is connected and settings change externally).
        /// </summary>
        public void ShowRestartBanner()
        {
            settingsRestartBanner.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Hide the restart banner.
        /// </summary>
        public void HideRestartBanner()
        {
            settingsRestartBanner.Visibility = Visibility.Collapsed;
        }

        // --- Internal event handlers ---

        private void CreateBackupFiles_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            _settingsService.UpdateSetting(s => s.CreateBackupFiles = createBackupFilesCheckBox.IsChecked == true);
            ShowRestartBannerIfConnected();
        }

        private void AutoCleanup_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            _settingsService.UpdateSetting(s => s.AutoCleanupEnabled = autoCleanupCheckBox.IsChecked == true);
        }

        private void BackupRetention_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            if (int.TryParse(backupRetentionTextBox.Text, out int hours))
            {
                _settingsService.UpdateSetting(s => s.BackupRetentionHours = hours);
            }
        }

        private void MaxBackups_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            if (int.TryParse(maxBackupsTextBox.Text, out int max))
            {
                _settingsService.UpdateSetting(s => s.MaxBackupsPerFile = max);
            }
        }

        private void TimeoutThreshold_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            if (int.TryParse(timeoutThresholdTextBox.Text, out int seconds))
            {
                _settingsService.UpdateSetting(s => s.TimeoutThresholdSeconds = seconds);
                TimeoutThresholdChanged?.Invoke(this, seconds);
            }
        }

        private void ShowSessionStats_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || _settingsService == null || !_settingsService.HasWorkspace) return;

            var show = showSessionStatsCheckBox.IsChecked == true;
            _settingsService.UpdateSetting(s => s.ShowSessionStatistics = show);
            sessionStatsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CleanAllBackups_Click(object sender, RoutedEventArgs e)
        {
            var workspace = _getCurrentWorkspace?.Invoke();
            if (workspace == null) return;

            var activeDirectories = workspace.InputDirectories
                .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                .ToList();

            var stats = _backupCleanupService!.GetStatistics(activeDirectories);

            if (stats.TotalCount == 0)
            {
                MessageBox.Show("No backup files found in this workspace.", "Cleanup",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Found {stats.TotalCount} backup files ({stats.TotalSizeMB:F2} MB).\n\nThis will permanently delete all .bak files in this workspace.\n\nContinue?",
                "Confirm Cleanup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var (deleted, bytesFreed) = _backupCleanupService.CleanupAll(activeDirectories);
            StatusUpdate?.Invoke(this, $"Deleted {deleted} backup files ({bytesFreed / (1024.0 * 1024.0):F2} MB freed)");
        }

        private void ShowBackupStats_Click(object sender, RoutedEventArgs e)
        {
            var workspace = _getCurrentWorkspace?.Invoke();
            if (workspace == null) return;

            var activeDirectories = workspace.InputDirectories
                .Where(dir => !workspace.DisabledDirectories.Contains(dir))
                .ToList();

            var stats = _backupCleanupService!.GetStatistics(activeDirectories);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Backup File Statistics\n");
            sb.AppendLine($"Total files: {stats.TotalCount}");
            sb.AppendLine($"Total size: {stats.TotalSizeMB:F2} MB");

            if (stats.OldestBackup.HasValue)
                sb.AppendLine($"Oldest backup: {stats.OldestBackup.Value:g}");

            if (stats.ByDirectory.Count > 0)
            {
                sb.AppendLine($"\nBy directory:");
                foreach (var kv in stats.ByDirectory.OrderByDescending(x => x.Value.Size))
                {
                    sb.AppendLine($"  {kv.Key}: {kv.Value.Count} files ({kv.Value.Size / 1024.0:F1} KB)");
                }
            }

            MessageBox.Show(sb.ToString(), "Backup Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RestartMcpServer_Click(object sender, RoutedEventArgs e)
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The actual restart banner visibility check needs to know if MCP is connected.
        /// We use a Func to avoid coupling to McpConnectionService directly.
        /// </summary>
        private Func<bool>? _isMcpConnected;

        public void SetConnectionCheck(Func<bool> isMcpConnected)
        {
            _isMcpConnected = isMcpConnected;
        }

        private void ShowRestartBannerIfConnected()
        {
            if (_isMcpConnected?.Invoke() == true)
            {
                settingsRestartBanner.Visibility = Visibility.Visible;
            }
        }
    }
}
