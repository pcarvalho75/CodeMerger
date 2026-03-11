using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>Parse a comma/semicolon/space-separated string into a trimmed, non-empty list.</summary>
        public static List<string> ParseList(string value)
        {
            return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        /// <summary>Parse Extensions into a list (e.g., [".cs", ".xaml"]).</summary>
        public List<string> ParseExtensions() => ParseList(Extensions);

        /// <summary>Parse IgnoredDirectories into a lowercase HashSet.</summary>
        public HashSet<string> ParseIgnoredDirs()
        {
            return ParseList(IgnoredDirectories + ",.git")
                .Select(d => d.ToLowerInvariant())
                .ToHashSet();
        }
    }
}
