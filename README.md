# CodeMerger v2.0 Update - Smart Context Features

## New Tools Added

### 1. `codemerger_search_content`
**Grep-like search inside file contents**

Search for text or regex patterns across all files in your project. Returns matching lines with surrounding context.

**Parameters:**
- `pattern` (required): Search pattern (text or regex)
- `isRegex` (default: true): Treat pattern as regex
- `caseSensitive` (default: false): Case-sensitive search
- `contextLines` (default: 2): Lines of context before/after match
- `maxResults` (default: 50): Maximum matches to return

**Example queries:**
- Find all TODOs: `pattern: "TODO"`
- Find exception handling: `pattern: "catch.*Exception"`
- Find async methods: `pattern: "async Task"`
- Find specific strings: `pattern: "connection string"` with `isRegex: false`

---

### 2. `codemerger_get_context_for_task`
**AI-native smart context gathering**

Describe what you want to do in natural language, and get the most relevant files automatically ranked by relevance.

**Parameters:**
- `task` (required): Natural language description of your task
- `maxFiles` (default: 10): Maximum files to return
- `maxTokens` (default: 50000): Token budget for context

**How it works:**
1. Extracts keywords and concepts from your task description
2. Scores each file based on:
   - Type/class name matches
   - Method name matches
   - File classification relevance
   - Dependency relationships
3. Returns files ranked by relevance with:
   - Match reasons explaining why each file was selected
   - Suggestions for how to approach the task
   - Related types to consider

**Example tasks:**
- "Add a new MCP tool for exporting project to JSON"
- "Fix the file path handling in GetFile method"
- "Add a new model class for user preferences"
- "Implement caching for the code analyzer"

---

## Installation

### Step 1: Backup existing files
Before replacing, backup your current:
- `Services\McpServer.cs`

### Step 2: Copy new files
Copy from this package:
- `Services\McpServer.cs` → Replace existing
- `Services\ContextAnalyzer.cs` → New file (add to project)

### Step 3: Add to Visual Studio project
If using Visual Studio:
1. Right-click on `Services` folder
2. Add → Existing Item → Select `ContextAnalyzer.cs`

### Step 4: Rebuild
Build the project to verify no compilation errors.

### Step 5: Restart MCP Server
1. Stop the MCP server if running
2. Re-index your project (Generate Chunks)
3. Start MCP server

---

## Changes Summary

### McpServer.cs
- Added `ContextAnalyzer` field and initialization in `IndexProject()`
- Added two new tools to `HandleListTools()`
- Added `SearchContent()` and `GetContextForTask()` handlers
- Fixed path normalization bug in `GetFile()` (now works with / or \)
- Version bumped to 2.0.0

### ContextAnalyzer.cs (NEW)
- `SearchContent()`: Regex-based file content search
- `GetContextForTask()`: Smart context analysis with keyword extraction
- Result models with `ToMarkdown()` for clean output
- Dependency-aware file scoring

---

## Usage Tips

### Best workflow with Claude:
1. **Start with context**: Use `codemerger_get_context_for_task` first
   ```
   "I want to add a new MCP tool for searching content"
   ```
2. **Drill down**: Use `codemerger_get_file` to read specific files
3. **Search patterns**: Use `codemerger_search_content` to find usages
   ```
   "HandleToolCall|CreateToolResponse"
   ```

### Token efficiency:
- Set `maxTokens` based on your context window
- Use `maxFiles` to limit scope for focused tasks
- The smart context prioritizes smaller, more relevant files

---

## Future Enhancements (Roadmap)

- [ ] `codemerger_write_file` - Two-way code editing
- [ ] `codemerger_get_callers` - Call hierarchy analysis
- [ ] `codemerger_create_patch` - Generate diffs for changes
- [ ] File watching for live index updates
- [ ] Semantic queries using Roslyn ("find all async methods")
