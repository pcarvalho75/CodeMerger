using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeMerger.Services
{
    public class ClaudeDesktopService
    {
        private const string EntryPrefix = "codemerger-";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public string GetClaudeDesktopExePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Claude", "Claude.exe");
        }

        public string GetConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json");
        }

        public bool IsClaudeDesktopInstalled()
        {
            return File.Exists(GetClaudeDesktopExePath());
        }

        public bool ConfigExists()
        {
            return File.Exists(GetConfigPath());
        }

        public JsonObject ReadConfig()
        {
            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
            {
                return new JsonObject();
            }

            try
            {
                var json = File.ReadAllText(configPath);
                return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }

        public void WriteConfig(JsonObject config)
        {
            var configPath = GetConfigPath();
            var configDir = Path.GetDirectoryName(configPath);

            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            // Create backup if config exists
            if (File.Exists(configPath))
            {
                var backupPath = configPath + ".bak";
                File.Copy(configPath, backupPath, overwrite: true);
            }

            var json = config.ToJsonString(JsonOptions);
            File.WriteAllText(configPath, json);
        }

        public string GetEntryName(string projectName)
        {
            return EntryPrefix + projectName;
        }

        public string? GetEntryPath(string projectName)
        {
            var config = ReadConfig();
            var entryName = GetEntryName(projectName);

            if (config.TryGetPropertyValue("mcpServers", out var mcpServersNode) &&
                mcpServersNode is JsonObject mcpServers &&
                mcpServers.TryGetPropertyValue(entryName, out var entryNode) &&
                entryNode is JsonObject entry &&
                entry.TryGetPropertyValue("command", out var commandNode))
            {
                return commandNode?.ToString();
            }

            return null;
        }

        public bool IsProjectConfigured(string projectName)
        {
            return GetEntryPath(projectName) != null;
        }

        /// <summary>
        /// Returns list of all codemerger entries (project names only, without prefix)
        /// </summary>
        public List<string> GetAllConfiguredProjects()
        {
            var config = ReadConfig();
            var projects = new List<string>();

            if (config.TryGetPropertyValue("mcpServers", out var mcpServersNode) &&
                mcpServersNode is JsonObject mcpServers)
            {
                foreach (var entry in mcpServers)
                {
                    if (entry.Key.StartsWith(EntryPrefix))
                    {
                        projects.Add(entry.Key.Substring(EntryPrefix.Length));
                    }
                }
            }

            return projects;
        }

        public void UpsertProjectEntry(string projectName, string exePath)
        {
            var config = ReadConfig();
            var entryName = GetEntryName(projectName);

            // Ensure mcpServers object exists
            if (!config.TryGetPropertyValue("mcpServers", out var mcpServersNode) ||
                mcpServersNode is not JsonObject)
            {
                mcpServersNode = new JsonObject();
                config["mcpServers"] = mcpServersNode;
            }

            var mcpServers = mcpServersNode.AsObject();

            // Create or update entry for this project
            var entry = new JsonObject
            {
                ["command"] = exePath,
                ["args"] = new JsonArray("--mcp", projectName)
            };

            mcpServers[entryName] = entry;

            WriteConfig(config);
        }

        public void RemoveProjectEntry(string projectName)
        {
            var config = ReadConfig();
            var entryName = GetEntryName(projectName);

            if (config.TryGetPropertyValue("mcpServers", out var mcpServersNode) &&
                mcpServersNode is JsonObject mcpServers &&
                mcpServers.ContainsKey(entryName))
            {
                mcpServers.Remove(entryName);
                WriteConfig(config);
            }
        }

        /// <summary>
        /// Checks all codemerger entries and updates paths if they don't match current EXE.
        /// Returns number of entries updated.
        /// </summary>
        public int SelfHeal()
        {
            var currentExePath = GetCurrentExePath();
            var configuredProjects = GetAllConfiguredProjects();
            int updatedCount = 0;

            foreach (var projectName in configuredProjects)
            {
                var configuredPath = GetEntryPath(projectName);

                if (configuredPath != null &&
                    !string.Equals(configuredPath, currentExePath, StringComparison.OrdinalIgnoreCase))
                {
                    UpsertProjectEntry(projectName, currentExePath);
                    updatedCount++;
                }
            }

            return updatedCount;
        }

        public string GetCurrentExePath()
        {
            return Environment.ProcessPath ??
                   System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ??
                   "CodeMerger.exe";
        }

        public void OpenDownloadPage()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://claude.ai/download",
                UseShellExecute = true
            });
        }

        public void OpenConfigFolder()
        {
            var configDir = Path.GetDirectoryName(GetConfigPath());
            if (!string.IsNullOrEmpty(configDir))
            {
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }
                System.Diagnostics.Process.Start("explorer.exe", configDir);
            }
        }
    }
}