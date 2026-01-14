using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    public class PythonAnalyzer
    {
        // Regex patterns for Python constructs
        private static readonly Regex ClassPattern = new(@"^class\s+(\w+)\s*(?:\((.*?)\))?\s*:", RegexOptions.Multiline);
        private static readonly Regex FunctionPattern = new(@"^(\s*)def\s+(\w+)\s*\((.*?)\)\s*(?:->\s*(\S+))?\s*:", RegexOptions.Multiline);
        private static readonly Regex AsyncFunctionPattern = new(@"^(\s*)async\s+def\s+(\w+)\s*\((.*?)\)\s*(?:->\s*(\S+))?\s*:", RegexOptions.Multiline);
        private static readonly Regex ImportPattern = new(@"^(?:from\s+(\S+)\s+)?import\s+(.+)$", RegexOptions.Multiline);
        private static readonly Regex DecoratorPattern = new(@"^(\s*)@(\w+(?:\.\w+)*)(?:\(.*?\))?", RegexOptions.Multiline);
        private static readonly Regex DocstringPattern = new(@"^\s*(?:""""""(.*?)""""""|'''(.*?)''')", RegexOptions.Singleline);

        public List<CallSite> CallSites { get; private set; } = new();

        public FileAnalysis AnalyzeFile(string filePath, string basePath)
        {
            var fileInfo = new FileInfo(filePath);
            var relativePath = filePath.Substring(basePath.Length).TrimStart('\\', '/');

            var analysis = new FileAnalysis
            {
                FilePath = filePath,
                RelativePath = relativePath,
                FileName = fileInfo.Name,
                Extension = ".py",
                Language = "python",
                SizeInBytes = fileInfo.Length,
                EstimatedTokens = (int)(fileInfo.Length / 4)
            };

            try
            {
                string code = File.ReadAllText(filePath);
                string[] lines = code.Split('\n');

                // Extract imports
                analysis.Usings = ExtractImports(code);

                // Extract classes and their methods
                var types = ExtractClasses(code, lines, filePath);
                analysis.Types.AddRange(types);

                // Extract module-level functions (not inside classes)
                var moduleFunctions = ExtractModuleFunctions(code, lines, filePath);
                if (moduleFunctions.Count > 0)
                {
                    // Create a pseudo-type for module-level functions
                    var moduleType = new CodeTypeInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(filePath),
                        FullName = Path.GetFileNameWithoutExtension(filePath),
                        Kind = CodeTypeKind.Class, // Treat as class for compatibility
                        StartLine = 1,
                        EndLine = lines.Length
                    };
                    moduleType.Members.AddRange(moduleFunctions);
                    analysis.Types.Insert(0, moduleType);
                }

                // Build dependencies from imports
                analysis.Dependencies = analysis.Usings.Select(u => u.Split('.').First()).Distinct().ToList();
            }
            catch
            {
                // If parsing fails, leave types empty
            }

            analysis.Classification = ClassifyPythonFile(analysis);
            return analysis;
        }

        private List<string> ExtractImports(string code)
        {
            var imports = new List<string>();
            var matches = ImportPattern.Matches(code);

            foreach (Match match in matches)
            {
                string fromModule = match.Groups[1].Value;
                string importPart = match.Groups[2].Value;

                if (!string.IsNullOrEmpty(fromModule))
                {
                    imports.Add(fromModule);
                }
                else
                {
                    // Handle "import x, y, z"
                    var modules = importPart.Split(',').Select(s => s.Trim().Split(' ')[0]);
                    imports.AddRange(modules);
                }
            }

            return imports.Distinct().Where(i => !string.IsNullOrEmpty(i)).ToList();
        }

        private List<CodeTypeInfo> ExtractClasses(string code, string[] lines, string filePath)
        {
            var classes = new List<CodeTypeInfo>();
            var matches = ClassPattern.Matches(code);

            foreach (Match match in matches)
            {
                var classInfo = new CodeTypeInfo
                {
                    Name = match.Groups[1].Value,
                    Kind = CodeTypeKind.Class
                };

                // Parse base classes
                string baseClasses = match.Groups[2].Value;
                if (!string.IsNullOrEmpty(baseClasses))
                {
                    var bases = baseClasses.Split(',').Select(b => b.Trim()).ToList();
                    if (bases.Count > 0)
                    {
                        classInfo.BaseType = bases[0];
                        classInfo.Interfaces.AddRange(bases.Skip(1));
                    }
                }

                // Find line number
                int charIndex = match.Index;
                int lineNumber = code.Substring(0, charIndex).Count(c => c == '\n') + 1;
                classInfo.StartLine = lineNumber;
                classInfo.FullName = classInfo.Name;

                // Find class end (next class or end of file)
                int classEndLine = FindBlockEnd(lines, lineNumber - 1, 0);
                classInfo.EndLine = classEndLine;

                // Extract docstring
                classInfo.XmlDoc = ExtractDocstring(lines, lineNumber);

                // Extract methods within this class
                string classBody = string.Join("\n", lines.Skip(lineNumber - 1).Take(classEndLine - lineNumber + 1));
                var methods = ExtractMethods(classBody, lines, lineNumber - 1, filePath, classInfo.Name, true);
                classInfo.Members.AddRange(methods);

                classes.Add(classInfo);
            }

            return classes;
        }

        private List<CodeMemberInfo> ExtractModuleFunctions(string code, string[] lines, string filePath)
        {
            var functions = new List<CodeMemberInfo>();
            
            // Find all functions at indentation level 0
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                
                // Check for decorators at column 0
                var decoratorMatch = DecoratorPattern.Match(line);
                string decorator = "";
                if (decoratorMatch.Success && string.IsNullOrEmpty(decoratorMatch.Groups[1].Value))
                {
                    decorator = decoratorMatch.Groups[2].Value;
                }

                // Check for def at column 0
                bool isAsync = line.TrimStart().StartsWith("async def ");
                var funcMatch = isAsync ? AsyncFunctionPattern.Match(line) : FunctionPattern.Match(line);
                
                if (funcMatch.Success && string.IsNullOrEmpty(funcMatch.Groups[1].Value))
                {
                    var memberInfo = CreateMemberInfo(funcMatch, lines, i, isAsync, decorator, filePath, "module");
                    if (memberInfo != null)
                    {
                        functions.Add(memberInfo);
                    }
                }
            }

            return functions;
        }

        private List<CodeMemberInfo> ExtractMethods(string classBody, string[] allLines, int classStartLine, string filePath, string className, bool isClassMethod)
        {
            var methods = new List<CodeMemberInfo>();
            var classLines = classBody.Split('\n');

            for (int i = 0; i < classLines.Length; i++)
            {
                string line = classLines[i];
                
                // Check for decorators
                var decoratorMatch = DecoratorPattern.Match(line);
                string decorator = "";
                if (decoratorMatch.Success)
                {
                    decorator = decoratorMatch.Groups[2].Value;
                }

                // Check for methods (indented def)
                bool isAsync = line.Contains("async def ");
                var funcMatch = isAsync ? AsyncFunctionPattern.Match(line) : FunctionPattern.Match(line);

                if (funcMatch.Success)
                {
                    string indent = funcMatch.Groups[1].Value;
                    // Only process methods with proper indentation (inside class)
                    if (isClassMethod && indent.Length > 0)
                    {
                        int actualLine = classStartLine + i;
                        var memberInfo = CreateMemberInfo(funcMatch, allLines, actualLine, isAsync, decorator, filePath, className);
                        if (memberInfo != null)
                        {
                            methods.Add(memberInfo);
                        }
                    }
                }
            }

            return methods;
        }

        private CodeMemberInfo? CreateMemberInfo(Match funcMatch, string[] lines, int lineIndex, bool isAsync, string decorator, string filePath, string className)
        {
            string methodName = funcMatch.Groups[2].Value;
            string parameters = funcMatch.Groups[3].Value;
            string returnType = funcMatch.Groups[4].Value;

            var memberInfo = new CodeMemberInfo
            {
                Name = methodName,
                Kind = CodeMemberKind.Method,
                ReturnType = string.IsNullOrEmpty(returnType) ? "None" : returnType,
                IsAsync = isAsync,
                IsStatic = decorator == "staticmethod",
                StartLine = lineIndex + 1,
                AccessModifier = methodName.StartsWith("_") ? "private" : "public"
            };

            // Parse parameters
            if (!string.IsNullOrEmpty(parameters))
            {
                memberInfo.Parameters = parameters.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
                
                // Check if it's a class method (has self/cls as first param)
                if (memberInfo.Parameters.Count > 0)
                {
                    string firstParam = memberInfo.Parameters[0].Split(':')[0].Trim();
                    if (firstParam == "cls")
                    {
                        memberInfo.IsStatic = true; // classmethod
                    }
                }
            }

            memberInfo.Signature = $"{methodName}({parameters})";

            // Find method end
            int methodEndLine = FindBlockEnd(lines, lineIndex, GetIndentLevel(lines[lineIndex]));
            memberInfo.EndLine = methodEndLine;

            // Extract docstring
            if (lineIndex + 1 < lines.Length)
            {
                memberInfo.XmlDoc = ExtractDocstring(lines, lineIndex + 2); // +2 because 1-indexed and next line
            }

            // Extract method body for call graph
            if (lineIndex < lines.Length)
            {
                var bodyLines = lines.Skip(lineIndex).Take(methodEndLine - lineIndex).ToList();
                memberInfo.Body = string.Join("\n", bodyLines);
                ExtractMethodCalls(memberInfo.Body, filePath, className, methodName);
            }

            return memberInfo;
        }

        private void ExtractMethodCalls(string body, string filePath, string callerType, string callerMethod)
        {
            // Simple regex to find function/method calls
            var callPattern = new Regex(@"(\w+(?:\.\w+)*)\s*\(");
            var matches = callPattern.Matches(body);

            foreach (Match match in matches)
            {
                string call = match.Groups[1].Value;
                string[] parts = call.Split('.');

                var callSite = new CallSite
                {
                    CallerType = callerType,
                    CallerMethod = callerMethod,
                    FilePath = filePath,
                    CalledMethod = parts.Last(),
                    CalledType = parts.Length > 1 ? string.Join(".", parts.Take(parts.Length - 1)) : callerType
                };

                CallSites.Add(callSite);
            }
        }

        private string ExtractDocstring(string[] lines, int startLine)
        {
            if (startLine < 1 || startLine > lines.Length) return "";

            string line = lines[startLine - 1].Trim();
            
            // Check for triple-quoted docstring
            if (line.StartsWith("\"\"\"") || line.StartsWith("'''"))
            {
                string quote = line.Substring(0, 3);
                var docLines = new List<string>();
                
                // Single-line docstring
                if (line.EndsWith(quote) && line.Length > 6)
                {
                    return line.Substring(3, line.Length - 6).Trim();
                }

                // Multi-line docstring
                docLines.Add(line.Substring(3));
                for (int i = startLine; i < lines.Length; i++)
                {
                    string docLine = lines[i];
                    if (docLine.Contains(quote))
                    {
                        int endIndex = docLine.IndexOf(quote);
                        docLines.Add(docLine.Substring(0, endIndex));
                        break;
                    }
                    docLines.Add(docLine.Trim());
                }

                return string.Join(" ", docLines).Trim();
            }

            return "";
        }

        private int FindBlockEnd(string[] lines, int startLine, int startIndent)
        {
            for (int i = startLine + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                int currentIndent = GetIndentLevel(line);
                if (currentIndent <= startIndent && !string.IsNullOrWhiteSpace(line))
                {
                    return i; // Return the line before the dedent
                }
            }
            return lines.Length;
        }

        private int GetIndentLevel(string line)
        {
            int spaces = 0;
            foreach (char c in line)
            {
                if (c == ' ') spaces++;
                else if (c == '\t') spaces += 4;
                else break;
            }
            return spaces;
        }

        private FileClassification ClassifyPythonFile(FileAnalysis analysis)
        {
            var fileName = analysis.FileName.ToLowerInvariant();
            var relativePath = analysis.RelativePath.ToLowerInvariant();

            // Test files
            if (fileName.StartsWith("test_") || fileName.EndsWith("_test.py") || 
                relativePath.Contains("/tests/") || relativePath.Contains("\\tests\\"))
                return FileClassification.Test;

            // Django/Flask patterns
            if (fileName == "views.py" || relativePath.Contains("/views/"))
                return FileClassification.View;
            if (fileName == "models.py" || relativePath.Contains("/models/"))
                return FileClassification.Model;
            if (fileName == "serializers.py" || fileName == "forms.py")
                return FileClassification.Model;
            if (fileName == "urls.py" || fileName == "routes.py")
                return FileClassification.Controller;
            if (fileName == "admin.py")
                return FileClassification.Controller;

            // Service patterns
            if (fileName.EndsWith("_service.py") || fileName.EndsWith("_services.py") ||
                relativePath.Contains("/services/"))
                return FileClassification.Service;

            // Repository patterns
            if (fileName.EndsWith("_repository.py") || relativePath.Contains("/repositories/"))
                return FileClassification.Repository;

            // Config files
            if (fileName == "settings.py" || fileName == "config.py" || fileName == "conftest.py")
                return FileClassification.Config;

            // Check for base classes in content
            foreach (var type in analysis.Types)
            {
                if (type.BaseType?.Contains("View") == true || type.BaseType?.Contains("APIView") == true)
                    return FileClassification.View;
                if (type.BaseType?.Contains("Model") == true)
                    return FileClassification.Model;
                if (type.BaseType?.Contains("TestCase") == true)
                    return FileClassification.Test;
            }

            return FileClassification.Unknown;
        }
    }
}
