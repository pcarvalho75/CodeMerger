using CodeMerger.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace CodeMerger.Services
{
    public class WorkspaceManager : INotifyPropertyChanged
    {
        private readonly WorkspaceService _workspaceService = new WorkspaceService();

        public ObservableCollection<Workspace> Workspaces { get; } = new ObservableCollection<Workspace>();

        private Workspace? _currentWorkspace;
        public Workspace? CurrentWorkspace
        {
            get => _currentWorkspace;
            private set
            {
                if (_currentWorkspace != value)
                {
                    _currentWorkspace = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWorkspace)));
                }
            }
        }

        public event Action<Workspace?>? OnWorkspaceChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void LoadWorkspaces()
        {
            var workspaces = _workspaceService.LoadAllWorkspaces();
            Workspaces.Clear();
            foreach (var w in workspaces)
            {
                Workspaces.Add(w);
            }
        }

        public Workspace? SelectWorkspace(string name)
        {
            var workspace = Workspaces.FirstOrDefault(w => w.Name == name);
            if (workspace != null)
            {
                CurrentWorkspace = workspace;
                _workspaceService.SetActiveWorkspace(name);
                OnWorkspaceChanged?.Invoke(workspace);
            }
            return workspace;
        }

        public Workspace? SelectWorkspace(Workspace workspace)
        {
            if (workspace == null) return null;
            return SelectWorkspace(workspace.Name);
        }

        public string? GetActiveWorkspaceName()
        {
            return _workspaceService.GetActiveWorkspace();
        }

        public Workspace? GetDefaultWorkspace()
        {
            var activeName = _workspaceService.GetActiveWorkspace();
            return Workspaces.FirstOrDefault(w => w.Name == activeName) ?? Workspaces.FirstOrDefault();
        }

        public Workspace? CreateWorkspace(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (_workspaceService.WorkspaceExists(name)) return null;

            var workspace = new Workspace { Name = name };
            _workspaceService.SaveWorkspace(workspace);
            Workspaces.Add(workspace);
            return workspace;
        }

        public bool RenameWorkspace(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;

            if (_workspaceService.RenameWorkspace(oldName, newName))
            {
                var workspace = Workspaces.FirstOrDefault(w => w.Name == oldName);
                if (workspace != null)
                {
                    workspace.Name = newName;
                }

                if (CurrentWorkspace?.Name == oldName)
                {
                    CurrentWorkspace = workspace;
                    _workspaceService.SetActiveWorkspace(newName);
                }

                // Reload to get fresh data
                LoadWorkspaces();
                return true;
            }
            return false;
        }

        public bool DeleteWorkspace(string name)
        {
            _workspaceService.DeleteWorkspace(name);

            var workspace = Workspaces.FirstOrDefault(w => w.Name == name);
            if (workspace != null)
            {
                Workspaces.Remove(workspace);
            }

            if (CurrentWorkspace?.Name == name)
            {
                CurrentWorkspace = null;
            }

            return true;
        }

        public void SaveCurrent()
        {
            if (CurrentWorkspace == null) return;
            _workspaceService.SaveWorkspace(CurrentWorkspace);
        }

        public void SaveWorkspace(Workspace workspace)
        {
            if (workspace == null) return;
            _workspaceService.SaveWorkspace(workspace);
        }

        public string GetWorkspaceFolder(string workspaceName)
        {
            return _workspaceService.GetWorkspaceFolder(workspaceName);
        }

        public string GetCurrentWorkspaceFolder()
        {
            if (CurrentWorkspace == null) return string.Empty;
            return _workspaceService.GetWorkspaceFolder(CurrentWorkspace.Name);
        }

        public bool WorkspaceExists(string name)
        {
            return _workspaceService.WorkspaceExists(name);
        }
    }
}
