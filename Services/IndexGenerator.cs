using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    public class IndexGenerator
    {
        public ProjectAnalysis BuildProjectAnalysis(string projectName, List<FileAnalysis> files, List<Chunk> chunks)
        {
            var analysis = new ProjectAnalysis
            {
                ProjectName = projectName,
                GeneratedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TotalFiles = files.Count,
                TotalChunks = chunks.Count,
                TotalTokens = files.Sum(f => f.EstimatedTokens),
                AllFiles = files,
                Chunks = chunks,
                DetectedFramework = DetectFramework(files)
            };

            analysis.TypeHierarchy = BuildTypeHierarchy(files);
            analysis.DependencyMap = BuildDependencyMap(files);

            return analysis;
        }

        public string GenerateMasterIndex(ProjectAnalysis analysis)
        {
            var sb = new StringBuilder();

            // Project Overview
            sb.AppendLine($"=== PROJECT: {analysis.ProjectName} ===");
            sb.AppendLine($"Generated: {analysis.GeneratedDate}");
            sb.AppendLine($"Total Files: {analysis.TotalFiles}");
            sb.AppendLine($"Total Chunks: {analysis.TotalChunks}");
            sb.AppendLine($"Total Tokens: {analysis.TotalTokens:N0}");
            sb.AppendLine($"Framework: {analysis.DetectedFramework}");
            sb.AppendLine();

            // Type Hierarchy
            sb.AppendLine("=== TYPE HIERARCHY ===");
            foreach (var type in analysis.TypeHierarchy.OrderBy(t => t.Key))
            {
                var inheritance = type.Value.Count > 0 ? $" : {string.Join(", ", type.Value)}" : "";
                sb.AppendLine($"{type.Key}{inheritance}");
            }
            sb.AppendLine();

            // Dependency Map
            sb.AppendLine("=== DEPENDENCY MAP ===");
            foreach (var dep in analysis.DependencyMap.Where(d => d.Value.Count > 0).OrderBy(d => d.Key))
            {
                sb.AppendLine($"{dep.Key}");
                sb.AppendLine($"  → uses: {string.Join(", ", dep.Value.Take(10))}{(dep.Value.Count > 10 ? "..." : "")}");
            }
            sb.AppendLine();

            // File Index
            sb.AppendLine("=== FILE INDEX ===");
            sb.AppendLine("| File | Chunk | Type | Key Members |");
            sb.AppendLine("|------|-------|------|-------------|");

            foreach (var chunk in analysis.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    var keyMembers = GetKeyMembers(file);
                    sb.AppendLine($"| {file.RelativePath} | {chunk.ChunkNumber} | {file.Classification} | {keyMembers} |");
                }
            }
            sb.AppendLine();

            // Chunk Manifest
            sb.AppendLine("=== CHUNK MANIFEST ===");
            foreach (var chunk in analysis.Chunks)
            {
                sb.AppendLine($"Chunk {chunk.ChunkNumber}: {chunk.Description} ({chunk.TotalTokens:N0} tokens)");
                if (chunk.CrossReferences.Count > 0)
                {
                    sb.AppendLine($"  References: {string.Join("; ", chunk.CrossReferences)}");
                }
            }

            return sb.ToString();
        }

        public string GenerateChunkContent(Chunk chunk, int totalChunks, List<FileAnalysis> allFiles)
        {
            var sb = new StringBuilder();

            // Chunk Header
            sb.AppendLine($"=== CHUNK {chunk.ChunkNumber} of {totalChunks}: {chunk.Description} ===");
            sb.AppendLine($"Files in this chunk: {string.Join(", ", chunk.Files.Select(f => f.FileName))}");

            if (chunk.CrossReferences.Count > 0)
            {
                sb.AppendLine($"See also: {string.Join("; ", chunk.CrossReferences)}");
            }
            sb.AppendLine();

            // Local Index
            sb.AppendLine("=== LOCAL INDEX ===");
            foreach (var file in chunk.Files)
            {
                sb.AppendLine($"{file.RelativePath}");

                foreach (var type in file.Types)
                {
                    var inheritance = !string.IsNullOrEmpty(type.BaseType) || type.Interfaces.Count > 0
                        ? $" : {string.Join(", ", new[] { type.BaseType }.Concat(type.Interfaces).Where(s => !string.IsNullOrEmpty(s)))}"
                        : "";

                    sb.AppendLine($"  {type.Kind.ToString().ToLower()} {type.Name}{inheritance}");

                    // Show public/internal members only
                    var visibleMembers = type.Members
                        .Where(m => m.AccessModifier == "public" || m.AccessModifier == "internal")
                        .Take(10)
                        .ToList();

                    foreach (var member in visibleMembers)
                    {
                        var signature = !string.IsNullOrEmpty(member.Signature) ? member.Signature : member.Name;
                        var returnType = !string.IsNullOrEmpty(member.ReturnType) ? $" : {member.ReturnType}" : "";
                        sb.AppendLine($"    - {signature}{returnType}");
                    }

                    if (type.Members.Count > visibleMembers.Count)
                    {
                        sb.AppendLine($"    - ... ({type.Members.Count - visibleMembers.Count} more members)");
                    }
                }
                sb.AppendLine();
            }

            // File Contents
            sb.AppendLine("=== FILES ===");
            sb.AppendLine();

            foreach (var file in chunk.Files)
            {
                sb.AppendLine($"// --- {file.RelativePath} ---");
                sb.AppendLine($"```{file.Language}");

                try
                {
                    var content = File.ReadAllText(file.FilePath);
                    sb.AppendLine(content);
                }
                catch
                {
                    sb.AppendLine("// Error reading file content");
                }

                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string DetectFramework(List<FileAnalysis> files)
        {
            var allUsings = files.SelectMany(f => f.Usings).ToHashSet();
            var allBaseTypes = files.SelectMany(f => f.Types).Select(t => t.BaseType).ToHashSet();

            if (allUsings.Any(u => u.Contains("Microsoft.AspNetCore")))
                return "ASP.NET Core";

            if (allUsings.Any(u => u.Contains("System.Web.Mvc")))
                return "ASP.NET MVC";

            if (allBaseTypes.Any(b => b?.Contains("Window") == true) || files.Any(f => f.Extension == ".xaml"))
                return "WPF";

            if (allUsings.Any(u => u.Contains("Xamarin")) || allUsings.Any(u => u.Contains("Microsoft.Maui")))
                return "MAUI/Xamarin";

            if (allUsings.Any(u => u.Contains("Microsoft.Azure.Functions")))
                return "Azure Functions";

            if (files.All(f => f.Extension == ".cs"))
                return ".NET Class Library";

            return ".NET Application";
        }

        private Dictionary<string, List<string>> BuildTypeHierarchy(List<FileAnalysis> files)
        {
            var hierarchy = new Dictionary<string, List<string>>();

            foreach (var file in files)
            {
                foreach (var type in file.Types)
                {
                    var inheritance = new List<string>();

                    if (!string.IsNullOrEmpty(type.BaseType))
                        inheritance.Add(type.BaseType);

                    inheritance.AddRange(type.Interfaces);

                    hierarchy[type.Name] = inheritance;
                }
            }

            return hierarchy;
        }

        private Dictionary<string, List<string>> BuildDependencyMap(List<FileAnalysis> files)
        {
            var depMap = new Dictionary<string, List<string>>();

            foreach (var file in files)
            {
                foreach (var type in file.Types)
                {
                    var deps = new HashSet<string>();

                    // Add base type and interfaces
                    if (!string.IsNullOrEmpty(type.BaseType))
                        deps.Add(type.BaseType);

                    foreach (var iface in type.Interfaces)
                        deps.Add(iface);

                    // Add from file dependencies
                    foreach (var dep in file.Dependencies)
                        deps.Add(dep);

                    depMap[type.Name] = deps.ToList();
                }
            }

            return depMap;
        }

        private string GetKeyMembers(FileAnalysis file)
        {
            var members = file.Types
                .SelectMany(t => t.Members)
                .Where(m => m.AccessModifier == "public" && m.Kind == CodeMemberKind.Method)
                .Take(3)
                .Select(m => m.Signature ?? m.Name);

            var result = string.Join(", ", members);
            return result.Length > 50 ? result.Substring(0, 47) + "..." : result;
        }
    }
}
