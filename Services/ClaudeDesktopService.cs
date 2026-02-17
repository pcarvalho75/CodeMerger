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
        // Fixed entry name - Claude Desktop requires "mcpsrv_" prefix or UUID
        private const string EntryName = "mcpsrv_codemerger";

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

            // Remove any old entries (migration from old formats)
            var oldEntries = mcpServers
                .Where(kv => kv.Key.StartsWith("codemerger-") || kv.Key == "codemerger")
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldEntries)
            {
                mcpServers.Remove(key);
            }

            // Create/update the fixed entry with auto-approve for all tools
            var entry = new JsonObject
            {
                ["command"] = exePath,
                ["args"] = new JsonArray("--mcp"),
                ["alwaysAllow"] = new JsonArray(
                    "codemerger_get_project_overview", "codemerger_list_files", "codemerger_get_file",
                    "codemerger_search_code", "codemerger_get_type", "codemerger_get_dependencies",
                    "codemerger_find_implementations", "codemerger_grep", "codemerger_get_context",
                    "codemerger_get_lines", "codemerger_get_method_body", "codemerger_find_references", "codemerger_get_callers",
                    "codemerger_get_callees", "codemerger_str_replace", "codemerger_replace_lines",
                    "codemerger_write_file", "codemerger_delete_file", "codemerger_grep_replace",
                    "codemerger_undo", "codemerger_move_file", "codemerger_rename_symbol",
                    "codemerger_generate_interface", "codemerger_extract_method", "codemerger_add_parameter",
                    "codemerger_implement_interface", "codemerger_generate_constructor", "codemerger_build",
                    "codemerger_refresh", "codemerger_shutdown", "codemerger_list_projects",
                    "codemerger_switch_project", "codemerger_clean_backups", "codemerger_find_duplicates",
                    "codemerger_lessons", "codemerger_notes",
                    "codemerger_git_status", "codemerger_git_commit",
                    "codemerger_git_push",
                    "codemerger_help"
                )
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
        /// Ensures Claude Desktop config always points to the currently running exe.
        /// - If running from stable/ClickOnce: deploys stable copy and points config there.
        /// - If running from VS debug or other path: points config directly to current exe (no deploy).
        /// This means whichever instance you launch last "wins" the config.
        /// </summary>
        public bool SelfHeal()
        {
            bool changed = false;
            var currentExePath = GetCurrentExePath();
            var configuredPath = GetConfiguredExePath();
            
            bool isDebugRun = IsDebugRun();

            if (!isDebugRun)
            {
                // Production run: deploy stable copy as before
                try
                {
                    changed = DeployStableCopy();
                }
                catch
                {
                    // Deploy failed — continue to fix config anyway
                }

                // Point to stable path
                var stableExePath = GetStableExePath();
                if (!string.Equals(configuredPath, stableExePath, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureConfigured(stableExePath);
                    changed = true;
                }
            }
            else
            {
                // Debug/VS run: point config directly to the debug exe
                if (!string.Equals(configuredPath, currentExePath, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureConfigured(currentExePath);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Detects if the application is running from a development/debug environment.
        /// True if running from a bin\Debug or bin\Release folder.
        /// </summary>
        public bool IsDebugRun()
        {
            var path = GetCurrentExePath();
            return path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
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
