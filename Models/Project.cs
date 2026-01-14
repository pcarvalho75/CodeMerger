using System;
using System.Collections.Generic;
using CodeMerger.Services;

namespace CodeMerger.Models
{
    public class Project
    {
        public string Name { get; set; } = string.Empty;
        public List<string> InputDirectories { get; set; } = new List<string>();
        public List<string> DisabledDirectories { get; set; } = new List<string>();
        public string Extensions { get; set; } = ".cs, .xaml, .py";
        public string IgnoredDirectories { get; set; } = "bin, obj, .vs, Properties, __pycache__, .venv";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime LastModifiedDate { get; set; } = DateTime.Now;

        // External git repositories
        public List<ExternalRepository> ExternalRepositories { get; set; } = new List<ExternalRepository>();
    }
}
