using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Handles storage and retrieval of self-improvement lessons.
    /// Local lessons are user/session-specific. Community lessons are shared across all CodeMerger users.
    /// Limited to 100 local lessons maximum.
    /// </summary>
    public class LessonService
    {
        private const int MaxLessons = 100;
        private const string LessonsFileName = "lessons.json";
        private const string CommunityLessonsFileName = "community-lessons.json";

        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeMerger");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public LessonService()
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
        }

        private string GetLessonsFilePath()
        {
            return Path.Combine(AppDataFolder, LessonsFileName);
        }

        private string GetCommunityLessonsFilePath()
        {
            return Path.Combine(AppDataFolder, CommunityLessonsFileName);
        }

        /// <summary>
        /// Gets local lessons only.
        /// </summary>
        public List<Lesson> GetLocalLessons()
        {
            var filePath = GetLessonsFilePath();
            if (!File.Exists(filePath))
            {
                return new List<Lesson>();
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var lessons = JsonSerializer.Deserialize<List<Lesson>>(json, JsonOptions) ?? new List<Lesson>();
                foreach (var l in lessons) l.Source = LessonSource.Local;
                return lessons;
            }
            catch
            {
                return new List<Lesson>();
            }
        }

        /// <summary>
        /// Gets community lessons from local cache.
        /// </summary>
        public List<Lesson> GetCommunityLessons()
        {
            var filePath = GetCommunityLessonsFilePath();
            if (!File.Exists(filePath))
            {
                return new List<Lesson>();
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var lessons = JsonSerializer.Deserialize<List<Lesson>>(json, JsonOptions) ?? new List<Lesson>();
                foreach (var l in lessons) l.Source = LessonSource.Community;
                return lessons;
            }
            catch
            {
                return new List<Lesson>();
            }
        }

        /// <summary>
        /// Gets all lessons (local + community), numbered sequentially.
        /// Local lessons come first, then community lessons.
        /// </summary>
        public List<Lesson> GetLessons()
        {
            var local = GetLocalLessons();
            var community = GetCommunityLessons();

            var all = new List<Lesson>();
            all.AddRange(local);
            all.AddRange(community);

            // Renumber sequentially for display
            for (int i = 0; i < all.Count; i++)
            {
                all[i].Number = i + 1;
            }

            return all;
        }

        /// <summary>
        /// Logs a new local lesson. Returns false if at capacity (100 lessons).
        /// </summary>
        public bool LogLesson(string type, string component, string observation, string proposal, string? suggestedCode = null)
        {
            var lessons = GetLocalLessons();

            if (lessons.Count >= MaxLessons)
            {
                return false;
            }

            var nextNumber = lessons.Count > 0 ? lessons.Max(l => l.Number) + 1 : 1;

            var lesson = new Lesson
            {
                Number = nextNumber,
                Timestamp = DateTime.Now,
                Type = type,
                Component = component,
                Observation = observation,
                Proposal = proposal,
                SuggestedCode = suggestedCode,
                Source = LessonSource.Local
            };

            lessons.Add(lesson);
            SaveLocalLessons(lessons);
            return true;
        }

        /// <summary>
        /// Saves community lessons cache (called after sync from remote).
        /// </summary>
        public void SaveCommunityLessons(List<Lesson> lessons)
        {
            foreach (var l in lessons) l.Source = LessonSource.Community;
            var filePath = GetCommunityLessonsFilePath();
            var json = JsonSerializer.Serialize(lessons, JsonOptions);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Deletes a specific lesson by its displayed number.
        /// Only local lessons can be deleted.
        /// </summary>
        public (bool success, string message) DeleteLesson(int displayNumber)
        {
            var all = GetLessons();
            if (displayNumber < 1 || displayNumber > all.Count)
                return (false, $"Lesson #{displayNumber} not found.");

            var target = all[displayNumber - 1];
            if (target.Source == LessonSource.Community)
                return (false, $"Lesson #{displayNumber} is a community lesson and cannot be deleted locally.");

            var local = GetLocalLessons();
            // Match by timestamp + observation since numbers shift
            var match = local.FirstOrDefault(l =>
                l.Timestamp == target.Timestamp && l.Observation == target.Observation);

            if (match == null)
                return (false, "Could not find matching local lesson.");

            local.Remove(match);
            for (int i = 0; i < local.Count; i++)
                local[i].Number = i + 1;

            SaveLocalLessons(local);
            return (true, $"Deleted local lesson #{displayNumber}.");
        }

        /// <summary>
        /// Deletes all local lessons. Community lessons are not affected.
        /// </summary>
        public void ClearAllLessons()
        {
            SaveLocalLessons(new List<Lesson>());
        }

        /// <summary>
        /// Gets the current count of local lessons.
        /// </summary>
        public int GetLessonCount()
        {
            return GetLocalLessons().Count;
        }

        /// <summary>
        /// Checks if more local lessons can be logged.
        /// </summary>
        public bool CanLogMore()
        {
            return GetLessonCount() < MaxLessons;
        }

        private void SaveLocalLessons(List<Lesson> lessons)
        {
            var filePath = GetLessonsFilePath();
            var json = JsonSerializer.Serialize(lessons, JsonOptions);
            File.WriteAllText(filePath, json);
        }
    }
}
