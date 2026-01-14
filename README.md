# CodeMerger Update v2

## Summary

This update simplifies the Claude Desktop configuration. The config no longer needs to change when you switch projects.

**Before:** Config had `codemerger-ProjectName` with `["--mcp", "ProjectName"]`
**After:** Config has fixed `codemerger` with `["--mcp"]` - project determined by settings file

## New Config Format

Your `claude_desktop_config.json` should now look like:

```json
{
  "preferences": {
    "chromeExtensionEnabled": true
  },
  "mcpServers": {
    "codemerger": {
      "command": "C:\\...\\CodeMerger.exe",
      "args": ["--mcp"]
    }
  }
}
```

When you switch projects in CodeMerger GUI, it updates `active_project.txt` instead of the config file. Just restart Claude Desktop to use the new project.

## Changes Included

### 1. Simplified Config (ClaudeDesktopService.cs)
- Fixed entry name: `codemerger` (no project suffix)
- Fixed args: `["--mcp"]` (no project name)
- Migrates old `codemerger-*` entries automatically

### 2. Active Project Tracking (ProjectService.cs)
- New methods: `GetActiveProject()`, `SetActiveProject()`, `ClearActiveProject()`
- Stores active project in `%AppData%/CodeMerger/active_project.txt`

### 3. MCP Mode Uses Settings (App.xaml.cs)
- Reads active project from settings instead of command line args
- Shows helpful error if no active project is set

### 4. Auto-Set Active Project (MainWindow.xaml.cs)
- Calls `SetActiveProject()` when switching projects in GUI
- Remembers last selected project on startup

### 5. Write Tools via MCP (McpServer.cs)
- `codemerger_write_file` - Create/overwrite files with backup
- `codemerger_preview_write` - Preview changes as diff
- `codemerger_rename_symbol` - Rename across project
- `codemerger_generate_interface` - Extract interface from class
- `codemerger_extract_method` - Extract code to new method

### 6. Status Bar Activity (MainWindow.xaml.cs + McpServer.cs)
- Real-time feedback: "🔄 [Project] Reading: file.cs"
- Works when MCP runs as separate process

## Installation

1. Close Visual Studio and CodeMerger
2. Replace these files in your project:
   - `Services/ClaudeDesktopService.cs`
   - `Services/ProjectService.cs`
   - `Services/McpServer.cs`
   - `App.xaml.cs`
   - `MainWindow.xaml.cs`
3. Rebuild the project
4. Open CodeMerger and select your project (this sets the active project)
5. Restart Claude Desktop

## Workflow

1. Open CodeMerger GUI
2. Select/switch to desired project
3. Restart Claude Desktop
4. Claude now has access to that project via MCP

No more editing config files when switching projects!
