using System;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages workspace settings lifecycle - loading, saving, and change notifications.
    /// </summary>
    public class WorkspaceSettingsService
    {
        private WorkspaceSettings _currentSettings;
        private string? _currentWorkspacePath;
        private readonly Action<string>? _log;

        /// <summary>
        /// Fired when settings are changed and saved.
        /// </summary>
        public event EventHandler<WorkspaceSettings>? SettingsChanged;

        /// <summary>
        /// Current workspace settings. Never null - returns defaults if no workspace loaded.
        /// </summary>
        public WorkspaceSettings CurrentSettings => _currentSettings;

        /// <summary>
        /// Whether a workspace is currently loaded.
        /// </summary>
        public bool HasWorkspace => !string.IsNullOrEmpty(_currentWorkspacePath);

        public WorkspaceSettingsService(Action<string>? log = null)
        {
            _log = log;
            _currentSettings = WorkspaceSettings.GetDefaultSettings();
        }

        /// <summary>
        /// Loads settings for a workspace. Call this when workspace changes.
        /// </summary>
        public WorkspaceSettings LoadSettings(string workspacePath)
        {
            if (string.IsNullOrEmpty(workspacePath))
            {
                _log?.Invoke("LoadSettings: empty workspace path, using defaults");
                _currentSettings = WorkspaceSettings.GetDefaultSettings();
                _currentWorkspacePath = null;
                return _currentSettings;
            }

            _currentWorkspacePath = workspacePath;
            _currentSettings = WorkspaceSettings.LoadFromWorkspace(workspacePath, _log);
            _log?.Invoke($"Settings loaded for workspace: {workspacePath}");

            return _currentSettings;
        }

        /// <summary>
        /// Saves current settings to the workspace.
        /// </summary>
        public bool SaveSettings()
        {
            if (string.IsNullOrEmpty(_currentWorkspacePath))
            {
                _log?.Invoke("SaveSettings: no workspace loaded");
                return false;
            }

            var success = _currentSettings.SaveToWorkspace(_currentWorkspacePath, _log);

            if (success)
            {
                SettingsChanged?.Invoke(this, _currentSettings);
            }

            return success;
        }

        /// <summary>
        /// Updates a setting and saves immediately.
        /// </summary>
        public bool UpdateSetting(Action<WorkspaceSettings> updateAction)
        {
            updateAction(_currentSettings);
            _currentSettings.Validate();
            return SaveSettings();
        }

        /// <summary>
        /// Reloads settings from disk (useful if edited externally).
        /// </summary>
        public WorkspaceSettings ReloadSettings()
        {
            if (string.IsNullOrEmpty(_currentWorkspacePath))
            {
                return _currentSettings;
            }

            _currentSettings = WorkspaceSettings.LoadFromWorkspace(_currentWorkspacePath, _log);
            return _currentSettings;
        }

        /// <summary>
        /// Resets settings to defaults and saves.
        /// </summary>
        public bool ResetToDefaults()
        {
            _currentSettings = WorkspaceSettings.GetDefaultSettings();
            return SaveSettings();
        }
    }
}
