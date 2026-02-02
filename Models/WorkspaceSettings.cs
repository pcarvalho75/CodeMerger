using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeMerger.Models
{
    /// <summary>
    /// Per-workspace settings for CodeMerger behavior.
    /// Stored in .codemerger/settings.json within each workspace.
    /// </summary>
    public class WorkspaceSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Schema version for future migration support
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        // Backup settings
        [JsonPropertyName("createBackupFiles")]
        public bool CreateBackupFiles { get; set; } = false;

        [JsonPropertyName("autoCleanupEnabled")]
        public bool AutoCleanupEnabled { get; set; } = false;

        [JsonPropertyName("backupRetentionHours")]
        public int BackupRetentionHours { get; set; } = 24;

        [JsonPropertyName("maxBackupsPerFile")]
        public int MaxBackupsPerFile { get; set; } = 1;

        // MCP Activity settings
        [JsonPropertyName("timeoutThresholdSeconds")]
        public int TimeoutThresholdSeconds { get; set; } = 45;

        [JsonPropertyName("showSessionStatistics")]
        public bool ShowSessionStatistics { get; set; } = true;

        // Future expansion
        [JsonPropertyName("maxFileSizeToIndexMB")]
        public int MaxFileSizeToIndexMB { get; set; } = 1;

        [JsonPropertyName("gcInterval")]
        public int GcInterval { get; set; } = 50;

        /// <summary>
        /// Validates settings and clamps values to acceptable ranges.
        /// </summary>
        public void Validate()
        {
            BackupRetentionHours = Math.Clamp(BackupRetentionHours, 1, 720);
            MaxBackupsPerFile = Math.Clamp(MaxBackupsPerFile, 1, 100);
            TimeoutThresholdSeconds = Math.Clamp(TimeoutThresholdSeconds, 5, 300);
            MaxFileSizeToIndexMB = Math.Clamp(MaxFileSizeToIndexMB, 1, 50);
            GcInterval = Math.Clamp(GcInterval, 10, 500);
        }

        /// <summary>
        /// Gets the settings file path for a workspace.
        /// </summary>
        public static string GetSettingsPath(string workspacePath)
        {
            return Path.Combine(workspacePath, ".codemerger", "settings.json");
        }

        /// <summary>
        /// Loads settings from a workspace directory.
        /// Returns default settings if file doesn't exist or is invalid.
        /// </summary>
        public static WorkspaceSettings LoadFromWorkspace(string workspacePath, Action<string>? log = null)
        {
            var settingsPath = GetSettingsPath(workspacePath);

            if (!File.Exists(settingsPath))
            {
                log?.Invoke($"No settings file at {settingsPath}, using defaults");
                return GetDefaultSettings();
            }

            try
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<WorkspaceSettings>(json, JsonOptions);

                if (settings == null)
                {
                    log?.Invoke("Settings file was empty, using defaults");
                    return GetDefaultSettings();
                }

                settings.Validate();
                return settings;
            }
            catch (JsonException ex)
            {
                log?.Invoke($"Invalid settings JSON: {ex.Message}, using defaults");

                // Backup corrupt file
                try
                {
                    var backupPath = settingsPath + ".corrupt";
                    File.Copy(settingsPath, backupPath, overwrite: true);
                    log?.Invoke($"Corrupt settings backed up to {backupPath}");
                }
                catch { }

                return GetDefaultSettings();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed to load settings: {ex.Message}, using defaults");
                return GetDefaultSettings();
            }
        }

        /// <summary>
        /// Saves settings to a workspace directory.
        /// Creates the .codemerger directory if it doesn't exist.
        /// </summary>
        public bool SaveToWorkspace(string workspacePath, Action<string>? log = null)
        {
            var settingsPath = GetSettingsPath(workspacePath);

            try
            {
                var dir = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Validate();
                var json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(settingsPath, json);

                log?.Invoke($"Settings saved to {settingsPath}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed to save settings: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a new settings instance with default values.
        /// </summary>
        public static WorkspaceSettings GetDefaultSettings()
        {
            return new WorkspaceSettings();
        }
    }
}
