using System.Collections.Generic;

namespace CodeMerger.Models
{
    public class FileAnalysis
    {
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public FileClassification Classification { get; set; } = FileClassification.Unknown;
        public long SizeInBytes { get; set; }
        public int EstimatedTokens { get; set; }

        public List<CodeTypeInfo> Types { get; set; } = new List<CodeTypeInfo>();
        public List<string> Usings { get; set; } = new List<string>();
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    public class CodeTypeInfo
    {
        public string Name { get; set; } = string.Empty;
        public CodeTypeKind Kind { get; set; } = CodeTypeKind.Class;
        public string BaseType { get; set; } = string.Empty;
        public List<string> Interfaces { get; set; } = new List<string>();
        public List<CodeMemberInfo> Members { get; set; } = new List<CodeMemberInfo>();
    }

    public class CodeMemberInfo
    {
        public string Name { get; set; } = string.Empty;
        public CodeMemberKind Kind { get; set; } = CodeMemberKind.Method;
        public string ReturnType { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string AccessModifier { get; set; } = "private";
    }

    public enum FileClassification
    {
        Unknown,
        View,
        ViewModel,
        Model,
        Service,
        Repository,
        Controller,
        Test,
        Config,
        Utility
    }

    public enum CodeTypeKind
    {
        Class,
        Interface,
        Struct,
        Enum,
        Record,
        Delegate
    }

    public enum CodeMemberKind
    {
        Method,
        Property,
        Field,
        Event,
        Constructor
    }
}
