using System.Collections.Generic;

namespace CodeMerger.Models
{
    public class FileAnalysis
    {
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        /// <summary>
        /// The root input directory this file belongs to. Used to disambiguate files when multiple directories are indexed.
        /// </summary>
        public string RootDirectory { get; set; } = string.Empty;
        
        /// <summary>
        /// The workspace name this file was indexed from. Used in merged workspace mode to track file origins.
        /// </summary>
        public string SourceWorkspace { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public FileClassification Classification { get; set; } = FileClassification.Unknown;
        public long SizeInBytes { get; set; }
        public int EstimatedTokens { get; set; }

        public List<CodeTypeInfo> Types { get; set; } = new List<CodeTypeInfo>();
        public List<string> Usings { get; set; } = new List<string>();
        public List<string> Dependencies { get; set; } = new List<string>();

        /// <summary>
        /// Primary namespace declared in this file (first namespace found)
        /// </summary>
        public string Namespace { get; set; } = string.Empty;
    }

    public class CodeTypeInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;  // Namespace.TypeName
        public CodeTypeKind Kind { get; set; } = CodeTypeKind.Class;
        public string BaseType { get; set; } = string.Empty;
        public List<string> Interfaces { get; set; } = new List<string>();
        public List<CodeMemberInfo> Members { get; set; } = new List<CodeMemberInfo>();
        public bool IsAbstract { get; set; }

        // Location info
        public int StartLine { get; set; }
        public int EndLine { get; set; }

        // Documentation
        public string XmlDoc { get; set; } = string.Empty;
    }

    public class CodeMemberInfo
    {
        public string Name { get; set; } = string.Empty;
        public CodeMemberKind Kind { get; set; } = CodeMemberKind.Method;
        public string ReturnType { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string AccessModifier { get; set; } = "private";

        // Extended info
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Body { get; set; } = string.Empty;
        public List<string> Parameters { get; set; } = new List<string>();
        public List<string> CalledMethods { get; set; } = new List<string>();
        public bool IsStatic { get; set; }
        public bool IsAsync { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsOverride { get; set; }
        public bool IsAbstract { get; set; }
        public string XmlDoc { get; set; } = string.Empty;
    }

    // Call site information for call graph
    public class CallSite
    {
        public string CallerType { get; set; } = string.Empty;
        public string CallerMethod { get; set; } = string.Empty;
        public string CalledType { get; set; } = string.Empty;
        public string CalledMethod { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
    }

    // Symbol usage information
    public class SymbolUsage
    {
        public string SymbolName { get; set; } = string.Empty;
        public string SymbolKind { get; set; } = string.Empty;  // Type, Method, Property, Field
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public int Column { get; set; }
        public string Context { get; set; } = string.Empty;  // Line of code
        public UsageKind UsageKind { get; set; } = UsageKind.Reference;
    }

    public enum UsageKind
    {
        Definition,
        Reference,
        Implementation,
        Override,
        Invocation
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
