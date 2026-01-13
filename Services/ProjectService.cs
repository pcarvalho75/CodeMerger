using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    public class ProjectService
    {
        private const string ConfigFileName = "project_config.json";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public string GetProjectsFolder()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(desktop, "CodeMerger");
        }

        public string GetProjectFolder(string projectName)
        {
            return Path.Combine(GetProjectsFolder(), projectName);
        }

        public List<Project> LoadAllProjects()
        {
            var projects = new List<Project>();
            string root = GetProjectsFolder();

            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                return projects;
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                string configPath = Path.Combine(dir, ConfigFileName);
                if (File.Exists(configPath))
                {
                    try
                    {
                        string json = File.ReadAllText(configPath);
                        var project = JsonSerializer.Deserialize<Project>(json);
                        if (project != null)
                        {
                            projects.Add(project);
                        }
                    }
                    catch
                    {
                        // Skip corrupted configs
                    }
                }
            }

            return projects;
        }

        public void SaveProject(Project project)
        {
            if (string.IsNullOrWhiteSpace(project.Name)) return;

            project.LastModifiedDate = DateTime.Now;
            string folder = GetProjectFolder(project.Name);
            Directory.CreateDirectory(folder);

            string configPath = Path.Combine(folder, ConfigFileName);
            string json = JsonSerializer.Serialize(project, JsonOptions);
            File.WriteAllText(configPath, json);
        }

        public void DeleteProject(string projectName)
        {
            string folder = GetProjectFolder(projectName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        public bool RenameProject(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return true;

            string oldFolder = GetProjectFolder(oldName);
            string newFolder = GetProjectFolder(newName);

            if (!Directory.Exists(oldFolder)) return false;
            if (Directory.Exists(newFolder)) return false;

            Directory.Move(oldFolder, newFolder);

            // Update config with new name
            string configPath = Path.Combine(newFolder, ConfigFileName);
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var project = JsonSerializer.Deserialize<Project>(json);
                if (project != null)
                {
                    project.Name = newName;
                    project.LastModifiedDate = DateTime.Now;
                    File.WriteAllText(configPath, JsonSerializer.Serialize(project, JsonOptions));
                }
            }

            return true;
        }

        public bool ProjectExists(string projectName)
        {
            return Directory.Exists(GetProjectFolder(projectName));
        }
    }
}
