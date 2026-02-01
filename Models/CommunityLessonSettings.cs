using System;
using System.IO;
using System.Text.Json;

namespace CodeMerger.Models
{
    /// <summary>
    /// Settings for community lesson sync and submission.
    /// Stored in AppData/CodeMerger/community-settings.json.
    /// </summary>
    public class CommunityLessonSettings
    {
        private const string SettingsFileName = "community-settings.json";

        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeMerger");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public string RepoUrl { get; set; } = "https://github.com/pcarvalho75/CodeMerger";
        public string GitHubToken { get; set; } = string.Empty;
        public string GitHubUsername { get; set; } = string.Empty;
        public int SyncIntervalHours { get; set; } = 24;
        public bool CommunityLessonsEnabled { get; set; } = true;

        public static CommunityLessonSettings Load()
        {
            var path = Path.Combine(AppDataFolder, SettingsFileName);
            if (!File.Exists(path))
                return new CommunityLessonSettings();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<CommunityLessonSettings>(json, JsonOptions)
                    ?? new CommunityLessonSettings();
            }
            catch
            {
                return new CommunityLessonSettings();
            }
        }

        public void Save()
        {
            if (!Directory.Exists(AppDataFolder))
                Directory.CreateDirectory(AppDataFolder);

            var path = Path.Combine(AppDataFolder, SettingsFileName);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
    }
}
