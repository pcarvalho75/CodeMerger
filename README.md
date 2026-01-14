# CodeMerger

Advanced Code Merger with MCP Server support for Claude AI integration.

## Features

### 1. Chunk-Based Export
- Analyzes your codebase using Roslyn
- Generates intelligent chunks (max 150k tokens each)
- Creates a master index with type hierarchy, dependencies, and file classifications
- Perfect for uploading to Claude Projects

### 2. MCP Server Mode
- For large projects (500k+ tokens)
- Dynamic code retrieval - Claude fetches only what it needs
- No need to upload everything upfront
- Tools available:
  - `codemerger_get_project_overview` - Project summary
  - `codemerger_list_files` - List files with classifications
  - `codemerger_get_file` - Get file contents
  - `codemerger_search_code` - Search types, methods, keywords
  - `codemerger_get_type` - Type details with members
  - `codemerger_get_dependencies` - Dependency analysis
  - `codemerger_get_type_hierarchy` - Full type hierarchy

## Installation

1. Ensure .NET 8.0 SDK is installed
2. Open the solution in Visual Studio 2022
3. Build and run

Or from command line:
```bash
dotnet restore
dotnet build
dotnet run
```

## Usage

### For Small/Medium Projects (< 500k tokens)
1. Create a new project
2. Add input directories
3. Configure extensions and ignored folders
4. Click "Generate Chunks"
5. Upload all generated files to a Claude Project

### For Large Projects (> 500k tokens)
1. Create and configure your project
2. Click "Start MCP Server"
3. Copy the configuration to your Claude Desktop config:
   - Windows: `%APPDATA%\Claude\claude_desktop_config.json`
   - Mac: `~/Library/Application Support/Claude/claude_desktop_config.json`
4. Restart Claude Desktop
5. Claude can now query your codebase dynamically

## Claude Desktop Configuration

Add this to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "codemerger": {
      "command": "C:\\path\\to\\CodeMerger.exe",
      "args": ["--mcp", "YourProjectName"]
    }
  }
}
```

## Output Structure

```
Desktop/CodeMerger/YourProject/
├── project_config.json          # Project settings
├── YourProject_master_index.txt # Global index
├── YourProject_chunk_1.txt      # Code chunk
├── YourProject_chunk_2.txt      # Code chunk
└── ...
```

## Recommendations

| Project Size | Recommended Mode |
|-------------|------------------|
| < 100k tokens | Generate Chunks |
| 100k - 500k tokens | Generate Chunks |
| > 500k tokens | MCP Server |

The app will show a recommendation banner based on your project size.

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- Visual Studio 2022 (for development)

## License

MIT License
