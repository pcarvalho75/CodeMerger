using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMerger.Models;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Handles refactoring MCP tools (rename, extract method, generate interface, etc.).
    /// </summary>
    public class McpRefactoringToolHandler
    {
        private readonly WorkspaceAnalysis _workspaceAnalysis;
        private readonly RefactoringService _refactoringService;
        private readonly List<CallSite> _callSites;
        private readonly Action<string> _sendActivity;
        private readonly Action<string> _log;

        public McpRefactoringToolHandler(
            WorkspaceAnalysis workspaceAnalysis,
            RefactoringService refactoringService,
            List<CallSite> callSites,
            Action<string> sendActivity,
            Action<string> log)
        {
            _workspaceAnalysis = workspaceAnalysis;
            _refactoringService = refactoringService;
            _callSites = callSites;
            _sendActivity = sendActivity;
            _log = log;
        }

        public string RenameSymbol(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("oldName", out var oldNameEl))
                return "Error: 'oldName' parameter is required.";

            if (!arguments.TryGetProperty("newName", out var newNameEl))
                return "Error: 'newName' parameter is required.";

            var oldName = oldNameEl.GetString() ?? "";
            var newName = newNameEl.GetString() ?? "";

            var preview = true;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            _sendActivity($"Rename: {oldName} → {newName}");

            var result = _refactoringService.RenameSymbol(oldName, newName, preview);
            _log($"RenameSymbol: {oldName} -> {newName} (preview={preview})");

            return result.ToMarkdown();
        }

        public string GenerateInterface(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("className", out var classNameEl))
                return "Error: 'className' parameter is required.";

            var className = classNameEl.GetString() ?? "";

            _sendActivity($"Generate interface: {className}");

            string? interfaceName = null;
            if (arguments.TryGetProperty("interfaceName", out var interfaceNameEl))
                interfaceName = interfaceNameEl.GetString();

            var result = _refactoringService.GenerateInterface(className, interfaceName);
            _log($"GenerateInterface: {className} -> {result.InterfaceName}");

            return result.ToMarkdown();
        }

        public string ExtractMethod(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("filePath", out var filePathEl))
                return "Error: 'filePath' parameter is required.";

            if (!arguments.TryGetProperty("startLine", out var startLineEl))
                return "Error: 'startLine' parameter is required.";

            if (!arguments.TryGetProperty("endLine", out var endLineEl))
                return "Error: 'endLine' parameter is required.";

            if (!arguments.TryGetProperty("methodName", out var methodNameEl))
                return "Error: 'methodName' parameter is required.";

            var filePath = filePathEl.GetString() ?? "";
            var startLine = startLineEl.GetInt32();
            var endLine = endLineEl.GetInt32();
            var methodName = methodNameEl.GetString() ?? "";

            _sendActivity($"Extract method: {methodName}");

            var result = _refactoringService.ExtractMethod(filePath, startLine, endLine, methodName);
            _log($"ExtractMethod: {filePath} lines {startLine}-{endLine} -> {methodName}()");

            return result.ToMarkdown();
        }

        public string AddParameter(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("typeName", out var typeNameEl))
                return "Error: 'typeName' parameter is required.";
            if (!arguments.TryGetProperty("methodName", out var methodNameEl))
                return "Error: 'methodName' parameter is required.";
            if (!arguments.TryGetProperty("parameterType", out var paramTypeEl))
                return "Error: 'parameterType' parameter is required.";
            if (!arguments.TryGetProperty("parameterName", out var paramNameEl))
                return "Error: 'parameterName' parameter is required.";
            if (!arguments.TryGetProperty("defaultValue", out var defaultValEl))
                return "Error: 'defaultValue' parameter is required.";

            var typeName = typeNameEl.GetString() ?? "";
            var methodName = methodNameEl.GetString() ?? "";
            var paramType = paramTypeEl.GetString() ?? "";
            var paramName = paramNameEl.GetString() ?? "";
            var defaultValue = defaultValEl.GetString() ?? "";

            var preview = true;
            if (arguments.TryGetProperty("preview", out var previewEl))
                preview = previewEl.GetBoolean();

            _sendActivity($"Add parameter: {methodName}");

            // Find the method definition
            var methodInfo = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Where(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(t => t.Members.Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                        .Select(m => new { File = f, Type = t, Method = m })))
                .FirstOrDefault();

            if (methodInfo == null)
            {
                return $"Error: Method '{methodName}' not found in type '{typeName}'";
            }

            var affectedFiles = new List<(string Path, string Change, int Count)>();

            // Find all call sites for this method
            var callSitesForMethod = _callSites
                .Where(cs => cs.CalledMethod.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"# Add Parameter: `{typeName}.{methodName}`");
            sb.AppendLine();
            sb.AppendLine($"**New parameter:** `{paramType} {paramName}`");
            sb.AppendLine($"**Default value at call sites:** `{defaultValue}`");
            sb.AppendLine();

            // Update method definition
            var defFile = methodInfo.File;
            try
            {
                var content = File.ReadAllText(defFile.FilePath);
                var tree = CSharpSyntaxTree.ParseText(content);
                var root = tree.GetCompilationUnitRoot();

                // Find method declaration
                var methodDecl = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == methodName &&
                        m.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.Identifier.Text == typeName));

                if (methodDecl != null)
                {
                    // Build old and new signature
                    var oldParams = methodDecl.ParameterList.ToString();
                    var paramList = methodDecl.ParameterList.Parameters.Select(p => p.ToString()).ToList();
                    paramList.Add($"{paramType} {paramName}");
                    var newParams = $"({string.Join(", ", paramList)})";

                    affectedFiles.Add((defFile.RelativePath, $"Definition: {oldParams} → {newParams}", 1));

                    if (!preview)
                    {
                        File.Copy(defFile.FilePath, defFile.FilePath + ".bak", true);
                        var newContent = content.Replace(oldParams, newParams);
                        File.WriteAllText(defFile.FilePath, newContent);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"**Warning:** Could not update definition: {ex.Message}");
            }

            // Update call sites
            var fileCallSites = callSitesForMethod
                .GroupBy(cs => cs.FilePath)
                .ToList();

            foreach (var fileGroup in fileCallSites)
            {
                var file = _workspaceAnalysis.AllFiles.FirstOrDefault(f => f.FilePath == fileGroup.Key);
                var relativePath = file?.RelativePath ?? Path.GetFileName(fileGroup.Key);

                try
                {
                    var content = File.ReadAllText(fileGroup.Key);

                    // Simple pattern: find methodName(...) and add parameter
                    var pattern = $@"\b{Regex.Escape(methodName)}\s*\(([^)]*)\)";
                    var matches = Regex.Matches(content, pattern);

                    if (matches.Count > 0)
                    {
                        var newContent = content;
                        foreach (Match match in matches.Cast<Match>().Reverse())
                        {
                            var existingArgs = match.Groups[1].Value.Trim();
                            var newArgs = string.IsNullOrEmpty(existingArgs)
                                ? defaultValue
                                : $"{existingArgs}, {defaultValue}";
                            var replacement = $"{methodName}({newArgs})";
                            newContent = newContent.Substring(0, match.Index) + replacement + newContent.Substring(match.Index + match.Length);
                        }

                        if (newContent != content)
                        {
                            affectedFiles.Add((relativePath, $"Call sites updated", matches.Count));

                            if (!preview)
                            {
                                File.Copy(fileGroup.Key, fileGroup.Key + ".bak", true);
                                File.WriteAllText(fileGroup.Key, newContent);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip files that can't be processed
                }
            }

            sb.AppendLine(preview ? "## Preview - Affected Files" : "## Applied Changes");
            sb.AppendLine();
            sb.AppendLine("| File | Change | Count |");
            sb.AppendLine("|------|--------|-------|");

            foreach (var (path, change, count) in affectedFiles)
            {
                sb.AppendLine($"| `{path}` | {change} | {count} |");
            }

            sb.AppendLine();
            sb.AppendLine($"**Total:** {affectedFiles.Sum(f => f.Count)} locations in {affectedFiles.Count} files");

            if (preview)
            {
                sb.AppendLine();
                sb.AppendLine("*Run with `preview: false` to apply changes.*");
            }

            _log($"AddParameter: {typeName}.{methodName} + {paramType} {paramName} (preview={preview})");

            return sb.ToString();
        }

        public string ImplementInterface(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("className", out var classNameEl))
                return "Error: 'className' parameter is required.";
            if (!arguments.TryGetProperty("interfaceName", out var interfaceNameEl))
                return "Error: 'interfaceName' parameter is required.";

            var className = classNameEl.GetString() ?? "";
            var interfaceName = interfaceNameEl.GetString() ?? "";

            _sendActivity($"Implement interface: {interfaceName} in {className}");

            // Find the interface
            var interfaceInfo = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Where(t => t.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) &&
                                                    t.Kind == CodeTypeKind.Interface)
                    .Select(t => new { File = f, Type = t }))
                .FirstOrDefault();

            if (interfaceInfo == null)
            {
                return $"Error: Interface '{interfaceName}' not found";
            }

            // Find the class
            var classInfo = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Where(t => t.Name.Equals(className, StringComparison.OrdinalIgnoreCase) &&
                                                    t.Kind == CodeTypeKind.Class)
                    .Select(t => new { File = f, Type = t }))
                .FirstOrDefault();

            if (classInfo == null)
            {
                return $"Error: Class '{className}' not found";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Implement Interface: `{interfaceName}` in `{className}`");
            sb.AppendLine();
            sb.AppendLine($"**Interface file:** `{interfaceInfo.File.RelativePath}`");
            sb.AppendLine($"**Class file:** `{classInfo.File.RelativePath}`");
            sb.AppendLine();

            // Generate stub implementations
            var stubs = new StringBuilder();
            stubs.AppendLine();
            stubs.AppendLine($"        #region {interfaceName} Implementation");
            stubs.AppendLine();

            foreach (var member in interfaceInfo.Type.Members)
            {
                if (member.Kind == CodeMemberKind.Method)
                {
                    var returnType = member.ReturnType ?? "void";
                    var signature = member.Signature ?? member.Name + "()";

                    stubs.AppendLine($"        public {returnType} {signature}");
                    stubs.AppendLine("        {");

                    if (returnType != "void" && returnType != "Task")
                    {
                        stubs.AppendLine($"            throw new NotImplementedException();");
                    }
                    else
                    {
                        stubs.AppendLine("            throw new NotImplementedException();");
                    }

                    stubs.AppendLine("        }");
                    stubs.AppendLine();
                }
                else if (member.Kind == CodeMemberKind.Property)
                {
                    stubs.AppendLine($"        public {member.ReturnType} {member.Name} {{ get; set; }}");
                    stubs.AppendLine();
                }
            }

            stubs.AppendLine("        #endregion");

            sb.AppendLine("## Generated Implementation Stubs");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(stubs.ToString());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("*Copy this code into your class, or use `codemerger_str_replace` to insert it before the last `}` of your class.*");

            _log($"ImplementInterface: {interfaceName} in {className}");

            return sb.ToString();
        }

        public string GenerateConstructor(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("className", out var classNameEl))
                return "Error: 'className' parameter is required.";

            var className = classNameEl.GetString() ?? "";

            _sendActivity($"Generate constructor: {className}");

            // Get specific fields if provided
            List<string>? specificFields = null;
            if (arguments.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
            {
                specificFields = fieldsEl.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList()!;
            }

            // Find the class
            var classInfo = _workspaceAnalysis.AllFiles
                .SelectMany(f => f.Types.Where(t => t.Name.Equals(className, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new { File = f, Type = t }))
                .FirstOrDefault();

            if (classInfo == null)
            {
                return $"Error: Class '{className}' not found";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Generate Constructor: `{className}`");
            sb.AppendLine();
            sb.AppendLine($"**File:** `{classInfo.File.RelativePath}`");
            sb.AppendLine();

            // Get fields and properties
            var members = classInfo.Type.Members
                .Where(m => m.Kind == CodeMemberKind.Field || m.Kind == CodeMemberKind.Property)
                .Where(m => !m.IsStatic)
                .Where(m => specificFields == null || specificFields.Count == 0 ||
                           specificFields.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (!members.Any())
            {
                return $"Error: No fields or properties found in class '{className}'";
            }

            // Generate constructor
            var ctor = new StringBuilder();
            ctor.AppendLine();
            ctor.AppendLine($"        public {className}(");

            var parameters = members.Select(m =>
            {
                var paramName = char.ToLower(m.Name[0]) + m.Name.Substring(1);
                // Remove underscore prefix if present
                if (paramName.StartsWith("_"))
                    paramName = paramName.Substring(1);
                return $"            {m.ReturnType} {paramName}";
            });

            ctor.AppendLine(string.Join(",\n", parameters));
            ctor.AppendLine("        )");
            ctor.AppendLine("        {");

            foreach (var member in members)
            {
                var paramName = char.ToLower(member.Name[0]) + member.Name.Substring(1);
                if (paramName.StartsWith("_"))
                    paramName = paramName.Substring(1);

                // Handle both fields (with underscore) and properties
                var fieldName = member.Name;
                ctor.AppendLine($"            {fieldName} = {paramName};");
            }

            ctor.AppendLine("        }");

            sb.AppendLine("## Generated Constructor");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(ctor.ToString());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Fields/Properties Included");
            foreach (var m in members)
            {
                sb.AppendLine($"- `{m.ReturnType} {m.Name}`");
            }

            sb.AppendLine();
            sb.AppendLine("*Use `codemerger_str_replace` to insert this constructor into your class.*");

            _log($"GenerateConstructor: {className}");

            return sb.ToString();
        }
    }
}
