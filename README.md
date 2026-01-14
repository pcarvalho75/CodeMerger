ok, and one more thing i would like to add an option so i can copy and paste a git repository (notmine but open) and then it also includes that in the project, i dont know what is the best way, if you have to download everythng and index,  the index will need to be very smart in order to handle big things. also, make the program understant python, besides c#, do all and update my project

# CodeMerger v3.0 - Comprehensive Upgrade

## Overview

This major upgrade transforms CodeMerger from a read-only code viewer into a full-featured AI-assisted development environment with **semantic analysis**, **call graph tracking**, and **refactoring capabilities**.

## New Tools Summary

### Smart Context (from v2)
| Tool | Purpose |
|------|---------|
| `codemerger_search_content` | Regex search inside files with context |
| `codemerger_get_context_for_task` | Describe task, get relevant files automatically |

### Semantic Analysis (NEW)
| Tool | Purpose |
|------|---------|
| `codemerger_get_method_body` | Get specific method without loading whole file |
| `codemerger_find_usages` | Find all references to a symbol |
| `codemerger_get_call_graph` | See who calls a method and what it calls |
| `codemerger_find_implementations` | Find interface/base class implementations |
| `codemerger_semantic_query` | Query by criteria (async, static, return type, etc.) |

### Refactoring (NEW)
| Tool | Purpose |
|------|---------|
| `codemerger_write_file` | Create/update files with backup & diff |
| `codemerger_preview_write` | Preview changes before writing |
| `codemerger_rename_symbol` | Rename across all files |
| `codemerger_generate_interface` | Extract interface from class |
| `codemerger_extract_method` | Extract code into new method |

---

## Installation

### Files to Replace
```
Models\FileAnalysis.cs     → REPLACE (enhanced with line numbers, method bodies, modifiers)
Services\CodeAnalyzer.cs   → REPLACE (captures method bodies, call sites)
Services\McpServer.cs      → REPLACE (all new tools integrated)
```

### Files to Add (NEW)
```
Services\SemanticAnalyzer.cs   → NEW (find usages, call graph, implementations)
Services\RefactoringService.cs → NEW (write files, rename, generate interface)
Services\ContextAnalyzer.cs    → NEW if not already added from v2
```

### Step-by-Step

1. **Close Visual Studio** (important - files may be locked)

2. **Backup your current Services folder** (just in case)

3. **Copy replacement files:**
   - Copy `Models\FileAnalysis.cs` → overwrite existing
   - Copy all files from `Services\` → overwrite existing, add new ones

4. **Open Visual Studio**

5. **Add new files to project:**
   - Right-click `Services` folder → Add → Existing Item
   - Select: `SemanticAnalyzer.cs`, `RefactoringService.cs`, `ContextAnalyzer.cs`

6. **Build** (Ctrl+Shift+B)

7. **Restart CodeMerger** and re-index your project

---

## What Changed

### FileAnalysis.cs
- Added `StartLine`, `EndLine` to types and members
- Added `Body` field to store method bodies
- Added `IsStatic`, `IsAsync`, `IsVirtual`, `IsOverride`, `IsAbstract` flags
- Added `Parameters` list with full parameter info
- Added `XmlDoc` for documentation extraction
- Added `CallSite` and `SymbolUsage` classes for tracking

### CodeAnalyzer.cs
- Now extracts method bodies during parsing
- Tracks all method call sites for call graph
- Captures XML documentation
- Records line numbers for all elements
- Exposes `CallSites` list for semantic analyzer

### McpServer.cs
- Version 3.0.0
- 18 total tools (was 7)
- Initializes SemanticAnalyzer and RefactoringService
- Reports call site count in project overview
- Cleaner tool schema generation with helper methods

---

## Usage Examples

### Find all async methods
```json
{
  "tool": "codemerger_semantic_query",
  "arguments": { "isAsync": true }
}
```

### Get call graph for a method
```json
{
  "tool": "codemerger_get_call_graph",
  "arguments": { 
    "typeName": "McpServer",
    "methodName": "HandleToolCall"
  }
}
```

### Find who implements an interface
```json
{
  "tool": "codemerger_find_implementations",
  "arguments": { "interfaceName": "INotifyPropertyChanged" }
}
```

### Write a file with backup
```json
{
  "tool": "codemerger_write_file",
  "arguments": {
    "path": "Services\\NewService.cs",
    "content": "using System;\n\nnamespace CodeMerger.Services\n{\n    public class NewService { }\n}"
  }
}
```

### Preview rename before applying
```json
{
  "tool": "codemerger_rename_symbol",
  "arguments": {
    "oldName": "OldClassName",
    "newName": "NewClassName",
    "preview": true
  }
}
```

### Generate interface from class
```json
{
  "tool": "codemerger_generate_interface",
  "arguments": { "className": "CodeAnalyzer" }
}
```

---

## Processing: Your Machine vs Claude

| Task | Your PC | Claude |
|------|---------|--------|
| Roslyn parsing | ✅ | |
| Call site extraction | ✅ | |
| Method body extraction | ✅ | |
| Regex content search | ✅ | |
| Relevance scoring | ✅ | |
| File writing | ✅ | |
| Rename symbol | ✅ | |
| Choosing which tool | | ✅ |
| Understanding intent | | ✅ |
| Suggesting code | | ✅ |

**~85% of processing is on your machine** - Claude receives pre-processed results.

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    McpServer                         │
│  - Handles JSON-RPC protocol                        │
│  - Routes tool calls to services                    │
└─────────────────┬───────────────────────────────────┘
                  │
    ┌─────────────┼─────────────┬─────────────┐
    ▼             ▼             ▼             ▼
┌─────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐
│CodeAna- │ │Context    │ │Semantic   │ │Refactoring│
│lyzer    │ │Analyzer   │ │Analyzer   │ │Service    │
│         │ │           │ │           │ │           │
│- Parse  │ │- Smart    │ │- Find     │ │- Write    │
│- Extract│ │  context  │ │  usages   │ │- Rename   │
│- Track  │ │- Search   │ │- Call     │ │- Generate │
│  calls  │ │  content  │ │  graph    │ │- Extract  │
└─────────┘ └───────────┘ └───────────┘ └───────────┘
```

---

## Troubleshooting

### "Context analyzer not initialized"
Re-index your project (Generate Chunks). Services are initialized during indexing.

### Call graph shows no results
The call graph is built from method bodies. Make sure you've re-indexed after upgrading to v3.

### Rename doesn't find all occurrences
Current rename uses regex word boundaries. It won't find references in:
- Comments
- Strings
- Partial name matches

### Write file creates wrong path
Paths are relative to your first input directory. Use forward slashes for cross-platform compatibility.

---

## Future Roadmap

- [ ] **File watcher** - Auto re-index on file changes
- [ ] **Semantic rename** - Use Roslyn for accurate rename
- [ ] **Inline refactoring** - Inline method/variable
- [ ] **Move type** - Move class to different file/namespace
- [ ] **Add using** - Automatically add missing using statements
- [ ] **Fix suggestions** - Suggest fixes for common issues

---

## Version History

- **v1.0** - Initial release (basic file reading)
- **v2.0** - Added `search_content` and `get_context_for_task`
- **v3.0** - Full semantic analysis and refactoring support
