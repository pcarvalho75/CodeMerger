using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeMerger.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services
{
    public class CodeAnalyzer
    {
        private static readonly Dictionary<string, string> LanguageMap = new()
        {
            { ".cs", "csharp" },
            { ".xaml", "xml" },
            { ".xml", "xml" },
            { ".json", "json" },
            { ".js", "javascript" },
            { ".ts", "typescript" },
            { ".html", "html" },
            { ".css", "css" },
            { ".sql", "sql" },
            { ".py", "python" },
            { ".md", "markdown" },
            { ".yaml", "yaml" },
            { ".yml", "yaml" },
            { ".csproj", "xml" }
        };

        // Store call sites during analysis
        public List<CallSite> CallSites { get; private set; } = new();

        // Python analyzer for .py files
        private readonly PythonAnalyzer _pythonAnalyzer = new();

        public FileAnalysis AnalyzeFile(string filePath, string basePath)
        {
            var fileInfo = new FileInfo(filePath);
            var relativePath = filePath.Substring(basePath.Length).TrimStart('\\', '/');
            var extension = fileInfo.Extension.ToLowerInvariant();

            // Route Python files to PythonAnalyzer
            if (extension == ".py")
            {
                var pythonAnalysis = _pythonAnalyzer.AnalyzeFile(filePath, basePath);
                // Merge call sites from Python analyzer
                CallSites.AddRange(_pythonAnalyzer.CallSites);
                _pythonAnalyzer.CallSites.Clear();
                return pythonAnalysis;
            }

            var analysis = new FileAnalysis
            {
                FilePath = filePath,
                RelativePath = relativePath,
                RootDirectory = basePath,
                FileName = fileInfo.Name,
                Extension = extension,
                Language = GetLanguage(extension),
                SizeInBytes = fileInfo.Length,
                EstimatedTokens = EstimateTokens(fileInfo.Length)
            };

            if (extension == ".cs")
            {
                AnalyzeCSharpFile(analysis);
            }
            else if (extension == ".csproj")
            {
                AnalyzeCsprojFile(analysis);
            }

            analysis.Classification = ClassifyFile(analysis);

            return analysis;
        }

        private void AnalyzeCSharpFile(FileAnalysis analysis)
        {
            try
            {
                string code = File.ReadAllText(analysis.FilePath);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetCompilationUnitRoot();

                // Extract namespace
                var namespaceDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                var namespaceName = namespaceDecl?.Name.ToString() ?? "";

                // Store the namespace at file level
                analysis.Namespace = namespaceName;

                // Extract usings
                analysis.Usings = root.Usings
                    .Select(u => u.Name?.ToString() ?? string.Empty)
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();

                // Extract types
                var typeDeclarations = root.DescendantNodes()
                    .Where(n => n is TypeDeclarationSyntax || n is EnumDeclarationSyntax || n is DelegateDeclarationSyntax);

                foreach (var typeNode in typeDeclarations)
                {
                    var typeInfo = ExtractTypeInfo(typeNode, namespaceName, code, analysis.FilePath);
                    if (typeInfo != null)
                    {
                        analysis.Types.Add(typeInfo);
                    }
                }

                // Build dependencies from usings and base types
                analysis.Dependencies = BuildDependencies(analysis);
            }
            catch
            {
                // If parsing fails, leave types empty
            }
        }

        private void AnalyzeCsprojFile(FileAnalysis analysis)
        {
            try
            {
                var doc = XDocument.Load(analysis.FilePath);
                var root = doc.Root;
                if (root == null) return;

                // Extract project references as dependencies
                var projectRefs = root.Descendants()
                    .Where(e => e.Name.LocalName == "ProjectReference")
                    .Select(e => e.Attribute("Include")?.Value)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Select(v => Path.GetFileNameWithoutExtension(v!))
                    .ToList();

                // Extract package references
                var packageRefs = root.Descendants()
                    .Where(e => e.Name.LocalName == "PackageReference")
                    .Select(e => e.Attribute("Include")?.Value)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToList();

                analysis.Dependencies = projectRefs.Concat(packageRefs!).ToList();

                // Extract root namespace if specified
                var rootNamespace = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value;

                // Fall back to assembly name or project file name
                if (string.IsNullOrEmpty(rootNamespace))
                {
                    rootNamespace = root.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value;
                }
                if (string.IsNullOrEmpty(rootNamespace))
                {
                    rootNamespace = Path.GetFileNameWithoutExtension(analysis.FilePath);
                }

                analysis.Namespace = rootNamespace ?? "";

                // Create a pseudo-type to represent the project
                var projectType = new CodeTypeInfo
                {
                    Name = Path.GetFileNameWithoutExtension(analysis.FilePath),
                    FullName = rootNamespace ?? Path.GetFileNameWithoutExtension(analysis.FilePath),
                    Kind = CodeTypeKind.Class // Use Class as placeholder
                };

                // Add target framework as a member
                var targetFramework = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "TargetFramework" || e.Name.LocalName == "TargetFrameworks")?.Value;

                if (!string.IsNullOrEmpty(targetFramework))
                {
                    projectType.Members.Add(new CodeMemberInfo
                    {
                        Name = "TargetFramework",
                        Kind = CodeMemberKind.Property,
                        ReturnType = targetFramework
                    });
                }

                // Add output type
                var outputType = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value ?? "Library";

                projectType.Members.Add(new CodeMemberInfo
                {
                    Name = "OutputType",
                    Kind = CodeMemberKind.Property,
                    ReturnType = outputType
                });

                analysis.Types.Add(projectType);
            }
            catch
            {
                // If parsing fails, leave empty
            }
        }

        private CodeTypeInfo? ExtractTypeInfo(SyntaxNode node, string namespaceName, string code, string filePath)
        {
            var typeInfo = new CodeTypeInfo();
            var lineSpan = node.GetLocation().GetLineSpan();
            typeInfo.StartLine = lineSpan.StartLinePosition.Line + 1;
            typeInfo.EndLine = lineSpan.EndLinePosition.Line + 1;

            switch (node)
            {
                case ClassDeclarationSyntax classDecl:
                    typeInfo.Name = classDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Class;
                    typeInfo.XmlDoc = GetXmlDoc(classDecl);
                    ExtractBaseTypes(classDecl.BaseList, typeInfo);
                    ExtractMembers(classDecl.Members, typeInfo, code, filePath, typeInfo.Name);
                    break;

                case InterfaceDeclarationSyntax interfaceDecl:
                    typeInfo.Name = interfaceDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Interface;
                    typeInfo.XmlDoc = GetXmlDoc(interfaceDecl);
                    ExtractBaseTypes(interfaceDecl.BaseList, typeInfo);
                    ExtractMembers(interfaceDecl.Members, typeInfo, code, filePath, typeInfo.Name);
                    break;

                case StructDeclarationSyntax structDecl:
                    typeInfo.Name = structDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Struct;
                    typeInfo.XmlDoc = GetXmlDoc(structDecl);
                    ExtractBaseTypes(structDecl.BaseList, typeInfo);
                    ExtractMembers(structDecl.Members, typeInfo, code, filePath, typeInfo.Name);
                    break;

                case RecordDeclarationSyntax recordDecl:
                    typeInfo.Name = recordDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Record;
                    typeInfo.XmlDoc = GetXmlDoc(recordDecl);
                    ExtractBaseTypes(recordDecl.BaseList, typeInfo);
                    ExtractMembers(recordDecl.Members, typeInfo, code, filePath, typeInfo.Name);
                    break;

                case EnumDeclarationSyntax enumDecl:
                    typeInfo.Name = enumDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Enum;
                    typeInfo.XmlDoc = GetXmlDoc(enumDecl);
                    foreach (var member in enumDecl.Members)
                    {
                        var memberLine = member.GetLocation().GetLineSpan();
                        typeInfo.Members.Add(new CodeMemberInfo
                        {
                            Name = member.Identifier.Text,
                            Kind = CodeMemberKind.Field,
                            StartLine = memberLine.StartLinePosition.Line + 1,
                            EndLine = memberLine.EndLinePosition.Line + 1
                        });
                    }
                    break;

                case DelegateDeclarationSyntax delegateDecl:
                    typeInfo.Name = delegateDecl.Identifier.Text;
                    typeInfo.FullName = string.IsNullOrEmpty(namespaceName) ? typeInfo.Name : $"{namespaceName}.{typeInfo.Name}";
                    typeInfo.Kind = CodeTypeKind.Delegate;
                    typeInfo.Members.Add(new CodeMemberInfo
                    {
                        Name = delegateDecl.Identifier.Text,
                        Kind = CodeMemberKind.Method,
                        ReturnType = delegateDecl.ReturnType.ToString(),
                        Signature = delegateDecl.ToString()
                    });
                    break;

                default:
                    return null;
            }

            return typeInfo;
        }

        private string GetXmlDoc(SyntaxNode node)
        {
            var trivia = node.GetLeadingTrivia()
                .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                           t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                .FirstOrDefault();

            return trivia.ToString().Trim();
        }

        private void ExtractBaseTypes(BaseListSyntax? baseList, CodeTypeInfo typeInfo)
        {
            if (baseList == null) return;

            foreach (var baseType in baseList.Types)
            {
                var typeName = baseType.Type.ToString();

                // Check if this looks like an interface name (starts with 'I' followed by uppercase letter)
                bool looksLikeInterface = typeName.Length > 1 &&
                                          typeName.StartsWith("I") &&
                                          char.IsUpper(typeName[1]);

                // First non-interface type becomes the base class
                if (string.IsNullOrEmpty(typeInfo.BaseType) && !looksLikeInterface)
                {
                    typeInfo.BaseType = typeName;
                }
                else
                {
                    typeInfo.Interfaces.Add(typeName);
                }
            }
        }

        private void ExtractMembers(SyntaxList<MemberDeclarationSyntax> members, CodeTypeInfo typeInfo, string code, string filePath, string typeName)
        {
            foreach (var member in members)
            {
                var memberInfo = ExtractMemberInfo(member, code, filePath, typeName);
                if (memberInfo != null)
                {
                    typeInfo.Members.Add(memberInfo);
                }
            }
        }

        private CodeMemberInfo? ExtractMemberInfo(MemberDeclarationSyntax member, string code, string filePath, string typeName)
        {
            var info = new CodeMemberInfo();
            var lineSpan = member.GetLocation().GetLineSpan();
            info.StartLine = lineSpan.StartLinePosition.Line + 1;
            info.EndLine = lineSpan.EndLinePosition.Line + 1;
            info.XmlDoc = GetXmlDoc(member);

            switch (member)
            {
                case MethodDeclarationSyntax method:
                    info.Name = method.Identifier.Text;
                    info.Kind = CodeMemberKind.Method;
                    info.ReturnType = method.ReturnType.ToString();
                    info.AccessModifier = GetAccessModifier(method.Modifiers);
                    info.IsStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    info.IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                    info.IsVirtual = method.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                    info.IsOverride = method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));
                    info.IsAbstract = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                    info.Parameters = method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}").ToList();
                    info.Signature = $"{info.Name}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "var"))})";

                    if (method.Body != null)
                    {
                        info.Body = method.Body.ToString();
                        ExtractMethodCalls(method.Body, filePath, typeName, info.Name);
                    }
                    else if (method.ExpressionBody != null)
                    {
                        info.Body = method.ExpressionBody.ToString();
                        ExtractMethodCalls(method.ExpressionBody, filePath, typeName, info.Name);
                    }
                    break;

                case PropertyDeclarationSyntax property:
                    info.Name = property.Identifier.Text;
                    info.Kind = CodeMemberKind.Property;
                    info.ReturnType = property.Type.ToString();
                    info.AccessModifier = GetAccessModifier(property.Modifiers);
                    info.IsStatic = property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    info.IsVirtual = property.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                    info.IsOverride = property.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));
                    info.IsAbstract = property.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                    break;

                case FieldDeclarationSyntax field:
                    var variable = field.Declaration.Variables.FirstOrDefault();
                    if (variable == null) return null;
                    info.Name = variable.Identifier.Text;
                    info.Kind = CodeMemberKind.Field;
                    info.ReturnType = field.Declaration.Type.ToString();
                    info.AccessModifier = GetAccessModifier(field.Modifiers);
                    info.IsStatic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    break;

                case EventDeclarationSyntax eventDecl:
                    info.Name = eventDecl.Identifier.Text;
                    info.Kind = CodeMemberKind.Event;
                    info.ReturnType = eventDecl.Type.ToString();
                    info.AccessModifier = GetAccessModifier(eventDecl.Modifiers);
                    info.IsStatic = eventDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    break;

                case ConstructorDeclarationSyntax ctor:
                    info.Name = ctor.Identifier.Text;
                    info.Kind = CodeMemberKind.Constructor;
                    info.AccessModifier = GetAccessModifier(ctor.Modifiers);
                    info.IsStatic = ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    info.Parameters = ctor.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}").ToList();
                    info.Signature = $"{info.Name}({string.Join(", ", ctor.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "var"))})";

                    if (ctor.Body != null)
                    {
                        info.Body = ctor.Body.ToString();
                        ExtractMethodCalls(ctor.Body, filePath, typeName, ".ctor");
                    }
                    break;

                default:
                    return null;
            }

            return info;
        }

        private void ExtractMethodCalls(SyntaxNode body, string filePath, string callerType, string callerMethod)
        {
            var invocations = body.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var callSite = new CallSite
                {
                    CallerType = callerType,
                    CallerMethod = callerMethod,
                    FilePath = filePath,
                    Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };

                switch (invocation.Expression)
                {
                    case MemberAccessExpressionSyntax memberAccess:
                        callSite.CalledMethod = memberAccess.Name.Identifier.Text;
                        callSite.CalledType = memberAccess.Expression.ToString();
                        break;
                    case IdentifierNameSyntax identifier:
                        callSite.CalledMethod = identifier.Identifier.Text;
                        callSite.CalledType = callerType;
                        break;
                }

                if (!string.IsNullOrEmpty(callSite.CalledMethod))
                {
                    CallSites.Add(callSite);
                }
            }
        }

        private string GetAccessModifier(SyntaxTokenList modifiers)
        {
            if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) return "public";
            if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return "protected";
            if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword))) return "internal";
            return "private";
        }

        private List<string> BuildDependencies(FileAnalysis analysis)
        {
            var deps = new HashSet<string>();

            foreach (var type in analysis.Types)
            {
                if (!string.IsNullOrEmpty(type.BaseType))
                    deps.Add(type.BaseType);

                foreach (var iface in type.Interfaces)
                    deps.Add(iface);
            }

            return deps.ToList();
        }

        private FileClassification ClassifyFile(FileAnalysis analysis)
        {
            var fileName = analysis.FileName.ToLowerInvariant();
            var relativePath = analysis.RelativePath.ToLowerInvariant();

            // Config files (including .csproj)
            if (fileName.EndsWith(".csproj") || fileName.EndsWith(".json") ||
                fileName.EndsWith(".config") || fileName.EndsWith(".xml"))
                return FileClassification.Config;

            // Path-based classification
            if (relativePath.Contains("test") || relativePath.Contains("spec"))
                return FileClassification.Test;
            if (relativePath.Contains("viewmodel") || relativePath.Contains("viewmodels"))
                return FileClassification.ViewModel;
            if (relativePath.Contains("model") || relativePath.Contains("models"))
                return FileClassification.Model;
            if (relativePath.Contains("service") || relativePath.Contains("services"))
                return FileClassification.Service;
            if (relativePath.Contains("repository") || relativePath.Contains("repositories"))
                return FileClassification.Repository;
            if (relativePath.Contains("controller") || relativePath.Contains("controllers"))
                return FileClassification.Controller;

            // Name-based classification
            if (fileName.EndsWith("viewmodel.cs"))
                return FileClassification.ViewModel;
            if (fileName.EndsWith("service.cs"))
                return FileClassification.Service;
            if (fileName.EndsWith("repository.cs"))
                return FileClassification.Repository;
            if (fileName.EndsWith("controller.cs"))
                return FileClassification.Controller;
            if (fileName.EndsWith("test.cs") || fileName.EndsWith("tests.cs"))
                return FileClassification.Test;
            if (fileName.EndsWith(".xaml") || fileName.EndsWith(".xaml.cs"))
                return FileClassification.View;

            // Content-based classification
            foreach (var type in analysis.Types)
            {
                if (type.BaseType?.Contains("Window") == true ||
                    type.BaseType?.Contains("Page") == true ||
                    type.BaseType?.Contains("UserControl") == true)
                    return FileClassification.View;

                if (type.Interfaces.Any(i => i.Contains("INotifyPropertyChanged")))
                    return FileClassification.ViewModel;
            }

            return FileClassification.Unknown;
        }

        private string GetLanguage(string extension)
        {
            return LanguageMap.TryGetValue(extension, out var lang) ? lang : "text";
        }

        private int EstimateTokens(long sizeInBytes)
        {
            return (int)(sizeInBytes / 4);
        }
    }
}
