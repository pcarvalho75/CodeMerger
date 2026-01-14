using System;
using System.Collections.Generic;

namespace CodeMerger.Services.Mcp
{
    /// <summary>
    /// Centralized registry for all MCP tool definitions.
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
            return tools.ToArray();
        }

        private static object[] GetReadTools()
        {
            return new object[]
            {
                new
                {
                    name = "codemerger_get_project_overview",
                    description = "Get high-level project information including framework, structure, namespaces, total files, and entry points. Use this first to understand the project before diving into specific files.",
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
                    description = "List all files in the project with their namespaces, classifications (View, Model, Service, etc.) and estimated tokens. Use 'classification' or 'namespace' filter to narrow down results.",
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
                    description = "Get the full content of a specific file by its relative path. For making changes, prefer using codemerger_str_replace for surgical edits rather than rewriting entire files.",
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
                    description = "Search for types, methods, namespaces, or keywords in the codebase. Use this to find where something is defined or used before making changes.",
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
                    description = "Get detailed information about a specific type including its members, base types, and interfaces. Useful for understanding a class before modifying it.",
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
                    description = "Get dependencies of a type (what it uses) and reverse dependencies (what uses it). Essential before renaming or refactoring to understand impact.",
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
                    name = "codemerger_get_type_hierarchy",
                    description = "Get the inheritance hierarchy for all types in the project.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "required", Array.Empty<string>() }
                    }
                },
                new
                {
                    name = "codemerger_grep",
                    description = "Search file contents using regex or plain text. Returns matches with line numbers and context. Ideal for finding specific code patterns, strings, or implementations.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "pattern", new Dictionary<string, string> { { "type", "string" }, { "description", "Search pattern (regex or plain text)" } } },
                                { "isRegex", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Treat pattern as regex (default: true)" }, { "default", true } } },
                                { "caseSensitive", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Case-sensitive search (default: false)" }, { "default", false } } },
                                { "contextLines", new Dictionary<string, object> { { "type", "integer" }, { "description", "Lines of context before/after match (default: 2)" }, { "default", 2 } } },
                                { "maxResults", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum results to return (default: 50)" }, { "default", 50 } } }
                            }
                        },
                        { "required", new[] { "pattern" } }
                    }
                },
                new
                {
                    name = "codemerger_get_context",
                    description = "Intelligently analyze a task description and return relevant files ranked by relevance. Use this to quickly find the right files for a given task.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "task", new Dictionary<string, string> { { "type", "string" }, { "description", "Natural language description of what you want to do (e.g., 'add validation to user input', 'implement new MCP tool')" } } },
                                { "maxFiles", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum files to return (default: 10)" }, { "default", 10 } } },
                                { "maxTokens", new Dictionary<string, object> { { "type", "integer" }, { "description", "Maximum total tokens (default: 50000)" }, { "default", 50000 } } }
                            }
                        },
                        { "required", new[] { "task" } }
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
                    description = "Find all references to a symbol (type, method, property) using semantic analysis. Shows definitions, invocations, and implementations.",
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
                    description = "Get all methods that call a specific method. Part of call graph analysis.",
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
                    description = "Get all methods that a specific method calls. Part of call graph analysis.",
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
                    description = "Replace a unique string in a file with another string. The oldStr must appear exactly once in the file. Use this for surgical edits instead of rewriting entire files. Set newStr to empty string to delete the match.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "oldStr", new Dictionary<string, string> { { "type", "string" }, { "description", "String to find and replace (must be unique in file)" } } },
                                { "newStr", new Dictionary<string, string> { { "type", "string" }, { "description", "Replacement string (empty to delete)" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before modifying (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "path", "oldStr" } }
                    }
                },
                new
                {
                    name = "codemerger_write_file",
                    description = "Write content to a file (create new or overwrite existing). Creates a .bak backup before overwriting. For small changes, prefer codemerger_str_replace instead.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file (e.g., 'Services/MyService.cs')" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "Complete file content to write" } } },
                                { "createBackup", new Dictionary<string, object> { { "type", "boolean" }, { "description", "Create .bak backup before overwriting (default: true)" }, { "default", true } } }
                            }
                        },
                        { "required", new[] { "path", "content" } }
                    }
                },
                new
                {
                    name = "codemerger_preview_write",
                    description = "Preview what a file write would look like without actually writing. Shows a diff of changes. Use this before codemerger_write_file to verify your changes are correct.",
                    inputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "path", new Dictionary<string, string> { { "type", "string" }, { "description", "Relative path to the file" } } },
                                { "content", new Dictionary<string, string> { { "type", "string" }, { "description", "Complete file content to preview" } } }
                            }
                        },
                        { "required", new[] { "path", "content" } }
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
                    description = "Rename a symbol (class, method, variable) across all files in the project. Use preview=true first to see all affected locations before applying.",
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
                    description = "Generate an interface from a class's public members. Returns the generated code which you can then write to a file using codemerger_write_file.",
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
                    description = "Extract a range of lines into a new method. Returns the modified file content which you can then write using codemerger_write_file.",
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
                    description = "Add a parameter to a method and update all call sites. Use preview=true first to see all affected locations.",
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
                    description = "Generate stub implementations for all members of an interface in a class. Returns the code to add.",
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
                    description = "Generate a constructor that initializes selected fields/properties of a class.",
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
                    name = "codemerger_refresh",
                    description = "Refresh the workspace index by re-analyzing all files. Use this after making changes to ensure the index is up to date.",
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
                    description = "Shutdown the CodeMerger MCP server. Use this when the user needs to recompile the project or wants to stop the server. The server will exit and release file locks.",
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
                    description = "List all available CodeMerger projects. Shows project names and indicates which one is currently active/loaded.",
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
                    description = "Switch to a different CodeMerger project. This will set the new project as active and restart the server to load it. Use codemerger_list_projects first to see available projects.",
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
                }
            };
        }
    }
}
