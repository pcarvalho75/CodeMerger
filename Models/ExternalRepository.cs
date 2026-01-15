using System;
using System.Collections.Generic;

namespace CodeMerger.Models
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
}
