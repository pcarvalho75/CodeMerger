using CodeMerger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeMerger.Services
{
    public class WorkspaceService
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeMerger");

        public const string ConfigFileName = "workspace.json";
        private const string ActiveWorkspaceFileName = "active_workspace.txt";

        public WorkspaceService()
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
        }

        public string GetWorkspaceFolder(string workspaceName)
        {
            return Path.Combine(AppDataFolder, workspaceName);
        }

        private string GetWorkspaceConfigPath(string workspaceName)
        {
            return Path.Combine(GetWorkspaceFolder(workspaceName), ConfigFileName);
        }

        private string GetActiveWorkspaceFilePath()
        {
            return Path.Combine(AppDataFolder, ActiveWorkspaceFileName);
        }

        public bool WorkspaceExists(string workspaceName)
        {
            return File.Exists(GetWorkspaceConfigPath(workspaceName));
        }

        public List<Workspace> LoadAllWorkspaces()
        {
            var workspaces = new List<Workspace>();

            if (!Directory.Exists(AppDataFolder)) return workspaces;

            foreach (var dir in Directory.GetDirectories(AppDataFolder))
            {
                var configPath = Path.Combine(dir, ConfigFileName);
                if (File.Exists(configPath))
                {
                    try
                    {
                        var json = File.ReadAllText(configPath);
                        var workspace = JsonSerializer.Deserialize<Workspace>(json);
                        if (workspace != null)
                        {
                            if (MergeDefaultExtensions(workspace))
                                SaveWorkspace(workspace);
                            workspaces.Add(workspace);
                        }
                    }
                    catch
                    {
                        // Skip invalid workspace files
                    }
                }
            }

            // Migration: also check for old project.json files
            foreach (var dir in Directory.GetDirectories(AppDataFolder))
            {
                var oldConfigPath = Path.Combine(dir, "project.json");
                var newConfigPath = Path.Combine(dir, ConfigFileName);
                
                if (File.Exists(oldConfigPath) && !File.Exists(newConfigPath))
                {
                    try
                    {
                        var json = File.ReadAllText(oldConfigPath);
                        var workspace = JsonSerializer.Deserialize<Workspace>(json);
                        if (workspace != null)
                        {
                            workspaces.Add(workspace);
                            // Save with new filename
                            SaveWorkspace(workspace);
                            // Remove old file
                            File.Delete(oldConfigPath);
                        }
                    }
                    catch
                    {
                        // Skip invalid files
                    }
                }
            }

            return workspaces;
        }

        public Workspace? LoadWorkspace(string workspaceName)
        {
            var configPath = GetWorkspaceConfigPath(workspaceName);
            
            // Migration: check for old project.json
            if (!File.Exists(configPath))
            {
                var oldConfigPath = Path.Combine(GetWorkspaceFolder(workspaceName), "project.json");
                if (File.Exists(oldConfigPath))
                {
                    configPath = oldConfigPath;
                }
            }
            
            if (!File.Exists(configPath)) return null;

            try
            {
                var json = File.ReadAllText(configPath);
                var workspace = JsonSerializer.Deserialize<Workspace>(json);
                if (workspace != null && MergeDefaultExtensions(workspace))
                    SaveWorkspace(workspace);
                return workspace;
            }
            catch
            {
                return null;
            }
        }

        public void SaveWorkspace(Workspace workspace)
        {
            var folder = GetWorkspaceFolder(workspace.Name);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            workspace.LastModifiedDate = DateTime.Now;

            var configPath = GetWorkspaceConfigPath(workspace.Name);
            var json = JsonSerializer.Serialize(workspace, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        public bool RenameWorkspace(string oldName, string newName)
        {
            if (WorkspaceExists(newName)) return false;

            var oldFolder = GetWorkspaceFolder(oldName);
            var newFolder = GetWorkspaceFolder(newName);

            if (!Directory.Exists(oldFolder)) return false;

            try
            {
                Directory.Move(oldFolder, newFolder);

                // Update workspace name in config
                var workspace = LoadWorkspace(newName);
                if (workspace != null)
                {
                    workspace.Name = newName;
                    SaveWorkspace(workspace);
                }

                // Update active workspace if it was the renamed one
                if (GetActiveWorkspace() == oldName)
                {
                    SetActiveWorkspace(newName);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void DeleteWorkspace(string workspaceName)
        {
            var folder = GetWorkspaceFolder(workspaceName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            // Clear active workspace if it was deleted
            if (GetActiveWorkspace() == workspaceName)
            {
                ClearActiveWorkspace();
            }
        }

        /// <summary>
        /// Gets the currently active workspace name for MCP mode.
        /// Returns null if no active workspace is set.
        /// </summary>
        public string? GetActiveWorkspace()
        {
            var filePath = GetActiveWorkspaceFilePath();
            
            // Migration: check for old active_project.txt
            if (!File.Exists(filePath))
            {
                var oldFilePath = Path.Combine(AppDataFolder, "active_project.txt");
                if (File.Exists(oldFilePath))
                {
                    filePath = oldFilePath;
                }
            }
            
            if (!File.Exists(filePath)) return null;

            try
            {
                var workspaceName = File.ReadAllText(filePath).Trim();
                
                // Validate that the workspace still exists
                if (!string.IsNullOrEmpty(workspaceName) && WorkspaceExists(workspaceName))
                {
                    return workspaceName;
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the active workspace for MCP mode.
        /// Called when user switches workspaces in the GUI.
        /// </summary>
        public void SetActiveWorkspace(string workspaceName)
        {
            var filePath = GetActiveWorkspaceFilePath();
            File.WriteAllText(filePath, workspaceName);
        }

        /// <summary>
        /// Merges any missing default extensions into an existing workspace.
        /// Returns true if the workspace was modified (needs saving).
        /// </summary>
        private static bool MergeDefaultExtensions(Workspace workspace)
        {
            var defaults = new Workspace();
            var defaultExts = new HashSet<string>(
                defaults.Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var currentExts = new HashSet<string>(
                workspace.Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var missing = defaultExts.Where(e => !currentExts.Contains(e)).ToList();
            if (missing.Count == 0) return false;

            workspace.Extensions = workspace.Extensions.TrimEnd().TrimEnd(',')
                + ", " + string.Join(", ", missing);
            return true;
        }

        /// <summary>
        /// Clears the active workspace setting.
        /// </summary>
        public void ClearActiveWorkspace()
        {
            var filePath = GetActiveWorkspaceFilePath();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
