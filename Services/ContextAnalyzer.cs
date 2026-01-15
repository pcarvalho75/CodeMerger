using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Provides intelligent context analysis for task-based code retrieval.
    /// Analyzes task descriptions and returns relevant files ranked by relevance.
    /// </summary>
    public class ContextAnalyzer
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;

        public ContextAnalyzer(WorkspaceAnalysis workspaceAnalysis)
        {
            _workspaceAnalysis = workspaceAnalysis;
        }

        /// <summary>
        /// Analyzes a task description and returns relevant files with context.
        /// </summary>
        public TaskContextResult GetContextForTask(string taskDescription, int maxFiles = 10, int maxTokens = 50000)
        {
            var result = new TaskContextResult
            {
                TaskDescription = taskDescription,
                AnalyzedAt = DateTime.Now
            };

            // Extract keywords and concepts from task description
            var keywords = ExtractKeywords(taskDescription);
            result.ExtractedKeywords = keywords;

            // Score all files based on relevance
            var scoredFiles = new List<ScoredFile>();

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                var score = CalculateRelevanceScore(file, keywords, taskDescription);
                if (score > 0)
                {
                    scoredFiles.Add(new ScoredFile
                    {
                        File = file,
                        Score = score,
                        MatchReasons = GetMatchReasons(file, keywords, taskDescription)
                    });
                }
            }

            // Sort by score and apply limits
            var rankedFiles = scoredFiles
                .OrderByDescending(f => f.Score)
                .ToList();

            // Apply token budget
            int currentTokens = 0;
            var selectedFiles = new List<ScoredFile>();

            foreach (var scoredFile in rankedFiles)
            {
                if (selectedFiles.Count >= maxFiles)
                    break;

                if (currentTokens + scoredFile.File.EstimatedTokens > maxTokens && selectedFiles.Count > 0)
                    continue; // Skip large files if we already have some

                selectedFiles.Add(scoredFile);
                currentTokens += scoredFile.File.EstimatedTokens;
            }

            result.RelevantFiles = selectedFiles;
            result.TotalTokens = currentTokens;

            // Generate suggestions based on task type
            result.Suggestions = GenerateSuggestions(taskDescription, selectedFiles);

            // Find related types that might be useful
            result.RelatedTypes = FindRelatedTypes(selectedFiles);

            return result;
        }

        /// <summary>
        /// Searches file contents for a pattern and returns matches with context.
        /// </summary>
        public ContentSearchResult SearchContent(string pattern, bool isRegex = true, bool caseSensitive = false, int contextLines = 2, int maxResults = 50)
        {
            var result = new ContentSearchResult
            {
                Pattern = pattern,
                IsRegex = isRegex,
                CaseSensitive = caseSensitive
            };

            var matches = new List<ContentMatch>();
            var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = isRegex
                    ? new Regex(pattern, regexOptions | RegexOptions.Compiled)
                    : new Regex(Regex.Escape(pattern), regexOptions | RegexOptions.Compiled);
            }
            catch (ArgumentException ex)
            {
                result.Error = $"Invalid regex pattern: {ex.Message}";
                return result;
            }

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                if (matches.Count >= maxResults)
                    break;

                try
                {
                    var lines = File.ReadAllLines(file.FilePath);

                    for (int i = 0; i < lines.Length && matches.Count < maxResults; i++)
                    {
                        var line = lines[i];
                        var match = regex.Match(line);

                        if (match.Success)
                        {
                            // Get context lines
                            var contextBefore = new List<string>();
                            var contextAfter = new List<string>();

                            for (int j = Math.Max(0, i - contextLines); j < i; j++)
                                contextBefore.Add(lines[j]);

                            for (int j = i + 1; j <= Math.Min(lines.Length - 1, i + contextLines); j++)
                                contextAfter.Add(lines[j]);

                            matches.Add(new ContentMatch
                            {
                                FilePath = file.RelativePath,
                                LineNumber = i + 1,
                                Line = line,
                                MatchStart = match.Index,
                                MatchLength = match.Length,
                                MatchedText = match.Value,
                                ContextBefore = contextBefore,
                                ContextAfter = contextAfter,
                                FileClassification = file.Classification.ToString()
                            });
                        }
                    }
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            result.Matches = matches;
            result.TotalMatches = matches.Count;
            result.FilesSearched = _workspaceAnalysis.AllFiles.Count;

            return result;
        }

        private List<string> ExtractKeywords(string taskDescription)
        {
            var keywords = new List<string>();
            var text = taskDescription.ToLowerInvariant();

            // Common programming concepts to look for
            var conceptPatterns = new Dictionary<string, string[]>
            {
                { "mcp", new[] { "mcp", "tool", "server", "protocol" } },
                { "ui", new[] { "ui", "view", "window", "xaml", "button", "dialog", "form" } },
                { "data", new[] { "model", "data", "entity", "dto", "database", "repository" } },
                { "service", new[] { "service", "api", "endpoint", "handler" } },
                { "analysis", new[] { "analyze", "parse", "process", "scan" } },
                { "file", new[] { "file", "read", "write", "load", "save", "io" } },
                { "search", new[] { "search", "find", "query", "filter", "match" } },
                { "dependency", new[] { "dependency", "reference", "import", "using" } },
                { "type", new[] { "type", "class", "interface", "struct", "enum" } },
                { "method", new[] { "method", "function", "call", "invoke" } },
                { "config", new[] { "config", "setting", "option", "preference" } },
                { "test", new[] { "test", "unit", "spec", "mock", "assert" } },
                { "error", new[] { "error", "exception", "catch", "try", "handle" } },
                { "async", new[] { "async", "await", "task", "thread", "parallel" } },
                { "json", new[] { "json", "serialize", "deserialize", "parse" } },
                { "index", new[] { "index", "generate", "build", "create" } }
            };

            foreach (var concept in conceptPatterns)
            {
                if (concept.Value.Any(term => text.Contains(term)))
                {
                    keywords.Add(concept.Key);
                }
            }

            // Extract specific identifiers (PascalCase, camelCase words)
            var identifierPattern = new Regex(@"\b([A-Z][a-zA-Z0-9]*|[a-z][a-zA-Z0-9]*[A-Z][a-zA-Z0-9]*)\b");
            var identifiers = identifierPattern.Matches(taskDescription)
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => w.Length > 2)
                .Distinct();

            keywords.AddRange(identifiers);

            // Extract quoted terms
            var quotedPattern = new Regex(@"[""']([^""']+)[""']");
            var quoted = quotedPattern.Matches(taskDescription)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value);

            keywords.AddRange(quoted);

            return keywords.Distinct().ToList();
        }

        private double CalculateRelevanceScore(FileAnalysis file, List<string> keywords, string taskDescription)
        {
            double score = 0;
            var taskLower = taskDescription.ToLowerInvariant();
            var fileLower = file.RelativePath.ToLowerInvariant();
            var fileNameLower = file.FileName.ToLowerInvariant();

            // Direct file name mention (highest weight)
            foreach (var keyword in keywords)
            {
                if (fileNameLower.Contains(keyword.ToLowerInvariant()))
                    score += 10;
            }

            // Type name matches
            foreach (var type in file.Types)
            {
                var typeLower = type.Name.ToLowerInvariant();
                foreach (var keyword in keywords)
                {
                    if (typeLower.Contains(keyword.ToLowerInvariant()))
                        score += 8;
                }

                // Method name matches
                foreach (var member in type.Members)
                {
                    var memberLower = member.Name.ToLowerInvariant();
                    foreach (var keyword in keywords)
                    {
                        if (memberLower.Contains(keyword.ToLowerInvariant()))
                            score += 3;
                    }
                }
            }

            // Classification bonus based on task type
            if (taskLower.Contains("tool") || taskLower.Contains("mcp"))
            {
                if (fileLower.Contains("mcp") || fileLower.Contains("server"))
                    score += 15;
            }

            if (taskLower.Contains("ui") || taskLower.Contains("view") || taskLower.Contains("window"))
            {
                if (file.Classification == FileClassification.View)
                    score += 10;
            }

            if (taskLower.Contains("model") || taskLower.Contains("data"))
            {
                if (file.Classification == FileClassification.Model)
                    score += 10;
            }

            if (taskLower.Contains("service") || taskLower.Contains("logic"))
            {
                if (file.Classification == FileClassification.Service)
                    score += 10;
            }

            if (taskLower.Contains("analyze") || taskLower.Contains("parse"))
            {
                if (fileLower.Contains("analyzer") || fileLower.Contains("parser"))
                    score += 12;
            }

            // Dependency-based scoring - if a high-scoring file depends on this one
            foreach (var type in file.Types)
            {
                if (_workspaceAnalysis.DependencyMap.Values.Any(deps => deps.Contains(type.Name)))
                {
                    score += 2; // Files that others depend on are valuable context
                }
            }

            return score;
        }

        private List<string> GetMatchReasons(FileAnalysis file, List<string> keywords, string taskDescription)
        {
            var reasons = new List<string>();
            var taskLower = taskDescription.ToLowerInvariant();

            foreach (var type in file.Types)
            {
                foreach (var keyword in keywords)
                {
                    if (type.Name.ToLowerInvariant().Contains(keyword.ToLowerInvariant()))
                    {
                        reasons.Add($"Type '{type.Name}' matches keyword '{keyword}'");
                    }
                }
            }

            if (taskLower.Contains("tool") && file.RelativePath.ToLowerInvariant().Contains("mcp"))
            {
                reasons.Add("MCP server file - tools are defined here");
            }

            if (file.Classification == FileClassification.View && taskLower.Contains("ui"))
            {
                reasons.Add("View file relevant to UI task");
            }

            if (file.Classification == FileClassification.Model && taskLower.Contains("model"))
            {
                reasons.Add("Model file relevant to data task");
            }

            // Check if this file is a dependency of other relevant files
            var usedBy = _workspaceAnalysis.DependencyMap
                .Where(kvp => kvp.Value.Any(dep => file.Types.Any(t => t.Name == dep)))
                .Select(kvp => kvp.Key)
                .Take(3)
                .ToList();

            if (usedBy.Any())
            {
                reasons.Add($"Used by: {string.Join(", ", usedBy)}");
            }

            return reasons.Distinct().Take(5).ToList();
        }

        private List<string> GenerateSuggestions(string taskDescription, List<ScoredFile> selectedFiles)
        {
            var suggestions = new List<string>();
            var taskLower = taskDescription.ToLowerInvariant();

            // Task-specific suggestions
            if (taskLower.Contains("add") && taskLower.Contains("tool"))
            {
                suggestions.Add("Look at HandleListTools() in McpServer.cs to see tool registration pattern");
                suggestions.Add("Add your tool to the tools array in HandleListTools()");
                suggestions.Add("Implement the handler method following the pattern of existing handlers");
                suggestions.Add("Add the case to the switch statement in HandleToolCall()");
            }

            if (taskLower.Contains("search") || taskLower.Contains("find"))
            {
                suggestions.Add("Consider using Regex for flexible pattern matching");
                suggestions.Add("Return line numbers and context for better usability");
            }

            if (taskLower.Contains("model") || taskLower.Contains("class"))
            {
                var modelFiles = selectedFiles.Where(f => f.File.Classification == FileClassification.Model).ToList();
                if (modelFiles.Any())
                {
                    suggestions.Add($"Review existing models in {modelFiles.First().File.RelativePath} for patterns");
                }
            }

            // General suggestions based on what was found
            if (!selectedFiles.Any())
            {
                suggestions.Add("No directly relevant files found - task may require new components");
                suggestions.Add("Consider which classification (Service, Model, View) fits best");
            }

            if (selectedFiles.Sum(f => f.File.EstimatedTokens) > 30000)
            {
                suggestions.Add("Large context selected - focus on specific types/methods to reduce scope");
            }

            return suggestions;
        }

        private List<string> FindRelatedTypes(List<ScoredFile> selectedFiles)
        {
            var relatedTypes = new HashSet<string>();

            foreach (var scoredFile in selectedFiles)
            {
                foreach (var type in scoredFile.File.Types)
                {
                    // Add base types
                    if (!string.IsNullOrEmpty(type.BaseType))
                        relatedTypes.Add(type.BaseType);

                    // Add interfaces
                    relatedTypes.UnionWith(type.Interfaces);

                    // Add types from dependency map
                    if (_workspaceAnalysis.DependencyMap.TryGetValue(type.Name, out var deps))
                    {
                        relatedTypes.UnionWith(deps.Take(5));
                    }
                }
            }

            // Filter to only types that exist in the project
            var projectTypes = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types)
                .Select(t => t.Name)
                .ToHashSet();

            return relatedTypes
                .Where(t => projectTypes.Contains(t))
                .Take(10)
                .ToList();
        }
    }

    #region Result Models

    public class TaskContextResult
    {
        public string TaskDescription { get; set; } = string.Empty;
        public DateTime AnalyzedAt { get; set; }
        public List<string> ExtractedKeywords { get; set; } = new();
        public List<ScoredFile> RelevantFiles { get; set; } = new();
        public int TotalTokens { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public List<string> RelatedTypes { get; set; } = new();

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# Context Analysis for Task");
            sb.AppendLine();
            sb.AppendLine($"**Task:** {TaskDescription}");
            sb.AppendLine();
            sb.AppendLine($"**Analyzed:** {AnalyzedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"**Total Tokens:** {TotalTokens:N0}");
            sb.AppendLine();

            if (ExtractedKeywords.Any())
            {
                sb.AppendLine("## Detected Keywords");
                sb.AppendLine(string.Join(", ", ExtractedKeywords.Select(k => $"`{k}`")));
                sb.AppendLine();
            }

            if (Suggestions.Any())
            {
                sb.AppendLine("## Suggestions");
                foreach (var suggestion in Suggestions)
                {
                    sb.AppendLine($"- {suggestion}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Relevant Files (by relevance)");
            sb.AppendLine();
            sb.AppendLine("| # | File | Score | Tokens | Reasons |");
            sb.AppendLine("|---|------|-------|--------|---------|");

            int i = 1;
            foreach (var file in RelevantFiles)
            {
                var reasons = file.MatchReasons.Any()
                    ? string.Join("; ", file.MatchReasons.Take(2))
                    : "-";
                sb.AppendLine($"| {i++} | `{file.File.RelativePath}` | {file.Score:F1} | {file.File.EstimatedTokens:N0} | {reasons} |");
            }
            sb.AppendLine();

            if (RelatedTypes.Any())
            {
                sb.AppendLine("## Related Types to Consider");
                sb.AppendLine(string.Join(", ", RelatedTypes.Select(t => $"`{t}`")));
            }

            return sb.ToString();
        }
    }

    public class ScoredFile
    {
        public FileAnalysis File { get; set; } = null!;
        public double Score { get; set; }
        public List<string> MatchReasons { get; set; } = new();
    }

    public class ContentSearchResult
    {
        public string Pattern { get; set; } = string.Empty;
        public bool IsRegex { get; set; }
        public bool CaseSensitive { get; set; }
        public List<ContentMatch> Matches { get; set; } = new();
        public int TotalMatches { get; set; }
        public int FilesSearched { get; set; }
        public string? Error { get; set; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(Error))
            {
                sb.AppendLine($"**Error:** {Error}");
                return sb.ToString();
            }

            sb.AppendLine($"# Search Results: `{Pattern}`");
            sb.AppendLine();
            sb.AppendLine($"**Found:** {TotalMatches} matches in {Matches.Select(m => m.FilePath).Distinct().Count()} files");
            sb.AppendLine($"**Files searched:** {FilesSearched}");
            sb.AppendLine($"**Options:** Regex={IsRegex}, CaseSensitive={CaseSensitive}");
            sb.AppendLine();

            var groupedByFile = Matches.GroupBy(m => m.FilePath);

            foreach (var fileGroup in groupedByFile)
            {
                sb.AppendLine($"## `{fileGroup.Key}`");
                sb.AppendLine();

                foreach (var match in fileGroup)
                {
                    sb.AppendLine($"**Line {match.LineNumber}:** matched `{match.MatchedText}`");
                    sb.AppendLine("```");

                    foreach (var ctx in match.ContextBefore)
                    {
                        sb.AppendLine(VisualizeWhitespace(ctx));
                    }

                    sb.AppendLine($"> {VisualizeWhitespace(match.Line)}");

                    foreach (var ctx in match.ContextAfter)
                    {
                        sb.AppendLine(VisualizeWhitespace(ctx));
                    }

                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Makes leading whitespace visible by showing tabs as → and preserving spaces.
        /// </summary>
        private static string VisualizeWhitespace(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            // Find the end of leading whitespace
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                i++;

            if (i == 0) return line;

            // Visualize leading whitespace: tabs become →, spaces stay as spaces
            var leading = line.Substring(0, i).Replace("\t", "→");
            var rest = line.Substring(i);

            return leading + rest;
        }
    }

    public class ContentMatch
    {
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Line { get; set; } = string.Empty;
        public int MatchStart { get; set; }
        public int MatchLength { get; set; }
        public string MatchedText { get; set; } = string.Empty;
        public List<string> ContextBefore { get; set; } = new();
        public List<string> ContextAfter { get; set; } = new();
        public string FileClassification { get; set; } = string.Empty;
    }

    #endregion
}
