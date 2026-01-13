using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            { ".yml", "yaml" }
        };

        public FileAnalysis AnalyzeFile(string filePath, string basePath)
        {
            var fileInfo = new FileInfo(filePath);
            var relativePath = filePath.Substring(basePath.Length).TrimStart('\\', '/');
            var extension = fileInfo.Extension.ToLowerInvariant();

            var analysis = new FileAnalysis
            {
                FilePath = filePath,
                RelativePath = relativePath,
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
                    var typeInfo = ExtractTypeInfo(typeNode);
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

        private CodeTypeInfo? ExtractTypeInfo(SyntaxNode node)
        {
            var typeInfo = new CodeTypeInfo();

            switch (node)
            {
                case ClassDeclarationSyntax classDecl:
                    typeInfo.Name = classDecl.Identifier.Text;
                    typeInfo.Kind = CodeTypeKind.Class;
                    ExtractBaseTypes(classDecl.BaseList, typeInfo);
                    ExtractMembers(classDecl.Members, typeInfo);
                    break;

                case InterfaceDeclarationSyntax interfaceDecl:
                    typeInfo.Name = interfaceDecl.Identifier.Text;
                    typeInfo.Kind = CodeTypeKind.Interface;
                    ExtractBaseTypes(interfaceDecl.BaseList, typeInfo);
                    ExtractMembers(interfaceDecl.Members, typeInfo);
                    break;

                case StructDeclarationSyntax structDecl:
                    typeInfo.Name = structDecl.Identifier.Text;
                    typeInfo.Kind = CodeTypeKind.Struct;
                    ExtractBaseTypes(structDecl.BaseList, typeInfo);
                    ExtractMembers(structDecl.Members, typeInfo);
                    break;

                case RecordDeclarationSyntax recordDecl:
                    typeInfo.Name = recordDecl.Identifier.Text;
                    typeInfo.Kind = CodeTypeKind.Record;
                    ExtractBaseTypes(recordDecl.BaseList, typeInfo);
                    ExtractMembers(recordDecl.Members, typeInfo);
                    break;

                case EnumDeclarationSyntax enumDecl:
                    typeInfo.Name = enumDecl.Identifier.Text;
                    typeInfo.Kind = CodeTypeKind.Enum;
                    foreach (var member in enumDecl.Members)
                    {
                        typeInfo.Members.Add(new CodeMemberInfo
                        {
                            Name = member.Identifier.Text,
                            Kind = CodeMemberKind.Field
                        });
                    }
                    break;

                case DelegateDeclarationSyntax delegateDecl:
                    typeInfo.Name = delegateDecl.Identifier.Text;
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

        private void ExtractBaseTypes(BaseListSyntax? baseList, CodeTypeInfo typeInfo)
        {
            if (baseList == null) return;

            foreach (var baseType in baseList.Types)
            {
                var typeName = baseType.Type.ToString();

                // First one could be base class (if not interface-like name)
                if (string.IsNullOrEmpty(typeInfo.BaseType) && !typeName.StartsWith("I") || !char.IsUpper(typeName.ElementAtOrDefault(1)))
                {
                    typeInfo.BaseType = typeName;
                }
                else
                {
                    typeInfo.Interfaces.Add(typeName);
                }
            }
        }

        private void ExtractMembers(SyntaxList<MemberDeclarationSyntax> members, CodeTypeInfo typeInfo)
        {
            foreach (var member in members)
            {
                var memberInfo = ExtractMemberInfo(member);
                if (memberInfo != null)
                {
                    typeInfo.Members.Add(memberInfo);
                }
            }
        }

        private CodeMemberInfo? ExtractMemberInfo(MemberDeclarationSyntax member)
        {
            var info = new CodeMemberInfo();

            switch (member)
            {
                case MethodDeclarationSyntax method:
                    info.Name = method.Identifier.Text;
                    info.Kind = CodeMemberKind.Method;
                    info.ReturnType = method.ReturnType.ToString();
                    info.AccessModifier = GetAccessModifier(method.Modifiers);
                    info.Signature = $"{info.Name}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "var"))})";
                    break;

                case PropertyDeclarationSyntax property:
                    info.Name = property.Identifier.Text;
                    info.Kind = CodeMemberKind.Property;
                    info.ReturnType = property.Type.ToString();
                    info.AccessModifier = GetAccessModifier(property.Modifiers);
                    break;

                case FieldDeclarationSyntax field:
                    var variable = field.Declaration.Variables.FirstOrDefault();
                    if (variable == null) return null;
                    info.Name = variable.Identifier.Text;
                    info.Kind = CodeMemberKind.Field;
                    info.ReturnType = field.Declaration.Type.ToString();
                    info.AccessModifier = GetAccessModifier(field.Modifiers);
                    break;

                case EventDeclarationSyntax eventDecl:
                    info.Name = eventDecl.Identifier.Text;
                    info.Kind = CodeMemberKind.Event;
                    info.ReturnType = eventDecl.Type.ToString();
                    info.AccessModifier = GetAccessModifier(eventDecl.Modifiers);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    info.Name = ctor.Identifier.Text;
                    info.Kind = CodeMemberKind.Constructor;
                    info.AccessModifier = GetAccessModifier(ctor.Modifiers);
                    info.Signature = $"{info.Name}({string.Join(", ", ctor.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "var"))})";
                    break;

                default:
                    return null;
            }

            return info;
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

            // Config files
            if (fileName.EndsWith(".json") || fileName.EndsWith(".config") || fileName.EndsWith(".xml"))
                return FileClassification.Config;

            return FileClassification.Unknown;
        }

        private string GetLanguage(string extension)
        {
            return LanguageMap.TryGetValue(extension, out var lang) ? lang : "text";
        }

        private int EstimateTokens(long sizeInBytes)
        {
            // Rough estimate: ~4 characters per token for code
            return (int)(sizeInBytes / 4);
        }
    }
}
