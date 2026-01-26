using System.Collections.Generic;

namespace CodeMerger.Models
{
    public class ProjectReference
    {
        public string SourceProject { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string ResolvedPath { get; set; } = string.Empty;
        public string Name => System.IO.Path.GetFileNameWithoutExtension(RelativePath);
    }

    public class Chunk
    {
        public int ChunkNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<FileAnalysis> Files { get; set; } = new List<FileAnalysis>();
        public int TotalTokens { get; set; }
        public List<string> CrossReferences { get; set; } = new List<string>();
    }

    public class WorkspaceAnalysis
    {
        public string WorkspaceName { get; set; } = string.Empty;
        public string GeneratedDate { get; set; } = string.Empty;
        public int TotalFiles { get; set; }
        public int TotalChunks { get; set; }
        public int TotalTokens { get; set; }
        public string DetectedFramework { get; set; } = string.Empty;

        public List<FileAnalysis> AllFiles { get; set; } = new List<FileAnalysis>();
        public List<Chunk> Chunks { get; set; } = new List<Chunk>();
        public List<ProjectReference> ProjectReferences { get; set; } = new List<ProjectReference>();
        public Dictionary<string, List<string>> TypeHierarchy { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> DependencyMap { get; set; } = new Dictionary<string, List<string>>();
    }
}
