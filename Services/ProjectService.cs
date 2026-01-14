using CodeMerger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CodeMerger.Services
{
    public class ProjectService
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeMerger");

        public const string ConfigFileName = "project.json";
        private const string ActiveProjectFileName = "active_project.txt";

        public ProjectService()
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
        }

        public string GetProjectFolder(string projectName)
        {
            return Path.Combine(AppDataFolder, projectName);
        }

        private string GetProjectConfigPath(string projectName)
        {
            return Path.Combine(GetProjectFolder(projectName), ConfigFileName);
        }

        private string GetActiveProjectFilePath()
        {
            return Path.Combine(AppDataFolder, ActiveProjectFileName);
        }

        public bool ProjectExists(string projectName)
        {
            return File.Exists(GetProjectConfigPath(projectName));
        }

        public List<Project> LoadAllProjects()
        {
            var projects = new List<Project>();

            if (!Directory.Exists(AppDataFolder)) return projects;

            foreach (var dir in Directory.GetDirectories(AppDataFolder))
            {
                var configPath = Path.Combine(dir, ConfigFileName);
                if (File.Exists(configPath))
                {
                    try
                    {
                        var json = File.ReadAllText(configPath);
                        var project = JsonSerializer.Deserialize<Project>(json);
                        if (project != null)
                        {
                            projects.Add(project);
                        }
                    }
                    catch
                    {
                        // Skip invalid project files
                    }
                }
            }

            return projects;
        }

        public Project? LoadProject(string projectName)
        {
            var configPath = GetProjectConfigPath(projectName);
            if (!File.Exists(configPath)) return null;

            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<Project>(json);
            }
            catch
            {
                return null;
            }
        }

        public void SaveProject(Project project)
        {
            var folder = GetProjectFolder(project.Name);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            project.LastModifiedDate = DateTime.Now;

            var configPath = GetProjectConfigPath(project.Name);
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        public bool RenameProject(string oldName, string newName)
        {
            if (ProjectExists(newName)) return false;

            var oldFolder = GetProjectFolder(oldName);
            var newFolder = GetProjectFolder(newName);

            if (!Directory.Exists(oldFolder)) return false;

            try
            {
                Directory.Move(oldFolder, newFolder);

                // Update project name in config
                var project = LoadProject(newName);
                if (project != null)
                {
                    project.Name = newName;
                    SaveProject(project);
                }

                // Update active project if it was the renamed one
                if (GetActiveProject() == oldName)
                {
                    SetActiveProject(newName);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void DeleteProject(string projectName)
        {
            var folder = GetProjectFolder(projectName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            // Clear active project if it was deleted
            if (GetActiveProject() == projectName)
            {
                ClearActiveProject();
            }
        }

        /// <summary>
        /// Gets the currently active project name for MCP mode.
        /// Returns null if no active project is set.
        /// </summary>
        public string? GetActiveProject()
        {
            var filePath = GetActiveProjectFilePath();
            if (!File.Exists(filePath)) return null;

            try
            {
                var projectName = File.ReadAllText(filePath).Trim();
                
                // Validate that the project still exists
                if (!string.IsNullOrEmpty(projectName) && ProjectExists(projectName))
                {
                    return projectName;
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the active project for MCP mode.
        /// Called when user switches projects in the GUI.
        /// </summary>
        public void SetActiveProject(string projectName)
        {
            var filePath = GetActiveProjectFilePath();
            File.WriteAllText(filePath, projectName);
        }

        /// <summary>
        /// Clears the active project setting.
        /// </summary>
        public void ClearActiveProject()
        {
            var filePath = GetActiveProjectFilePath();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
