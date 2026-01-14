using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Provides semantic analysis capabilities using indexed project data.
    /// Handles find usages, call graph analysis, and implementation finding.
    /// </summary>
    public class SemanticAnalyzer
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly List<CallSite> _callSites;

        public SemanticAnalyzer(WorkspaceAnalysis workspaceAnalysis, List<CallSite> callSites)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _callSites = callSites;
        }

        /// <summary>
        /// Find all usages of a symbol (type, method, property, field) across the codebase.
        /// </summary>
        public SymbolUsageResult FindUsages(string symbolName, string? symbolKind = null)
        {
            var result = new SymbolUsageResult
            {
                SymbolName = symbolName,
                RequestedKind = symbolKind
            };

            // Find definition(s)
            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    // Type definition
                    if (type.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Usages.Add(new SymbolUsage
                        {
                            SymbolName = type.Name,
                            SymbolKind = "Type",
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            UsageKind = UsageKind.Definition,
                            Context = $"{type.Kind.ToString().ToLower()} {type.Name}"
                        });
                    }

                    // Member definitions and references
                    foreach (var member in type.Members)
                    {
                        if (member.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Usages.Add(new SymbolUsage
                            {
                                SymbolName = member.Name,
                                SymbolKind = member.Kind.ToString(),
                                FilePath = file.RelativePath,
                                Line = member.StartLine,
                                UsageKind = member.IsOverride ? UsageKind.Override : UsageKind.Definition,
                                Context = $"{member.AccessModifier} {member.Signature ?? member.Name}"
                            });
                        }
                    }
                }
            }

            // Find references via call sites
            var relevantCallSites = _callSites
                .Where(cs => cs.CalledMethod.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var callSite in relevantCallSites)
            {
                var file = _workspaceAnalysis.AllFiles.FirstOrDefault(f => f.FilePath == callSite.FilePath);
                var relativePath = file?.RelativePath ?? callSite.FilePath;

                result.Usages.Add(new SymbolUsage
                {
                    SymbolName = symbolName,
                    SymbolKind = "Method",
                    FilePath = relativePath,
                    Line = callSite.Line,
                    UsageKind = UsageKind.Invocation,
                    Context = $"{callSite.CallerType}.{callSite.CallerMethod} → {callSite.CalledMethod}"
                });
            }

            // Search for type references in inheritance
            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    if (type.BaseType?.Equals(symbolName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        result.Usages.Add(new SymbolUsage
                        {
                            SymbolName = symbolName,
                            SymbolKind = "Type",
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            UsageKind = UsageKind.Reference,
                            Context = $"{type.Name} : {type.BaseType}"
                        });
                    }

                    if (type.Interfaces.Any(i => i.Equals(symbolName, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Usages.Add(new SymbolUsage
                        {
                            SymbolName = symbolName,
                            SymbolKind = "Interface",
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            UsageKind = UsageKind.Implementation,
                            Context = $"{type.Name} : {symbolName}"
                        });
                    }
                }
            }

            result.TotalUsages = result.Usages.Count;
            return result;
        }

        /// <summary>
        /// Get the call graph for a method - who calls it and what it calls.
        /// </summary>
        public CallGraphResult GetCallGraph(string typeName, string methodName, int depth = 2)
        {
            var result = new CallGraphResult
            {
                RootType = typeName,
                RootMethod = methodName,
                Depth = depth
            };

            // Find callers (who calls this method)
            var callers = _callSites
                .Where(cs => cs.CalledMethod.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrEmpty(typeName) || cs.CalledType.Contains(typeName)))
                .Select(cs => new CallNode
                {
                    TypeName = cs.CallerType,
                    MethodName = cs.CallerMethod,
                    FilePath = GetRelativePath(cs.FilePath),
                    Line = cs.Line
                })
                .Distinct(new CallNodeComparer())
                .ToList();

            result.Callers = callers;

            // Find callees (what this method calls)
            var callees = _callSites
                .Where(cs => cs.CallerMethod.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrEmpty(typeName) || cs.CallerType.Contains(typeName)))
                .Select(cs => new CallNode
                {
                    TypeName = cs.CalledType,
                    MethodName = cs.CalledMethod,
                    FilePath = GetRelativePath(cs.FilePath),
                    Line = cs.Line
                })
                .Distinct(new CallNodeComparer())
                .ToList();

            result.Callees = callees;

            // Build deeper call chain if requested
            if (depth > 1)
            {
                foreach (var caller in callers.Take(10))
                {
                    var callerCallers = _callSites
                        .Where(cs => cs.CalledMethod.Equals(caller.MethodName, StringComparison.OrdinalIgnoreCase))
                        .Select(cs => $"{cs.CallerType}.{cs.CallerMethod}")
                        .Distinct()
                        .Take(5)
                        .ToList();

                    caller.UpstreamCallers = callerCallers;
                }

                foreach (var callee in callees.Take(10))
                {
                    var calleeCallees = _callSites
                        .Where(cs => cs.CallerMethod.Equals(callee.MethodName, StringComparison.OrdinalIgnoreCase))
                        .Select(cs => $"{cs.CalledType}.{cs.CalledMethod}")
                        .Distinct()
                        .Take(5)
                        .ToList();

                    callee.DownstreamCallees = calleeCallees;
                }
            }

            return result;
        }

        /// <summary>
        /// Find all implementations of an interface or overrides of a virtual method.
        /// </summary>
        public ImplementationResult FindImplementations(string interfaceOrTypeName, string? methodName = null)
        {
            var result = new ImplementationResult
            {
                InterfaceName = interfaceOrTypeName,
                MethodName = methodName
            };

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    // Check if type implements the interface or extends the base type
                    bool implementsInterface = type.Interfaces.Any(i => 
                        i.Equals(interfaceOrTypeName, StringComparison.OrdinalIgnoreCase));
                    bool extendsType = type.BaseType?.Equals(interfaceOrTypeName, StringComparison.OrdinalIgnoreCase) == true;

                    if (implementsInterface || extendsType)
                    {
                        var impl = new ImplementationInfo
                        {
                            TypeName = type.Name,
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            IsInterface = implementsInterface,
                            IsInheritance = extendsType
                        };

                        // If looking for specific method, find it
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            var method = type.Members.FirstOrDefault(m => 
                                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
                                (m.IsOverride || type.Kind == CodeTypeKind.Interface || implementsInterface));

                            if (method != null)
                            {
                                impl.ImplementedMethod = method.Signature ?? method.Name;
                                impl.MethodLine = method.StartLine;
                                result.Implementations.Add(impl);
                            }
                        }
                        else
                        {
                            result.Implementations.Add(impl);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get a specific method's body by type and method name.
        /// </summary>
        public MethodBodyResult GetMethodBody(string typeName, string methodName)
        {
            var result = new MethodBodyResult
            {
                TypeName = typeName,
                MethodName = methodName
            };

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    if (!type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var method = type.Members.FirstOrDefault(m =>
                        m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
                        m.Kind == CodeMemberKind.Method || m.Kind == CodeMemberKind.Constructor);

                    if (method != null)
                    {
                        result.Found = true;
                        result.FilePath = file.RelativePath;
                        result.StartLine = method.StartLine;
                        result.EndLine = method.EndLine;
                        result.Signature = method.Signature ?? method.Name;
                        result.ReturnType = method.ReturnType;
                        result.Parameters = method.Parameters;
                        result.Body = method.Body;
                        result.IsAsync = method.IsAsync;
                        result.IsStatic = method.IsStatic;
                        result.AccessModifier = method.AccessModifier;
                        result.XmlDoc = method.XmlDoc;

                        // If body is empty, try to read from file
                        if (string.IsNullOrEmpty(result.Body) && method.StartLine > 0 && method.EndLine > 0)
                        {
                            try
                            {
                                var lines = File.ReadAllLines(file.FilePath);
                                var methodLines = lines.Skip(method.StartLine - 1).Take(method.EndLine - method.StartLine + 1);
                                result.FullText = string.Join(Environment.NewLine, methodLines);
                            }
                            catch { }
                        }

                        return result;
                    }
                }
            }

            result.Found = false;
            result.Error = $"Method '{methodName}' not found in type '{typeName}'";
            return result;
        }

        /// <summary>
        /// Find methods matching certain criteria (async, static, return type, etc.)
        /// </summary>
        public SemanticQueryResult SemanticQuery(SemanticQueryOptions options)
        {
            var result = new SemanticQueryResult { Query = options };
            var matches = new List<SemanticMatch>();

            foreach (var file in _workspaceAnalysis.AllFiles)
            {
                foreach (var type in file.Types)
                {
                    // Type-level queries
                    if (options.FindInterfaces && type.Kind == CodeTypeKind.Interface)
                    {
                        matches.Add(new SemanticMatch
                        {
                            Name = type.Name,
                            Kind = "Interface",
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            Description = $"interface {type.Name}"
                        });
                    }

                    if (options.ImplementsInterface != null && 
                        type.Interfaces.Any(i => i.Contains(options.ImplementsInterface)))
                    {
                        matches.Add(new SemanticMatch
                        {
                            Name = type.Name,
                            Kind = "Class",
                            FilePath = file.RelativePath,
                            Line = type.StartLine,
                            Description = $"{type.Name} : {string.Join(", ", type.Interfaces)}"
                        });
                    }

                    // Member-level queries
                    foreach (var member in type.Members)
                    {
                        bool match = true;

                        if (options.IsAsync.HasValue && member.IsAsync != options.IsAsync.Value)
                            match = false;
                        if (options.IsStatic.HasValue && member.IsStatic != options.IsStatic.Value)
                            match = false;
                        if (options.IsVirtual.HasValue && member.IsVirtual != options.IsVirtual.Value)
                            match = false;
                        if (options.ReturnType != null && !member.ReturnType.Contains(options.ReturnType))
                            match = false;
                        if (options.MemberKind.HasValue && member.Kind != options.MemberKind.Value)
                            match = false;
                        if (options.AccessModifier != null && member.AccessModifier != options.AccessModifier)
                            match = false;
                        if (options.NamePattern != null && !member.Name.Contains(options.NamePattern, StringComparison.OrdinalIgnoreCase))
                            match = false;

                        if (match && (options.IsAsync.HasValue || options.IsStatic.HasValue || 
                                      options.IsVirtual.HasValue || options.ReturnType != null ||
                                      options.MemberKind.HasValue || options.AccessModifier != null ||
                                      options.NamePattern != null))
                        {
                            matches.Add(new SemanticMatch
                            {
                                Name = $"{type.Name}.{member.Name}",
                                Kind = member.Kind.ToString(),
                                FilePath = file.RelativePath,
                                Line = member.StartLine,
                                Description = $"{member.AccessModifier} {(member.IsAsync ? "async " : "")}{(member.IsStatic ? "static " : "")}{member.ReturnType} {member.Signature ?? member.Name}"
                            });
                        }
                    }

                    // Find missing XML docs
                    if (options.MissingXmlDocs)
                    {
                        foreach (var member in type.Members.Where(m => m.AccessModifier == "public" && string.IsNullOrEmpty(m.XmlDoc)))
                        {
                            matches.Add(new SemanticMatch
                            {
                                Name = $"{type.Name}.{member.Name}",
                                Kind = member.Kind.ToString(),
                                FilePath = file.RelativePath,
                                Line = member.StartLine,
                                Description = "Missing XML documentation"
                            });
                        }
                    }
                }
            }

            result.Matches = matches.Take(100).ToList();
            result.TotalMatches = matches.Count;
            return result;
        }

        private string GetRelativePath(string filePath)
        {
            var file = _workspaceAnalysis.AllFiles.FirstOrDefault(f => f.FilePath == filePath);
            return file?.RelativePath ?? Path.GetFileName(filePath);
        }
    }

    #region Result Models

    public class SymbolUsageResult
    {
        public string SymbolName { get; set; } = string.Empty;
        public string? RequestedKind { get; set; }
        public List<SymbolUsage> Usages { get; set; } = new();
        public int TotalUsages { get; set; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Usages of `{SymbolName}`");
            sb.AppendLine();
            sb.AppendLine($"**Total:** {TotalUsages} usages");
            sb.AppendLine();

            var grouped = Usages.GroupBy(u => u.UsageKind).OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var usage in group.OrderBy(u => u.FilePath).ThenBy(u => u.Line))
                {
                    sb.AppendLine($"- `{usage.FilePath}:{usage.Line}` - {usage.Context}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    public class CallGraphResult
    {
        public string RootType { get; set; } = string.Empty;
        public string RootMethod { get; set; } = string.Empty;
        public int Depth { get; set; }
        public List<CallNode> Callers { get; set; } = new();
        public List<CallNode> Callees { get; set; } = new();

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Call Graph: `{RootType}.{RootMethod}`");
            sb.AppendLine();

            sb.AppendLine("## Callers (who calls this)");
            if (Callers.Any())
            {
                foreach (var caller in Callers)
                {
                    sb.AppendLine($"- `{caller.TypeName}.{caller.MethodName}` ({caller.FilePath}:{caller.Line})");
                    if (caller.UpstreamCallers?.Any() == true)
                    {
                        foreach (var upstream in caller.UpstreamCallers)
                            sb.AppendLine($"  ← {upstream}");
                    }
                }
            }
            else
            {
                sb.AppendLine("*No callers found (entry point or unused)*");
            }
            sb.AppendLine();

            sb.AppendLine("## Callees (what this calls)");
            if (Callees.Any())
            {
                foreach (var callee in Callees)
                {
                    sb.AppendLine($"- `{callee.TypeName}.{callee.MethodName}` ({callee.FilePath}:{callee.Line})");
                    if (callee.DownstreamCallees?.Any() == true)
                    {
                        foreach (var downstream in callee.DownstreamCallees)
                            sb.AppendLine($"  → {downstream}");
                    }
                }
            }
            else
            {
                sb.AppendLine("*No outgoing calls*");
            }

            return sb.ToString();
        }
    }

    public class CallNode
    {
        public string TypeName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public List<string>? UpstreamCallers { get; set; }
        public List<string>? DownstreamCallees { get; set; }
    }

    public class CallNodeComparer : IEqualityComparer<CallNode>
    {
        public bool Equals(CallNode? x, CallNode? y)
        {
            if (x == null || y == null) return false;
            return x.TypeName == y.TypeName && x.MethodName == y.MethodName;
        }

        public int GetHashCode(CallNode obj) => HashCode.Combine(obj.TypeName, obj.MethodName);
    }

    public class ImplementationResult
    {
        public string InterfaceName { get; set; } = string.Empty;
        public string? MethodName { get; set; }
        public List<ImplementationInfo> Implementations { get; set; } = new();

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            var title = string.IsNullOrEmpty(MethodName)
                ? $"# Implementations of `{InterfaceName}`"
                : $"# Implementations of `{InterfaceName}.{MethodName}`";
            sb.AppendLine(title);
            sb.AppendLine();

            if (Implementations.Any())
            {
                sb.AppendLine($"**Found:** {Implementations.Count} implementations");
                sb.AppendLine();

                foreach (var impl in Implementations.OrderBy(i => i.FilePath))
                {
                    var marker = impl.IsInterface ? "implements" : "extends";
                    sb.AppendLine($"- **{impl.TypeName}** ({marker}) - `{impl.FilePath}:{impl.Line}`");
                    if (!string.IsNullOrEmpty(impl.ImplementedMethod))
                    {
                        sb.AppendLine($"  - `{impl.ImplementedMethod}` at line {impl.MethodLine}");
                    }
                }
            }
            else
            {
                sb.AppendLine("*No implementations found*");
            }

            return sb.ToString();
        }
    }

    public class ImplementationInfo
    {
        public string TypeName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public bool IsInterface { get; set; }
        public bool IsInheritance { get; set; }
        public string? ImplementedMethod { get; set; }
        public int MethodLine { get; set; }
    }

    public class MethodBodyResult
    {
        public string TypeName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public bool Found { get; set; }
        public string? Error { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Signature { get; set; } = string.Empty;
        public string ReturnType { get; set; } = string.Empty;
        public List<string> Parameters { get; set; } = new();
        public string Body { get; set; } = string.Empty;
        public string? FullText { get; set; }
        public bool IsAsync { get; set; }
        public bool IsStatic { get; set; }
        public string AccessModifier { get; set; } = string.Empty;
        public string XmlDoc { get; set; } = string.Empty;

        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            if (!Found)
            {
                sb.AppendLine($"**Error:** {Error}");
                return sb.ToString();
            }

            sb.AppendLine($"# {TypeName}.{MethodName}");
            sb.AppendLine();
            sb.AppendLine($"**File:** `{FilePath}` (lines {StartLine}-{EndLine})");
            sb.AppendLine($"**Signature:** `{AccessModifier} {(IsAsync ? "async " : "")}{(IsStatic ? "static " : "")}{ReturnType} {Signature}`");
            
            if (Parameters.Any())
            {
                sb.AppendLine($"**Parameters:** {string.Join(", ", Parameters)}");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(XmlDoc))
            {
                sb.AppendLine("## Documentation");
                sb.AppendLine("```xml");
                sb.AppendLine(XmlDoc);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine("## Body");
            sb.AppendLine("```csharp");
            sb.AppendLine(string.IsNullOrEmpty(Body) ? FullText : Body);
            sb.AppendLine("```");

            return sb.ToString();
        }
    }

    public class SemanticQueryOptions
    {
        public bool? IsAsync { get; set; }
        public bool? IsStatic { get; set; }
        public bool? IsVirtual { get; set; }
        public string? ReturnType { get; set; }
        public CodeMemberKind? MemberKind { get; set; }
        public string? AccessModifier { get; set; }
        public string? NamePattern { get; set; }
        public string? ImplementsInterface { get; set; }
        public bool FindInterfaces { get; set; }
        public bool MissingXmlDocs { get; set; }
    }

    public class SemanticQueryResult
    {
        public SemanticQueryOptions Query { get; set; } = new();
        public List<SemanticMatch> Matches { get; set; } = new();
        public int TotalMatches { get; set; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Semantic Query Results");
            sb.AppendLine();
            sb.AppendLine($"**Found:** {TotalMatches} matches");
            sb.AppendLine();

            var grouped = Matches.GroupBy(m => m.Kind);
            foreach (var group in grouped)
            {
                sb.AppendLine($"## {group.Key}s");
                foreach (var match in group.OrderBy(m => m.FilePath))
                {
                    sb.AppendLine($"- `{match.Name}` - {match.Description}");
                    sb.AppendLine($"  - `{match.FilePath}:{match.Line}`");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    public class SemanticMatch
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    #endregion
}
