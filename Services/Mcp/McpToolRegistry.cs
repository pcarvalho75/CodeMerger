using System;
using System.Collections.Generic;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Centralized registry for all MCP tool definitions.
    /// Categories: Exploration, Searching, Understanding, Editing, Refactoring, Validation, Recovery, Server, Maintenance, Self-Improvement.
    /// </summary>
    public static class McpToolRegistry
    {
        public static object[] GetAllTools()
        {
            var tools = new List<object>();
            tools.AddRange(GetReadTools());
            tools.AddRange(GetWriteTools());
            tools.AddRange(GetRefactoringTools());
            tools.AddRange(GetSemanticTools());
            tools.AddRange(GetServerControlTools());
            tools.AddRange(GetMaintenanceTools());
            tools.AddRange(GetLessonTools());
            tools.AddRange(GetNotesTools());
            tools.AddRange(GetGitTools());
            return tools.ToArray();
        }

        private static object[] GetReadTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_get_project_overview",
                    description = "Get high-level project info: framework, namespaces, file breakdown, entry points, project references.\n" +
                        "Call this once at the start of a session. Then: get_notes to read architecture context, then get_context for task-based exploration.\n" +
                        "MANDATORY FIRST CALL when switching projects or starting a new conversation.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_list_files",
                    description = "List files with namespaces, classifications, and token counts. Filter by namespace or classification.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "classification", new Dictionary<string, string> { { "type", "string" }, { "description", "Filter by classification: View, Model, Service, Controller, Test, Config, Unknown" } } },
                                { "namespace", new Dictionary<string, string> { { "type", "string" }, { "description", "Filter by namespace (partial match supported)" } } },
                                { "limit", new Dictionary<string, string> { { "type", "integer" }, { "description", "Maximum files to return (default 50)" } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_get_file",
                    description = "Get full file content. For large files (>300 lines), prefer get_lines.\n" +
                        "If you only need one method, use get_method_body instead — it's faster and returns exactly what you need.\n" +
                        "Always verify content before str_replace.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                },
                new
                {
                    name = "codemerger_search_code",
                    description = "Search the semantic index for type/method/namespace names. PREFER THIS over grep for finding C# symbols.\n" +
                        "Use grep ONLY for literal text in comments, strings, XAML, or non-C# files.\n" +
                        "Examples: 'where is the Save method?' → search_code. 'Find TODO comments' → grep.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "query", new Dictionary<string, string> { { "type", "string" }, { "description", "Search query (type name, method name, namespace, or keyword)" } } },
                                { "searchIn", new Dictionary<string, string> { { "type", "string" }, { "description", "Where to search: types, methods, files, namespaces, all (default: all)" } } }
                            }
                        },
                        { "required", new[] { "query" } }
                    }
                },
                new
                {
                    name = "codemerger_get_type",
                    description = "Get type details: members with full signatures, base types, interfaces.\n" +
                        "Use to understand a class's API before modifying it. Call get_dependencies before making changes.\n" +
                        "PREFER THIS over get_file when you need to understand what a class exposes — faster and more structured.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the type" } } }
                            }
                        },
                        { "required", new[] { "typeName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_dependencies",
                    description = "Get what a type uses and what uses it. ALWAYS call before modifying any public type, method signature, or property.\n" +
                        "Essential before: rename_symbol, move_file, changing public members, adding/removing properties.\n" +
                        "Prevents breaking unknown consumers — catches errors before they happen instead of at build time.\n" +
                        "MANDATORY before any refactoring that touches public API surface.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the type" } } }
                            }
                        },
                        { "required", new[] { "typeName" } }
                    }
                },
                new
                {
                    name = "codemerger_find_implementations",
                    description = "Find concrete implementations of an interface or base class. Set includeAbstract: true to include abstract classes.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "description", "Interface or base class name to search for" }
                                    }
                                },
                                { "includeAbstract", new Dictionary<string, object>
                                    {
                                        { "type", "boolean" },
                                        { "default", false },
                                        { "description", "Include abstract classes (default: only concrete)" }
                                    }
                                }
                            }
                        },
                        { "required", new[] { "typeName" } }
                    }
                },
                new
                {
                    name = "codemerger_grep",
                    description = "Regex/text search in file contents with line numbers and context.\n" +
                        "ONLY use for: XAML content, string literals, comments, non-C# files, or when you need line-level context.\n" +
                        "Also valid as a SAFETY NET after find_references if results seem incomplete (e.g., lambdas, dynamic calls, nameof()).\n" +
                        "DO NOT use for: finding C# symbol usages (use find_references), finding types/methods (use search_code), " +
                        "checking who calls a method (use get_callers), or verifying property usage across files (use find_references).\n" +
                        "When tempted to grep a C# symbol name, stop and use the semantic tool instead.\n" +
                        "DECISION AID: Is the target a C# identifier (class, method, property, field)? → WRONG TOOL. Use search_code, find_references, or get_callers.\n" +
                        "Need to understand XAML layout/structure? → WRONG TOOL. Use get_xaml_tree.\n" +
                        "Is the target inside a string literal, a comment, or a specific text pattern? → CORRECT TOOL.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "pattern", new Dictionary<string, string> { { "type", "string" }, { "description", "Search pattern (regex or plain text)" } } },
                                { "isRegex", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Treat pattern as regex (default: true)" }, { "default", true } } },
                                { "caseSensitive", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Case-sensitive search (default: false)" }, { "default", false } } },
                                { "contextLines", new Dictionary<string, object> { { "type", "integer" }, { "description", "Lines of context before/after match (default: 2)" }, { "default", 2 } } },
                                { "maxResults", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum results to return (default: 50)" }, { "default", 50 } } },
                                { "summaryOnly", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Return only a file-by-file match count table instead of full line matches. Useful for getting an overview of where a pattern appears across the project (default: false)" }, { "default", false } } }
                            }
                        },
                        { "required", new[] { "pattern" } }
                    }
                },
                new
                {
                    name = "codemerger_get_context",
                    description = "START HERE for any new task. Describe what you want to do in natural language, get relevant files ranked by relevance with call-graph expansion.\n" +
                        "Returns file contents so you can understand the codebase before making changes.\n" +
                        "More efficient than manually calling get_file on multiple files — one call replaces 3-5 individual file reads.\n" +
                        "MANDATORY: Call this BEFORE writing any code or making edits. Do not skip this to 'save time' — it prevents wrong assumptions.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "task", new Dictionary<string, string> { { "type", "string" }, { "description", "Natural language description of what you want to do (e.g., 'add validation to user input', 'implement new MCP tool')" } } },
                                { "maxFiles", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum files to return (default: 10). Increase for complex tasks that touch many files." }, { "default", 10 } } },
                                { "maxTokens", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum total tokens (default: 50000). The tool reserves 30% of budget for call-graph expanded files." }, { "default", 50000 } } }
                            }
                        },
                        { "required", new[] { "task" } }
                    }
                },
                new
                {
                    name = "codemerger_get_lines",
                    description = "Get raw lines with visible whitespace (tabs→, spaces·). Use when str_replace fails, for large files, or to verify content before editing.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "startLine", new Dictionary<string, object> { { "type", "integer" }, { "description", "First line to retrieve (1-indexed)" } } },
                                { "endLine", new Dictionary<string, object> { { "type", "integer" }, { "description", "Last line to retrieve (1-indexed, default: startLine + 20)" } } }
                            }
                        },
                        { "required", new[] { "path", "startLine" } }
                    }
                },
                new
                {
                    name = "codemerger_get_method_body",
                    description = "Get one method's source code by name. PREFER THIS over get_file when you only need one method.\n" +
                        "Much more efficient than loading an entire file just to read a single method.\n" +
                        "If ambiguous (multiple methods with same name), returns disambiguation list — specify typeName to resolve.\n" +
                        "WRONG: get_file then scroll to find the method. RIGHT: get_method_body methodName='HandleTrade'.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the method" } } },
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Qualifying class name if ambiguous" } } },
                                { "includeDoc", new Dictionary<string, object> { { "type", "boolean" }, { "default", false }, { "description", "Include XML doc comments" } } }
                            }
                        },
                        { "required", new[] { "methodName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_xaml_tree",
                    description = "Parse a XAML file and return a compact visual hierarchy with line numbers, element names, x:Name, Headers, Bindings, and Content.\n" +
                        "USE THIS instead of grep or get_lines when you need to understand XAML layout structure.\n" +
                        "Returns: element tree with line ranges, named elements list, and all {Binding} references.\n" +
                        "One call replaces 5-8 grep/get_lines calls when navigating XAML files.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the XAML file" } } }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                }
            };
        }

        private static object[] GetSemanticTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_find_references",
                    description = "Find all references to a symbol using semantic analysis. PREFER THIS over grep for verifying C# symbol usage.\n" +
                        "Use to: check if a property/method/type is used, verify wiring after adding new members, find all consumers before refactoring.\n" +
                        "Tracks: method invocations, property/field accesses (reads and writes), type inheritance, interface implementations.\n" +
                        "LIMITATION: Uses syntax-tree heuristics, not full semantic compilation. May miss references in: " +
                        "lambdas assigned to variables, dynamic/reflection calls, or nameof() expressions. " +
                        "Common .NET/LINQ members (Where, Select, Add, Count, etc.) are filtered out to reduce noise. " +
                        "If results seem incomplete, follow up with grep as a safety net.\n" +
                        "Only use grep instead when searching for non-C# content (XAML, comments, string literals).\n" +
                        "EXAMPLES: 'Is SaveTrade used anywhere?' → find_references. 'Who implements IDataStream?' → find_references.\n" +
                        "WRONG: grep 'SaveTrade' to find usages. RIGHT: find_references symbolName='SaveTrade'.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "symbolName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the symbol to find references for" } } },
                                { "symbolKind", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional: type, method, property, field to narrow search" } } }
                            }
                        },
                        { "required", new[] { "symbolName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_callers",
                    description = "Get all methods that call a specific method or access a specific property/field. PREFER THIS over grep when tracing call chains.\n" +
                        "Essential before modifying method signatures — shows exactly what will break. Pair with get_callees for full call graph.\n" +
                        "WRONG: grep 'MethodName' to find who calls it. RIGHT: get_callers methodName='MethodName'.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the method to find callers for" } } },
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional: type containing the method to narrow search" } } }
                            }
                        },
                        { "required", new[] { "methodName" } }
                    }
                },
                new
                {
                    name = "codemerger_get_callees",
                    description = "Get all methods that a specific method calls. Use to understand a method's dependencies before modifying it.\n" +
                        "Pair with get_callers for full call graph understanding.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the method to find callees for" } } },
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional: type containing the method to narrow search" } } }
                            }
                        },
                        { "required", new[] { "methodName" } }
                    }
                }
            };
        }

        private static object[] GetWriteTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_str_replace",
                    description = "Replace a unique string in a file. oldStr must appear exactly once.\n\n" +
                        "PREFER specialized tools: rename_symbol for renaming, move_file for moving, add_parameter for params, extract_method for extraction.\n" +
                        "On failure: use get_lines to see exact whitespace, then retry. One edit at a time; verify with build between edits.\n" +
                        "For .csproj: grep existing PackageReferences before adding new ones.\n" +
                        "WORKFLOW: Present roadmap first, pause between steps for user OK (unless told otherwise), end with Review & Cleanup step.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "oldStr", new Dictionary<string, string> { { "type", "string" }, { "description", "String to find and replace (must be unique in file)" } } },
                                { "newStr", new Dictionary<string, string> { { "type", "string" }, { "description", "Replacement string (empty to delete)" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before modifying (default: true)" }, { "default", true } } },
                            { "normalizeIndent", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Ignore leading whitespace differences when matching (default: false). Use when exact indentation match fails." }, { "default", false } } }
                            }
                        },
                        { "required", new[] { "path", "oldStr" } }
                        }
            },
                    new
                    {
                        name = "codemerger_write_file",
                    description = "Write full file content (create or overwrite). Creates .bak backup.\n\n" +
                        "PREFER str_replace for small/medium changes, rename_symbol for renaming, move_file for moving.\n" +
                        "For files >600 lines: write skeleton first, fill methods via str_replace. Set preview: true to diff without writing.\n" +
                        "WORKFLOW: Present roadmap first, pause between steps for user OK (unless told otherwise), end with Review & Cleanup step.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file (e.g., 'Services/MyService.cs')" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "Complete file content to write" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before overwriting (default: true)" }, { "default", true } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Preview diff without writing (default: false)" }, { "default", false } } }
                            }
                        },
                        { "required", new[] { "path", "content" } }
                    }
                },
                new
                {
                    name = "codemerger_delete_file",
                    description = "Delete a file (.bak backup created). Use undo to restore. Check get_dependencies first.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file to delete" } } }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                },
                new
                {
                    name = "codemerger_grep_replace",
                    description = "Regex find-and-replace across all project files. ALWAYS preview first (default), then apply.\n" +
                        "Use for: renaming strings in XAML, updating text patterns across many files, bulk find-replace.\n" +
                        "NOT for: renaming C# symbols (use rename_symbol instead).\n" +
                        "Preview numbers each match. Use excludeMatches to skip specific matches, or excludePattern to skip lines matching a regex.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "pattern", new Dictionary<string, string> { { "type", "string" }, { "description", "Regex pattern to search for" } } },
                                { "replacement", new Dictionary<string, string> { { "type", "string" }, { "description", "Replacement string (supports regex groups like $1, $2)" } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only show what would change without applying (default: true)" }, { "default", true } } },
                                { "caseSensitive", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Case-sensitive matching (default: false)" }, { "default", false } } },
                            { "fileFilter", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional: only match files whose path contains this string (e.g., '.xaml', 'Services/')" } } },
                            { "excludeMatches", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, string> { { "type", "integer" } } }, { "description", "Optional: match indices to skip when applying (from preview output). E.g., [3] to skip match #3." } } },
                            { "excludePattern", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional: regex pattern — lines matching this are skipped (not replaced). E.g., 'private.*BuildFullInput' to skip the definition." } } }
                        }
                        },
                        { "required", new[] { "pattern", "replacement" } }
                    }
                },
                new
                {
                    name = "codemerger_undo",
                    description = "Restore a file from its .bak backup. Only restores the most recent backup.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file to restore" } } }
                            }
                        },
                        { "required", new[] { "path" } }
                    }
                },
                new
                {
                    name = "codemerger_replace_lines",
                    description = "Replace a range of lines in a file with new content. Use when you know exact line numbers from get_lines.\n" +
                        "Simpler than str_replace when line numbers are known. Creates .bak backup.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "startLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "First line to replace (1-indexed)" } } },
                                { "endLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "Last line to replace (1-indexed, inclusive)" } } },
                                { "newContent", new Dictionary<string, string> { { "type", "string" }, { "description", "Replacement content (replaces all lines from startLine to endLine)" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before modifying (default: true)" }, { "default", true } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Preview the replacement without applying (default: false)" }, { "default", false } } }
                            }
                        },
                        { "required", new[] { "path", "startLine", "endLine", "newContent" } }
                    }
                },
                new
                {
                    name = "codemerger_move_file",
                    description = "Move/rename a file and update all using statements and references. Always preview first. Call get_dependencies before using.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "oldPath", new Dictionary<string, string> { { "type", "string" }, { "description", "Current relative path of the file" } } },
                                { "newPath", new Dictionary<string, string> { { "type", "string" }, { "description", "New relative path for the file" } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only show what would change without applying (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "oldPath", "newPath" } }
                    }
                }
            };
        }

        private static object[] GetRefactoringTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_rename_symbol",
                    description = "Rename a symbol across all files. ALWAYS use instead of str_replace for renaming.\n" +
                        "Call get_dependencies first. Always preview=true first, then confirm with user before preview=false.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "oldName", new Dictionary<string, string> { { "type", "string" }, { "description", "Current name of the symbol" } } },
                                { "newName", new Dictionary<string, string> { { "type", "string" }, { "description", "New name for the symbol" } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only show what would change without applying (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "oldName", "newName" } }
                    }
                },
                new
                {
                    name = "codemerger_generate_interface",
                    description = "Generate an interface from a class's public members. Returns code — write to file, then use implement_interface.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "className", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the class to extract interface from" } } },
                                { "interfaceName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name for the generated interface (default: I{ClassName})" } } }
                            }
                        },
                        { "required", new[] { "className" } }
                    }
                },
                new
                {
                    name = "codemerger_extract_method",
                    description = "Extract a line range into a new method. Returns modified content — write with write_file. Use get_lines to identify range.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "filePath", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "startLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "First line to extract (1-indexed)" } } },
                                { "endLine", new Dictionary<string, string> { { "type", "integer" }, { "description", "Last line to extract (1-indexed)" } } },
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name for the new method" } } }
                            }
                        },
                        { "required", new[] { "filePath", "startLine", "endLine", "methodName" } }
                    }
                },
                new
                {
                    name = "codemerger_add_parameter",
                    description = "Add a parameter to a method and update all call sites with a default value. Use instead of str_replace for this.\n" +
                        "Preview first, confirm with user before applying.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "typeName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the type containing the method" } } },
                                { "methodName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the method to add parameter to" } } },
                                { "parameterType", new Dictionary<string, string> { { "type", "string" }, { "description", "Type of the new parameter (e.g., 'string', 'int', 'bool')" } } },
                                { "parameterName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the new parameter" } } },
                                { "defaultValue", new Dictionary<string, string> { { "type", "string" }, { "description", "Default value for the parameter at call sites (e.g., 'null', '0', 'true')" } } },
                                { "preview", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only show what would change without applying (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "typeName", "methodName", "parameterType", "parameterName", "defaultValue" } }
                    }
                },
                new
                {
                    name = "codemerger_implement_interface",
                    description = "Generate stub implementations for all interface members in a class. Returns code — insert via str_replace.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "className", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the class that should implement the interface" } } },
                                { "interfaceName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the interface to implement" } } }
                            }
                        },
                        { "required", new[] { "className", "interfaceName" } }
                    }
                },
                new
                {
                    name = "codemerger_generate_constructor",
                    description = "Generate a constructor initializing selected fields/properties. Returns code — insert via str_replace.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "className", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the class to generate constructor for" } } },
                                { "fields", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, string> { { "type", "string" } } }, { "description", "Optional: specific field/property names to include. If empty, includes all fields and properties." } } }
                            }
                        },
                        { "required", new[] { "className" } }
                    }
                }
            };
        }

        private static object[] GetServerControlTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_build",
                    description = "Validate the project compiles correctly.\n\n" +
                        "Default: full `dotnet build` with NuGet/XAML (10-30s).\n" +
                        "quickCheck: true for fast Roslyn syntax-only check (~1s). Use after quick edits.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "configuration", new Dictionary<string, object> { { "type", "string" }, { "description", "Build configuration: Debug or Release (default: Debug)" }, { "default", "Debug" } } },
                                { "verbose", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Include full build output (default: false)" }, { "default", false } } },
                                { "quickCheck", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Fast Roslyn syntax-only check instead of full build (default: false)" }, { "default", false } } },
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Specific .csproj or .sln to build (filename or path). For quickCheck: specific .cs file. Default: auto-detect (prefers .sln)" } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_refresh",
                    description = "Re-analyze all files to update the index. Only needed after external edits (outside MCP tools).",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_shutdown",
                    description = "Shutdown the server and release all file locks.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_list_projects",
                    description = "List available projects and shared directories between them. Use switch_project to change.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_switch_project",
                    description = "Switch to a different project (hot-swap, no restart). Pass comma-separated names to merge workspaces.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "projectName", new Dictionary<string, string> { { "type", "string" }, { "description", "Name of the project to switch to" } } }
                            }
                        },
                        { "required", new[] { "projectName" } }
                    }
                },
                new
                {
                    name = "codemerger_help",
                    description = "Get the CodeMerger user manual. Returns setup instructions, tool reference, workflows, tips, and troubleshooting.\n" +
                        "Use without topic for full manual, or specify a topic to get a specific section.\n" +
                        "Available topics: overview, requirements, install-claude, install-codemerger, quickstart, ui, workspaces, " +
                        "tools-exploration, tools-semantic, tools-editing, tools-refactoring, tools-build, tools-project, " +
                        "tools-maintenance, tools-notes, tools-lessons, tools-git, workflows, tips, troubleshooting.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "topic", new Dictionary<string, string> { { "type", "string" }, { "description", "Specific topic to look up (e.g., 'quickstart', 'troubleshooting', 'str_replace'). Omit for full manual." } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                }
            };
        }

        private static object[] GetMaintenanceTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_clean_backups",
                    description = "Delete .bak files. Call without confirm to preview, then with confirm: true to delete.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "confirm", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Must be true to actually delete. If false or omitted, only shows what would be deleted." } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_find_duplicates",
                    description = "Find duplicate/similar code blocks ranked by impact. Follow up with extract_method to refactor.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "minLines", new Dictionary<string, object> { { "type", "integer" }, { "description", "Minimum lines for a code block to be considered (default: 5)" }, { "default", 5 } } },
                                { "minSimilarity", new Dictionary<string, object> { { "type", "integer" }, { "description", "Minimum similarity percentage 0-100 (default: 80). Use 90+ for near-exact matches, 70 for loose similarity." }, { "default", 80 } } },
                                { "maxResults", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum duplicate clusters to return (default: 20)" }, { "default", 20 } } }
                            }
                        },
                        { "required", Array.Empty<string>() }
                    }
                }
            };
        }

        private static object[] GetLessonTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_lessons",
                    description = "Manage self-improvement lessons. Commands: log, get, delete, sync, submit. Types: description|implementation|new_tool|workflow|performance|error_handling",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "command", new Dictionary<string, object> { { "type", "string" }, { "description", "Action to perform" }, { "enum", new[] { "log", "get", "delete", "sync", "submit" } } } },
                                { "type", new Dictionary<string, string> { { "type", "string" }, { "description", "Category (for 'log'): description, implementation, new_tool, workflow, performance, error_handling" } } },
                                { "component", new Dictionary<string, string> { { "type", "string" }, { "description", "Which component is affected (for 'log')" } } },
                                { "observation", new Dictionary<string, string> { { "type", "string" }, { "description", "What happened or what you noticed (for 'log')" } } },
                                { "proposal", new Dictionary<string, string> { { "type", "string" }, { "description", "How it could be improved (for 'log')" } } },
                                { "suggestedCode", new Dictionary<string, string> { { "type", "string" }, { "description", "Optional code snippet (for 'log')" } } },
                                { "number", new Dictionary<string, object> { { "type", "integer" }, { "description", "Lesson number (for 'delete' or 'submit')" } } },
                                { "all", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Delete all local lessons (for 'delete')" } } }
                            }
                        },
                        { "required", new[] { "command" } }
                    }
                }
            };
        }

        private static object[] GetNotesTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_notes",
                    description = "Manage project notes (CODEMERGER_NOTES.md). Commands: get, add, update, delete, clear.\n" +
                        "IMPORTANT: Call 'get' after get_project_overview to load architecture context and avoid rediscovering the project from scratch.\n" +
                        "Notes contain architecture summaries, conventions, and gotchas that save 5-10 exploratory tool calls.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "command", new Dictionary<string, object> { { "type", "string" }, { "description", "Action to perform" }, { "enum", new[] { "get", "add", "update", "delete", "clear" } } } },
                                { "note", new Dictionary<string, string> { { "type", "string" }, { "description", "Note content (for 'add')" } } },
                                { "section", new Dictionary<string, string> { { "type", "string" }, { "description", "Section name (for 'add', 'update', 'clear')" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "New section content (for 'update')" } } },
                                { "lineNumber", new Dictionary<string, object> { { "type", "integer" }, { "description", "Line number to delete (for 'delete')" } } }
                            }
                        },
                        { "required", new[] { "command" } }
                    }
                }
            };
        }

        private static object[] GetGitTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_git_status",
                    description = "Show modified, staged, and untracked files.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_git_commit",
                    description = "Stage all and commit. Set push: true to also push to remote.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "message", new Dictionary<string, string> { { "type", "string" }, { "description", "Commit message" } } },
                                { "push", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Also push to remote after committing (default: false)" }, { "default", false } } }
                            }
                        },
                        { "required", new[] { "message" } }
                    }
                },
                new
                {
                    name = "codemerger_git_push",
                    description = "Push committed changes to remote.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
            };
        }
    }
}
