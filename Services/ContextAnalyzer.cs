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
    /// Uses call graph and dependency analysis for smarter context selection.
    /// </summary>
    public class ContextAnalyzer
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly List<CallSite> _callSites;

        // Cached call graph for quick lookups
        private readonly Dictionary<string, HashSet<string>> _callers = new();
        private readonly Dictionary<string, HashSet<string>> _callees = new();

        public ContextAnalyzer(WorkspaceAnalysis workspaceAnalysis, List<CallSite>? callSites = null)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _callSites = callSites ?? new List<CallSite>();
            BuildCallGraph();
        }

        private void BuildCallGraph()
        {
            foreach (var site in _callSites)
            {
                var callerKey = $"{site.CallerType}.{site.CallerMethod}";
                var calleeKey = $"{site.CalledType}.{site.CalledMethod}";

                // Track who calls whom
                if (!_callers.ContainsKey(calleeKey))
                    _callers[calleeKey] = new HashSet<string>();
                _callers[calleeKey].Add(callerKey);

                // Track whom each method calls
                if (!_callees.ContainsKey(callerKey))
                    _callees[callerKey] = new HashSet<string>();
                _callees[callerKey].Add(calleeKey);
            }
        }

        /// <summary>
        /// Analyzes a task description and returns relevant files with context.
        /// Uses call graph analysis to include related files (callers/callees).
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

            // Apply token budget for initial selection (reserve some for call graph expansion)
            int initialTokenBudget = (int)(maxTokens * 0.7); // 70% for direct matches
            int currentTokens = 0;
            var selectedFiles = new List<ScoredFile>();
            int initialMaxFiles = Math.Max(3, maxFiles / 2); // At least 3, at most half

            foreach (var scoredFile in rankedFiles)
            {
                if (selectedFiles.Count >= initialMaxFiles)
                    break;

                if (currentTokens + scoredFile.File.EstimatedTokens > initialTokenBudget && selectedFiles.Count > 0)
                    continue; // Skip large files if we already have some

                selectedFiles.Add(scoredFile);
                currentTokens += scoredFile.File.EstimatedTokens;
            }

            // Expand with call graph related files (use remaining budget)
            int remainingTokens = maxTokens - currentTokens;
            int remainingFileSlots = maxFiles - selectedFiles.Count;
            
            if (remainingFileSlots > 0 && remainingTokens > 0)
            {
                selectedFiles = ExpandWithCallGraph(selectedFiles, remainingFileSlots, remainingTokens);
            }

            // Recalculate total tokens after expansion
            result.TotalTokens = selectedFiles.Sum(f => f.File.EstimatedTokens);
            result.RelevantFiles = selectedFiles;

            // Generate suggestions based on task type
            result.Suggestions = GenerateSuggestions(taskDescription, selectedFiles);

            // Find related types that might be useful
            result.RelatedTypes = FindRelatedTypes(selectedFiles);

            return result;
        }

        /// <summary>
        /// Searches file contents for a pattern and returns matches with context.
        /// </summary>
        public ContentSearchResult SearchContent(string pattern, bool isRegex = true, bool caseSensitive = false, int contextLines = 2, int maxResults = 50, bool summaryOnly = false)
        {
            var result = new ContentSearchResult
            {
                Pattern = pattern,
                IsRegex = isRegex,
                CaseSensitive = caseSensitive,
                IsSummaryOnly = summaryOnly
            };

            var matches = new List<ContentMatch>();
            var fileSummaries = new Dictionary<string, int>();
            int actualTotal = 0;
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
                try
                {
                    var lines = File.ReadAllLines(file.FilePath);
                    int fileMatchCount = 0;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var match = regex.Match(line);

                        if (match.Success)
                        {
                            fileMatchCount++;
                            actualTotal++;

                            // In summary mode, skip collecting individual matches
                            if (summaryOnly)
                                continue;

                            // In normal mode, collect matches up to maxResults
                            if (matches.Count < maxResults)
                            {
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

                    if (fileMatchCount > 0)
                        fileSummaries[file.RelativePath] = fileMatchCount;
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            result.Matches = matches;
            result.TotalMatches = matches.Count;
            result.ActualTotalMatches = actualTotal;
            result.FilesSearched = _workspaceAnalysis.AllFiles.Count;
            result.FilesWithMatches = fileSummaries.Count;
            result.FileSummaries = fileSummaries;

            return result;
        }

        private List<string> ExtractKeywords(string taskDescription)
        {
            var keywords = new List<string>();
            var text = taskDescription.ToLowerInvariant();

            // Step 1: Extract ALL meaningful words (3+ chars, not stop words)
            var stopWords = new HashSet<string>
            {
                "the", "and", "for", "that", "this", "with", "from", "have", "are", "was",
                "will", "can", "has", "but", "not", "you", "all", "any", "been", "each",
                "how", "its", "may", "new", "now", "old", "see", "way", "who", "did",
                "get", "got", "let", "say", "she", "too", "use", "her", "him", "his",
                "what", "when", "where", "which", "while", "about", "after", "before",
                "being", "below", "between", "both", "could", "does", "doing", "down",
                "during", "into", "just", "more", "most", "only", "other", "over",
                "same", "should", "some", "such", "than", "them", "then", "there",
                "these", "they", "those", "through", "under", "very", "were", "would",
                "also", "want", "need", "like", "make", "look", "find", "take", "give"
            };

            var wordPattern = new Regex(@"\b([a-zA-Z]{3,})\b");
            var allWords = wordPattern.Matches(taskDescription)
                .Cast<Match>()
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !stopWords.Contains(w))
                .Distinct()
                .ToList();

            // Step 2: Match words directly against workspace type and method names (highest value)
            var typeNames = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types)
                .Select(t => t.Name)
                .Distinct()
                .ToList();

            var methodNames = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types)
                .SelectMany(t => t.Members)
                .Select(m => m.Name)
                .Distinct()
                .ToList();

            foreach (var word in allWords)
            {
                // Check if this word matches part of any type name
                foreach (var typeName in typeNames)
                {
                    if (typeName.ToLowerInvariant().Contains(word) && word.Length >= 4)
                    {
                        keywords.Add(typeName); // Add the actual type name for precise matching
                    }
                }
            }

            // Step 3: Add all meaningful content words as keywords (for file/member matching)
            keywords.AddRange(allWords);

            // Step 4: Extract specific identifiers (PascalCase, camelCase words)
            var identifierPattern = new Regex(@"\b([A-Z][a-zA-Z0-9]*|[a-z][a-zA-Z0-9]*[A-Z][a-zA-Z0-9]*)\b");
            var identifiers = identifierPattern.Matches(taskDescription)
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => w.Length > 2)
                .Distinct();

            keywords.AddRange(identifiers);

            // Step 5: Extract quoted terms
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

            foreach (var keyword in keywords)
            {
                var kwLower = keyword.ToLowerInvariant();

                // Direct file name match
                if (fileNameLower.Contains(kwLower))
                    score += 10;

                // Path match (e.g., keyword "dashboard" matching "Live\Dashboard\")
                if (fileLower.Contains(kwLower))
                    score += 4;

                foreach (var type in file.Types)
                {
                    var typeLower = type.Name.ToLowerInvariant();

                    // Exact type name match (highest value - keyword IS a type name)
                    if (typeLower == kwLower)
                        score += 20;

                    // Partial type name match
                    else if (typeLower.Contains(kwLower) && kwLower.Length >= 4)
                        score += 10;

                    // Member name matches
                    foreach (var member in type.Members)
                    {
                        var memberLower = member.Name.ToLowerInvariant();
                        if (memberLower.Contains(kwLower) && kwLower.Length >= 4)
                            score += 5;
                    }
                }
            }

            // Call graph scoring: files with heavily-called methods are important
            foreach (var type in file.Types)
            {
                foreach (var member in type.Members)
                {
                    var methodKey = $"{type.Name}.{member.Name}";
                    if (_callers.TryGetValue(methodKey, out var callerSet))
                    {
                        score += Math.Min(callerSet.Count * 0.5, 5);
                    }
                }
            }

            // Classification bonus based on task keywords
            if (taskLower.Contains("tool") || taskLower.Contains("mcp"))
            {
                if (fileLower.Contains("mcp") || fileLower.Contains("server"))
                    score += 15;
            }

            if (taskLower.Contains("ui") || taskLower.Contains("view") || taskLower.Contains("window") || taskLower.Contains("xaml"))
            {
                if (file.Classification == FileClassification.View)
                    score += 10;
            }

            // Dependency-based scoring
            foreach (var type in file.Types)
            {
                if (_workspaceAnalysis.DependencyMap.Values.Any(deps => deps.Contains(type.Name)))
                {
                    score += 2;
                }
            }

            return score;
        }

        /// <summary>
        /// Expands file selection by including files containing callers/callees of methods in selected files.
        /// This ensures related code paths are included for complete context.
        /// </summary>
        private List<ScoredFile> ExpandWithCallGraph(List<ScoredFile> selectedFiles, int maxAdditionalFiles, int remainingTokens)
        {
            if (_callSites.Count == 0)
                return selectedFiles;

            var selectedPaths = selectedFiles.Select(f => f.File.RelativePath).ToHashSet();
            var additionalFiles = new List<ScoredFile>();

            // Build type-to-file lookup
            var typeToFile = new Dictionary<string, FileAnalysis>();
            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    typeToFile[type.Name] = file;
                }
            }

            // For each selected file, find callers and callees
            foreach (var scoredFile in selectedFiles)
            {
                foreach (var type in scoredFile.File.Types)
                {
                    foreach (var member in type.Members.Where(m => m.Kind == CodeMemberKind.Method))
                    {
                        var methodKey = $"{type.Name}.{member.Name}";

                        // Add files containing callers of this method
                        if (_callers.TryGetValue(methodKey, out var callerSet))
                        {
                            foreach (var callerKey in callerSet.Take(3)) // Limit to avoid explosion
                            {
                                var callerType = callerKey.Split('.').FirstOrDefault();
                                if (callerType != null && typeToFile.TryGetValue(callerType, out var callerFile))
                                {
                                    if (!selectedPaths.Contains(callerFile.RelativePath) &&
                                        !additionalFiles.Any(f => f.File.RelativePath == callerFile.RelativePath))
                                    {
                                        additionalFiles.Add(new ScoredFile
                                        {
                                            File = callerFile,
                                            Score = scoredFile.Score * 0.6, // Lower score than original
                                            MatchReasons = new List<string> { $"Calls {type.Name}.{member.Name}" }
                                        });
                                    }
                                }
                            }
                        }

                        // Add files containing callees of this method
                        if (_callees.TryGetValue(methodKey, out var calleeSet))
                        {
                            foreach (var calleeKey in calleeSet.Take(3)) // Limit to avoid explosion
                            {
                                var calleeType = calleeKey.Split('.').FirstOrDefault();
                                if (calleeType != null && typeToFile.TryGetValue(calleeType, out var calleeFile))
                                {
                                    if (!selectedPaths.Contains(calleeFile.RelativePath) &&
                                        !additionalFiles.Any(f => f.File.RelativePath == calleeFile.RelativePath))
                                    {
                                        additionalFiles.Add(new ScoredFile
                                        {
                                            File = calleeFile,
                                            Score = scoredFile.Score * 0.5, // Lower score than callers
                                            MatchReasons = new List<string> { $"Called by {type.Name}.{member.Name}" }
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Sort additional files by score and add within budget
            var sortedAdditional = additionalFiles
                .OrderByDescending(f => f.Score)
                .Take(maxAdditionalFiles);

            int addedTokens = 0;
            var result = new List<ScoredFile>(selectedFiles);

            foreach (var file in sortedAdditional)
            {
                if (addedTokens + file.File.EstimatedTokens > remainingTokens)
                    continue;

                result.Add(file);
                addedTokens += file.File.EstimatedTokens;
            }

            return result;
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

                // Add call graph information
                foreach (var member in type.Members.Where(m => m.Kind == CodeMemberKind.Method))
                {
                    var methodKey = $"{type.Name}.{member.Name}";
                    
                    if (_callers.TryGetValue(methodKey, out var callerSet) && callerSet.Count > 2)
                    {
                        reasons.Add($"{type.Name}.{member.Name} called by {callerSet.Count} methods");
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
            
            // Count files from call graph expansion
            int directMatches = RelevantFiles.Count(f => !f.MatchReasons.Any(r => r.StartsWith("Calls ") || r.StartsWith("Called by ")));
            int callGraphExpanded = RelevantFiles.Count - directMatches;
            if (callGraphExpanded > 0)
            {
                sb.AppendLine($"*{directMatches} direct matches + {callGraphExpanded} from call graph analysis*");
                sb.AppendLine();
            }

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
        public int ActualTotalMatches { get; set; }
        public int FilesSearched { get; set; }
        public int FilesWithMatches { get; set; }
        public Dictionary<string, int> FileSummaries { get; set; } = new();
        public bool IsSummaryOnly { get; set; }
        public string? Error { get; set; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(Error))
            {
                sb.AppendLine($"**Error:** {Error}");
                return sb.ToString();
            }

            if (IsSummaryOnly)
                return ToSummaryMarkdown();

            sb.AppendLine($"# Search Results: `{Pattern}`");
            sb.AppendLine();

            var shownFiles = Matches.Select(m => m.FilePath).Distinct().Count();
            if (ActualTotalMatches > TotalMatches)
                sb.AppendLine($"**Found:** {TotalMatches} matches shown ({ActualTotalMatches} total) in {shownFiles} of {FilesWithMatches} files");
            else
                sb.AppendLine($"**Found:** {TotalMatches} matches in {shownFiles} files");

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

        private string ToSummaryMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# Search Summary: `{Pattern}`");
            sb.AppendLine();
            sb.AppendLine($"**Total matches:** {ActualTotalMatches} in {FilesWithMatches} files");
            sb.AppendLine($"**Files searched:** {FilesSearched}");
            sb.AppendLine($"**Options:** Regex={IsRegex}, CaseSensitive={CaseSensitive}");
            sb.AppendLine();

            if (FileSummaries.Count > 0)
            {
                sb.AppendLine("| File | Matches |");
                sb.AppendLine("|------|---------|");

                foreach (var kvp in FileSummaries.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine($"| `{kvp.Key}` | {kvp.Value} |");
                }
            }
            else
            {
                sb.AppendLine("*No matches found.*");
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
