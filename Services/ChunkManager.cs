using System;
using System.Collections.Generic;
using System.Linq;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    public class ChunkManager
    {
        private readonly int _maxTokensPerChunk;

        public ChunkManager(int maxTokensPerChunk = 150000)
        {
            _maxTokensPerChunk = maxTokensPerChunk;
        }

        public List<Chunk> CreateChunks(List<FileAnalysis> files)
        {
            var chunks = new List<Chunk>();
            
            // Group files by classification for semantic grouping
            var groupedFiles = files
                .GroupBy(f => GetGroupingKey(f))
                .OrderBy(g => GetGroupPriority(g.Key))
                .ToList();

            var currentChunk = CreateNewChunk(chunks.Count + 1);
            chunks.Add(currentChunk);

            foreach (var group in groupedFiles)
            {
                foreach (var file in group.OrderBy(f => f.RelativePath))
                {
                    // If file alone exceeds limit, it gets its own chunk
                    if (file.EstimatedTokens > _maxTokensPerChunk)
                    {
                        // Finish current chunk if it has files
                        if (currentChunk.Files.Count > 0)
                        {
                            currentChunk = CreateNewChunk(chunks.Count + 1);
                            chunks.Add(currentChunk);
                        }

                        currentChunk.Files.Add(file);
                        currentChunk.TotalTokens = file.EstimatedTokens;
                        currentChunk.Description = $"Large file: {file.FileName}";

                        // Start new chunk for next files
                        currentChunk = CreateNewChunk(chunks.Count + 1);
                        chunks.Add(currentChunk);
                        continue;
                    }

                    // If adding this file exceeds limit, start new chunk
                    if (currentChunk.TotalTokens + file.EstimatedTokens > _maxTokensPerChunk)
                    {
                        currentChunk = CreateNewChunk(chunks.Count + 1);
                        chunks.Add(currentChunk);
                    }

                    currentChunk.Files.Add(file);
                    currentChunk.TotalTokens += file.EstimatedTokens;
                }
            }

            // Remove empty trailing chunk
            if (chunks.Count > 0 && chunks.Last().Files.Count == 0)
            {
                chunks.RemoveAt(chunks.Count - 1);
            }

            // Generate descriptions and cross-references
            foreach (var chunk in chunks)
            {
                chunk.Description = GenerateChunkDescription(chunk);
                chunk.CrossReferences = GenerateCrossReferences(chunk, chunks);
            }

            return chunks;
        }

        private Chunk CreateNewChunk(int number)
        {
            return new Chunk
            {
                ChunkNumber = number,
                Name = $"chunk_{number}",
                Files = new List<FileAnalysis>(),
                TotalTokens = 0
            };
        }

        private string GetGroupingKey(FileAnalysis file)
        {
            // Group by classification, but keep Views with their code-behind
            if (file.Classification == FileClassification.View)
                return "Views";

            if (file.Classification == FileClassification.ViewModel)
                return "ViewModels";

            if (file.Classification == FileClassification.Model)
                return "Models";

            if (file.Classification == FileClassification.Service)
                return "Services";

            if (file.Classification == FileClassification.Repository)
                return "Repositories";

            if (file.Classification == FileClassification.Controller)
                return "Controllers";

            if (file.Classification == FileClassification.Test)
                return "Tests";

            if (file.Classification == FileClassification.Config)
                return "Config";

            return "Other";
        }

        private int GetGroupPriority(string groupKey)
        {
            // Order: Config first (usually small), then core code, tests last
            return groupKey switch
            {
                "Config" => 0,
                "Models" => 1,
                "Services" => 2,
                "Repositories" => 3,
                "ViewModels" => 4,
                "Views" => 5,
                "Controllers" => 6,
                "Other" => 7,
                "Tests" => 8,
                _ => 9
            };
        }

        private string GenerateChunkDescription(Chunk chunk)
        {
            var classifications = chunk.Files
                .GroupBy(f => f.Classification)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key.ToString())
                .Take(3);

            return string.Join(" + ", classifications);
        }

        private List<string> GenerateCrossReferences(Chunk currentChunk, List<Chunk> allChunks)
        {
            var references = new List<string>();
            var currentTypes = currentChunk.Files
                .SelectMany(f => f.Types)
                .Select(t => t.Name)
                .ToHashSet();

            var currentDependencies = currentChunk.Files
                .SelectMany(f => f.Dependencies)
                .ToHashSet();

            foreach (var otherChunk in allChunks)
            {
                if (otherChunk.ChunkNumber == currentChunk.ChunkNumber)
                    continue;

                var otherTypes = otherChunk.Files
                    .SelectMany(f => f.Types)
                    .Select(t => t.Name)
                    .ToHashSet();

                // Find dependencies that exist in other chunk
                var sharedDeps = currentDependencies.Intersect(otherTypes).ToList();

                if (sharedDeps.Count > 0)
                {
                    var depList = sharedDeps.Count <= 3
                        ? string.Join(", ", sharedDeps)
                        : $"{string.Join(", ", sharedDeps.Take(3))}...";

                    references.Add($"Chunk {otherChunk.ChunkNumber}: {depList}");
                }
            }

            return references;
        }
    }
}
