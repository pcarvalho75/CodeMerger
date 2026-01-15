using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace CodeMerger.Services
{
    public class ExternalRepository
    {
        public string Url { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Branch { get; set; } = "main";
        public DateTime LastUpdated { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<string> IncludePaths { get; set; } = new(); // Empty = include all
        public List<string> ExcludePaths { get; set; } = new(); // Paths to exclude
    }

    public class GitService
    {
        private readonly string _reposFolder;

        public event Action<string>? OnProgress;

        public GitService(string projectFolder)
        {
            _reposFolder = Path.Combine(projectFolder, ".repos");
            Directory.CreateDirectory(_reposFolder);
        }

        public string GetReposFolder() => _reposFolder;

        /// <summary>
        /// Clone or pull a repository
        /// </summary>
        public async Task<ExternalRepository> CloneOrPullAsync(string url, bool shallowClone = true)
        {
            var repoName = ExtractRepoName(url);
            var localPath = Path.Combine(_reposFolder, repoName);

            return await Task.Run(() =>
            {
                if (Directory.Exists(localPath) && Repository.IsValid(localPath))
                {
                    // Pull existing repo
                    OnProgress?.Invoke($"Updating {repoName}...");
                    PullRepository(localPath);
                }
                else
                {
                    // Clone new repo
                    if (Directory.Exists(localPath))
                    {
                        Directory.Delete(localPath, true);
                    }

                    OnProgress?.Invoke($"Cloning {repoName}...");
                    CloneRepository(url, localPath, shallowClone);
                }

                var branch = GetCurrentBranch(localPath);

                return new ExternalRepository
                {
                    Url = url,
                    LocalPath = localPath,
                    Name = repoName,
                    Branch = branch,
                    LastUpdated = DateTime.Now,
                    IsEnabled = true
                };
            });
        }

        private void CloneRepository(string url, string localPath, bool shallow)
        {
            var options = new CloneOptions
            {
                RecurseSubmodules = false,
                OnCheckoutProgress = (path, completedSteps, totalSteps) =>
                {
                    if (totalSteps > 0)
                    {
                        int percent = (int)((completedSteps * 100) / totalSteps);
                        OnProgress?.Invoke($"Checkout: {percent}% ({completedSteps}/{totalSteps})");
                    }
                }
            };

            options.FetchOptions.OnTransferProgress = (progress) =>
            {
                if (progress.TotalObjects > 0)
                {
                    int percent = (int)((progress.ReceivedObjects * 100) / progress.TotalObjects);
                    OnProgress?.Invoke($"Downloading: {percent}% ({progress.ReceivedObjects}/{progress.TotalObjects})");
                }
                return true;
            };

            Repository.Clone(url, localPath, options);
            OnProgress?.Invoke($"Clone complete: {localPath}");
        }

        private void PullRepository(string localPath)
        {
            try
            {
                using var repo = new Repository(localPath);

                // Get the remote
                var remote = repo.Network.Remotes["origin"];
                if (remote == null)
                {
                    OnProgress?.Invoke("No origin remote found");
                    return;
                }

                // Fetch
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                {
                    OnProgress = (output) =>
                    {
                        OnProgress?.Invoke(output);
                        return true;
                    }
                }, "Fetching updates");

                // Get tracking branch
                var trackingBranch = repo.Head.TrackedBranch;
                if (trackingBranch == null)
                {
                    OnProgress?.Invoke("No tracking branch configured");
                    return;
                }

                // Fast-forward merge
                var signature = new Signature("CodeMerger", "codemerger@local", DateTimeOffset.Now);
                repo.Merge(trackingBranch, signature, new MergeOptions
                {
                    FastForwardStrategy = FastForwardStrategy.FastForwardOnly
                });

                OnProgress?.Invoke("Pull complete");
            }
            catch (Exception ex)
            {
                OnProgress?.Invoke($"Pull failed: {ex.Message}");
            }
        }

        private string GetCurrentBranch(string localPath)
        {
            try
            {
                using var repo = new Repository(localPath);
                return repo.Head.FriendlyName;
            }
            catch
            {
                return "main";
            }
        }

        /// <summary>
        /// Extract repository name from URL
        /// </summary>
        public static string ExtractRepoName(string url)
        {
            // Handle various URL formats:
            // https://github.com/user/repo.git
            // https://github.com/user/repo
            // git@github.com:user/repo.git

            var uri = url.TrimEnd('/');

            // Remove .git suffix
            if (uri.EndsWith(".git"))
            {
                uri = uri.Substring(0, uri.Length - 4);
            }

            // Get last segment
            var lastSlash = uri.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                return uri.Substring(lastSlash + 1);
            }

            // Handle SSH format
            var lastColon = uri.LastIndexOf(':');
            if (lastColon >= 0)
            {
                var path = uri.Substring(lastColon + 1);
                lastSlash = path.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    return path.Substring(lastSlash + 1);
                }
                return path;
            }

            return "repo";
        }

        /// <summary>
        /// Get all files from repository respecting .gitignore and filters
        /// </summary>
        public List<string> GetRepositoryFiles(ExternalRepository repo, string extensions, string ignoredDirs)
        {
            var files = new List<string>();
            if (!Directory.Exists(repo.LocalPath)) return files;

            var extList = extensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .ToList();

            var ignoredDirList = ignoredDirs.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().ToLowerInvariant())
                .ToHashSet();

            // Always ignore these
            ignoredDirList.Add(".git");
            ignoredDirList.Add("node_modules");
            ignoredDirList.Add("__pycache__");
            ignoredDirList.Add(".venv");
            ignoredDirList.Add("venv");
            ignoredDirList.Add("dist");
            ignoredDirList.Add("build");

            // Parse .gitignore if exists
            var gitignorePatterns = ParseGitignore(repo.LocalPath);

            try
            {
                var allFiles = Directory.EnumerateFiles(repo.LocalPath, "*", SearchOption.AllDirectories);

                foreach (var file in allFiles)
                {
                    var relativePath = file.Substring(repo.LocalPath.Length).TrimStart('\\', '/');
                    var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    // Check ignored directories
                    if (pathParts.Any(part => ignoredDirList.Contains(part.ToLowerInvariant())))
                        continue;

                    // Check .gitignore patterns
                    if (IsIgnoredByGitignore(relativePath, gitignorePatterns))
                        continue;

                    // Check include paths filter
                    if (repo.IncludePaths.Count > 0)
                    {
                        if (!repo.IncludePaths.Any(inc => relativePath.StartsWith(inc, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }

                    // Check exclude paths filter
                    if (repo.ExcludePaths.Any(exc => relativePath.StartsWith(exc, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Check extension
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extList.Count == 0 || extList.Contains("*.*") || extList.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress?.Invoke($"Error scanning {repo.Name}: {ex.Message}");
            }

            return files;
        }

        private List<string> ParseGitignore(string repoPath)
        {
            var patterns = new List<string>();
            var gitignorePath = Path.Combine(repoPath, ".gitignore");

            if (!File.Exists(gitignorePath)) return patterns;

            try
            {
                var lines = File.ReadAllLines(gitignorePath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    // Skip comments and empty lines
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    patterns.Add(trimmed);
                }
            }
            catch
            {
                // Ignore errors reading .gitignore
            }

            return patterns;
        }

        private bool IsIgnoredByGitignore(string relativePath, List<string> patterns)
        {
            var normalizedPath = relativePath.Replace('\\', '/');

            foreach (var pattern in patterns)
            {
                if (MatchesGitignorePattern(normalizedPath, pattern))
                    return true;
            }

            return false;
        }

        private bool MatchesGitignorePattern(string path, string pattern)
        {
            // Simple gitignore matching - handles common cases
            var p = pattern.TrimStart('/');

            // Directory pattern (ends with /)
            if (p.EndsWith("/"))
            {
                p = p.TrimEnd('/');
                var parts = path.Split('/');
                return parts.Any(part => part.Equals(p, StringComparison.OrdinalIgnoreCase));
            }

            // Wildcard pattern
            if (p.Contains("*"))
            {
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(p)
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*") + "$";

                return System.Text.RegularExpressions.Regex.IsMatch(path, regex, 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Exact match or directory name match
            if (path.Equals(p, StringComparison.OrdinalIgnoreCase))
                return true;

            var pathParts = path.Split('/');
            return pathParts.Any(part => part.Equals(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Delete a cloned repository
        /// </summary>
        public void DeleteRepository(ExternalRepository repo)
        {
            if (Directory.Exists(repo.LocalPath))
            {
                // Git repos have read-only files, need to clear attributes first
                SetAttributesNormal(new DirectoryInfo(repo.LocalPath));
                Directory.Delete(repo.LocalPath, true);
            }
        }

        private void SetAttributesNormal(DirectoryInfo dir)
        {
            foreach (var subDir in dir.GetDirectories())
            {
                SetAttributesNormal(subDir);
                subDir.Attributes = FileAttributes.Normal;
            }
            foreach (var file in dir.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
            }
        }

        /// <summary>
        /// Check if URL is a valid git repository URL
        /// </summary>
        public static bool IsValidGitUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // HTTPS URLs
            if (url.StartsWith("https://github.com/") ||
                url.StartsWith("https://gitlab.com/") ||
                url.StartsWith("https://bitbucket.org/") ||
                url.StartsWith("https://") && url.Contains(".git"))
                return true;

            // SSH URLs
            if (url.StartsWith("git@"))
                return true;

            return false;
        }
    }
}
