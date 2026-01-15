using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace CodeMerger.Services
{
    public class GitRepositoryManager
    {
        public ObservableCollection<ExternalRepository> Repositories { get; } = new ObservableCollection<ExternalRepository>();

        private GitService? _gitService;

        public event Action<string>? OnProgress;
        public event Action<string>? OnError;
        public event Action? OnRepositoriesChanged;

        public void SetWorkspaceFolder(string workspaceFolder)
        {
            if (_gitService != null)
            {
                _gitService.OnProgress -= HandleGitProgress;
            }

            _gitService = new GitService(workspaceFolder);
            _gitService.OnProgress += HandleGitProgress;
        }

        private void HandleGitProgress(string message)
        {
            OnProgress?.Invoke(message);
        }

        public async Task<bool> AddRepositoryAsync(string url)
        {
            if (_gitService == null)
            {
                OnError?.Invoke("No workspace folder set.");
                return false;
            }

            if (!GitService.IsValidGitUrl(url))
            {
                OnError?.Invoke("Invalid Git URL. Use https://github.com/user/repo format.");
                return false;
            }

            if (Repositories.Any(r => r.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                OnError?.Invoke("Repository already added.");
                return false;
            }

            try
            {
                OnProgress?.Invoke("Cloning repository...");
                var repo = await _gitService.CloneOrPullAsync(url);
                Repositories.Add(repo);
                OnProgress?.Invoke($"Cloned {repo.Name} ({repo.Branch})");
                OnRepositoriesChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Clone failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RefreshRepositoryAsync(ExternalRepository repo)
        {
            if (_gitService == null || repo == null) return false;

            try
            {
                OnProgress?.Invoke($"Updating {repo.Name}...");
                await _gitService.CloneOrPullAsync(repo.Url);
                repo.LastUpdated = DateTime.Now;
                OnProgress?.Invoke($"Updated {repo.Name}");
                OnRepositoriesChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Update failed: {ex.Message}");
                return false;
            }
        }

        public bool RemoveRepository(ExternalRepository repo)
        {
            if (_gitService == null || repo == null) return false;

            try
            {
                _gitService.DeleteRepository(repo);
                Repositories.Remove(repo);
                OnProgress?.Invoke($"Removed {repo.Name}");
                OnRepositoriesChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Remove failed: {ex.Message}");
                return false;
            }
        }

        public async Task UpdateAllAsync()
        {
            if (_gitService == null || Repositories.Count == 0) return;

            foreach (var repo in Repositories.Where(r => r.IsEnabled))
            {
                try
                {
                    OnProgress?.Invoke($"Updating {repo.Name}...");
                    await _gitService.CloneOrPullAsync(repo.Url);
                    repo.LastUpdated = DateTime.Now;
                }
                catch (Exception ex)
                {
                    OnProgress?.Invoke($"Failed to update {repo.Name}: {ex.Message}");
                }
            }

            OnProgress?.Invoke(Repositories.Count > 0
                ? $"{Repositories.Count} repository(s) loaded"
                : "");
        }

        public System.Collections.Generic.List<string> GetAllFiles(string extensions, string ignoredDirs)
        {
            var files = new System.Collections.Generic.List<string>();
            if (_gitService == null) return files;

            foreach (var repo in Repositories.Where(r => r.IsEnabled))
            {
                files.AddRange(_gitService.GetRepositoryFiles(repo, extensions, ignoredDirs));
            }

            return files;
        }

        public void Load(System.Collections.Generic.List<ExternalRepository> repositories)
        {
            Repositories.Clear();
            foreach (var repo in repositories)
            {
                Repositories.Add(repo);
            }
        }

        public System.Collections.Generic.List<ExternalRepository> ToList()
        {
            return Repositories.ToList();
        }

        public void Clear()
        {
            Repositories.Clear();
        }

        public void NotifySelectionChanged()
        {
            OnRepositoriesChanged?.Invoke();
        }
    }
}
