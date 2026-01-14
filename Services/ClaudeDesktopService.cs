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
        // Fixed entry name - no longer includes project name
        private const string EntryName = "codemerger";

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

        public string? GetConfiguredExePath()
        {
            var config = ReadConfig();

            if (config.TryGetPropertyValue("mcpServers", out var mcpServersNode) &&
                mcpServersNode is JsonObject mcpServers &&
                mcpServers.TryGetPropertyValue(EntryName, out var entryNode) &&
                entryNode is JsonObject entry &&
                entry.TryGetPropertyValue("command", out var commandNode))
            {
                return commandNode?.ToString();
            }

            return null;
        }

        public bool IsConfigured()
        {
            return GetConfiguredExePath() != null;
        }

        /// <summary>
        /// Ensures CodeMerger is configured in Claude Desktop.
        /// Uses fixed entry name "codemerger" with no project-specific args.
        /// The active project is determined by ProjectService.GetActiveProject().
        /// </summary>
        public void EnsureConfigured(string exePath)
        {
            var config = ReadConfig();

            // Ensure mcpServers object exists
            if (!config.TryGetPropertyValue("mcpServers", out var mcpServersNode) ||
                mcpServersNode is not JsonObject)
            {
                mcpServersNode = new JsonObject();
                config["mcpServers"] = mcpServersNode;
            }

            var mcpServers = mcpServersNode.AsObject();

            // Remove any old codemerger-* entries (migration from old format)
            var oldEntries = mcpServers
                .Where(kv => kv.Key.StartsWith("codemerger-"))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldEntries)
            {
                mcpServers.Remove(key);
            }

            // Create/update the fixed entry
            var entry = new JsonObject
            {
                ["command"] = exePath,
                ["args"] = new JsonArray("--mcp")
            };

            mcpServers[EntryName] = entry;

            WriteConfig(config);
        }

        public void RemoveConfig()
        {
            var config = ReadConfig();

            if (config.TryGetPropertyValue("mcpServers", out var mcpServersNode) &&
                mcpServersNode is JsonObject mcpServers &&
                mcpServers.ContainsKey(EntryName))
            {
                mcpServers.Remove(EntryName);
                WriteConfig(config);
            }
        }

        /// <summary>
        /// Checks if configured path matches current EXE and updates if needed.
        /// Returns true if config was updated.
        /// </summary>
        public bool SelfHeal()
        {
            var currentExePath = GetCurrentExePath();
            var configuredPath = GetConfiguredExePath();

            if (configuredPath == null)
            {
                // Not configured at all - configure it
                EnsureConfigured(currentExePath);
                return true;
            }

            if (!string.Equals(configuredPath, currentExePath, StringComparison.OrdinalIgnoreCase))
            {
                // Path mismatch - update it
                EnsureConfigured(currentExePath);
                return true;
            }

            return false;
        }

        public string GetCurrentExePath()
        {
            return Environment.ProcessPath ??
                   System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ??
                   "CodeMerger.exe";
        }

        /// <summary>
        /// Detects if the application is running from a ClickOnce deployment folder.
        /// ClickOnce installs to paths like: C:\Users\...\AppData\Local\Apps\2.0\...
        /// </summary>
        public bool IsClickOnceDeployment()
        {
            var path = GetCurrentExePath();
            return path.Contains(@"\Apps\2.0\");
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
