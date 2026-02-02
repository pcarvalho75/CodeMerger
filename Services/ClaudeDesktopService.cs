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

        /// <summary>
        /// Gets the stable directory where CodeMerger deploys itself.
        /// This path never changes, unlike ClickOnce's random Apps\2.0\ paths.
        /// </summary>
        public string GetStableDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeMerger", "current");
        }

        /// <summary>
        /// Gets the stable EXE path that Claude Desktop should always point to.
        /// </summary>
        public string GetStableExePath()
        {
            return Path.Combine(GetStableDirectory(), "CodeMerger.exe");
        }

        public bool IsClaudeDesktopInstalled()
        {
            return ConfigExists();
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
        /// Always points to the stable path so it survives ClickOnce updates.
        /// </summary>
        public void EnsureConfigured()
        {
            EnsureConfigured(GetStableExePath());
        }

        /// <summary>
        /// Ensures CodeMerger is configured in Claude Desktop with a specific path.
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
        /// Copies the running application and all its dependencies to the stable directory.
        /// Returns true if files were copied (new or updated).
        /// </summary>
        public bool DeployStableCopy()
        {
            var currentExePath = GetCurrentExePath();
            var sourceDir = Path.GetDirectoryName(currentExePath);
            var stableDir = GetStableDirectory();
            var stableExePath = GetStableExePath();

            if (string.IsNullOrEmpty(sourceDir)) return false;

            // Skip if we're already running from the stable directory
            if (sourceDir.Equals(stableDir, StringComparison.OrdinalIgnoreCase))
                return false;

            // Check if an update is needed by comparing the EXE write times
            if (File.Exists(stableExePath))
            {
                var sourceTime = File.GetLastWriteTimeUtc(currentExePath);
                var stableTime = File.GetLastWriteTimeUtc(stableExePath);
                if (sourceTime <= stableTime)
                    return false; // Stable copy is up to date
            }

            // Create stable directory
            Directory.CreateDirectory(stableDir);

            // Copy all files from source directory (EXE, DLLs, configs, etc.)
            var filesToCopy = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            int copied = 0;

            foreach (var sourceFile in filesToCopy)
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(stableDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                try
                {
                    File.Copy(sourceFile, destFile, overwrite: true);
                    copied++;
                }
                catch
                {
                    // File might be locked (e.g., another MCP instance running) — skip it
                }
            }

            return copied > 0;
        }

        /// <summary>
        /// Deploys to stable directory and ensures Claude Desktop config points there.
        /// Returns true if anything was updated.
        /// </summary>
        public bool SelfHeal()
        {
            bool changed = false;

            // Step 1: Deploy stable copy
            try
            {
                changed = DeployStableCopy();
            }
            catch
            {
                // Deploy failed — continue to fix config anyway
            }

            // Step 2: Ensure Claude Desktop config points to stable path
            var stableExePath = GetStableExePath();
            var configuredPath = GetConfiguredExePath();

            if (configuredPath == null)
            {
                // Not configured at all
                EnsureConfigured(stableExePath);
                changed = true;
            }
            else if (!string.Equals(configuredPath, stableExePath, StringComparison.OrdinalIgnoreCase))
            {
                // Points to wrong path (old ClickOnce path, or anything else)
                EnsureConfigured(stableExePath);
                changed = true;
            }

            return changed;
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
