using System;
using System.Collections.Generic;

namespace CodeMerger.Models
{
    public class Workspace
    {
        public string Name { get; set; } = string.Empty;
        public List<string> InputDirectories { get; set; } = new List<string>();
        public List<string> DisabledDirectories { get; set; } = new List<string>();
        public string Extensions { get; set; } = ".cs, .xaml, .py, .csproj, .sln, .slnx, .json, .md, .props, .targets";
        public string IgnoredDirectories { get; set; } = "bin, obj, .vs, Properties, __pycache__, .venv";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime LastModifiedDate { get; set; } = DateTime.Now;

        // External git repositories
        public List<ExternalRepository> ExternalRepositories { get; set; } = new List<ExternalRepository>();
    }
}
