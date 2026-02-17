namespace CodeMerger.Models;

/// <summary>
/// Static user manual content, organized by sections for both MCP tool and GUI access.
/// </summary>
public static class HelpManual
{
    public static readonly Dictionary<string, (string Title, string Content)> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = ("What is CodeMerger?", @"CodeMerger is an MCP (Model Context Protocol) server that gives AI assistants like Claude semantic understanding of C# codebases. Instead of reading files and grepping text, Claude gets 30+ specialized tools — call graphs, type introspection, refactoring, XAML parsing — turning multi-step explorations into single precise calls.

It runs as a lightweight WPF application in your Windows system tray, indexes your project using Roslyn syntax trees, and communicates with Claude Desktop via the MCP protocol. It also supports ChatGPT and other LLMs via an SSE (Server-Sent Events) bridge."),

        ["requirements"] = ("Requirements", @"- Windows 10/11 (64-bit)
- .NET 8.0 Runtime (will be prompted to install if missing)
- Claude Desktop (recommended) or any MCP-compatible client
- A C# project — works with WPF, ASP.NET, console apps, class libraries, MAUI, or any .NET project"),

        ["install-claude"] = ("Installing Claude Desktop", @"If you don't have Claude Desktop yet, you need to install it first — CodeMerger connects to Claude through it.

1. Go to claude.ai/download and download the Windows version
2. Run the installer — it's a standard Windows app, installs in seconds
3. Sign in with your Anthropic account (or create one at claude.ai if you don't have one)
4. Claude Desktop works with Free, Pro ($20/month), and Team plans. The Free plan works but has message limits. Pro is recommended for serious coding sessions since you'll be making many tool calls

Important: Claude Desktop is different from claude.ai in the browser. The desktop app is what supports MCP servers like CodeMerger. The browser version cannot connect to local MCP servers."),

        ["install-codemerger"] = ("Installing CodeMerger", @"1. Go to pcarvalho.com/codemerger and click Download
2. The ClickOnce installer will download and run CodeMerger
3. On first launch, CodeMerger places itself in the system tray (notification area near the clock)

Auto-Updates: Every time CodeMerger launches, it checks for updates automatically. If a new version is available, it downloads and installs it before opening. You always run the latest version.

System Tray Behavior:
- CodeMerger runs in the system tray by default
- Double-click the tray icon to open the main window
- Right-click the tray icon for quick options: Open, Exit
- Closing the window minimizes to tray (doesn't exit)
- To fully exit, right-click the tray icon and choose Exit"),

        ["quickstart"] = ("Quick Start (5 Minutes)", @"Step 1: Create or Open a C# Project
If you don't have a project yet, open Visual Studio Community Edition (free) and create any C# project — Console App, WPF App, ASP.NET, whatever you're working on. Note the folder path (e.g., C:\Users\YourName\source\repos\MyProject).

Step 2: Add Your Source Folder
1. Double-click the CodeMerger tray icon to open the main window
2. Click New to create a workspace (give it a name, e.g., ""MyProject"")
3. In the Source Directories tab, click Add and browse to your project's root folder
4. CodeMerger immediately scans and indexes all C# files, XAML files, .csproj files, etc.
5. You'll see the file count and estimated token count in the status bar

Step 3: Claude Desktop Auto-Configuration
CodeMerger automatically configures Claude Desktop to connect to it:
- It writes the MCP server configuration to Claude Desktop's config file
- It adds all 30+ tools to the ""always allow"" list so Claude doesn't ask for permission on every call
- Restart Claude Desktop after the first setup (Claude Desktop needs to restart to pick up new MCP servers)

Step 4: Start Chatting
Open Claude Desktop and ask something about your code:
- ""Give me an overview of this project""
- ""Who calls the SaveData method?""
- ""What does the MainViewModel class look like?""
- ""Rename UserService to AccountService across all files""

Watch the CodeMerger window — you'll see real-time activity as Claude uses the tools!"),

        ["ui"] = ("The User Interface", @"Header Bar:
- Workspace dropdown — switch between projects
- New / Rename / Delete buttons for workspace management
- Connection status — green dot when Claude Desktop is connected, gray when idle
- Version number — shows current CodeMerger version
- Stop Server button — disconnects the MCP server

Tabs:

Source Directories — Configure which folders to index. Add/Remove directories, set extension filters (default: .cs, .xaml, .py, .csproj, .sln, .slnx, .json, .md, .props, .targets), set ignored directories (default: bin, obj, .vs, Properties). Tip: Add your solution's root folder. CodeMerger finds everything inside it.

LLMs — Configure AI assistant connections. Claude Desktop: shows status, Add Config button, Open Config Folder. ChatGPT/Other: SSE server with optional Cloudflare Tunnel for cloud-based LLMs.

Parameters — Fine-tune behavior: backup files (default: on), auto-cleanup, backup retention hours, max backups per file, timeout threshold, session statistics toggle, clean/show backup buttons.

Found Files — Lists all indexed files in the current workspace. Useful for verifying your filters are correct.

Activity Log — Real-time monitoring: live feed of every tool call with timestamps and duration, response time chart, tool breakdown with averages, export and clear options.

Lessons — Self-improvement system: Claude logs lessons it learns, view/delete/submit to community, sync community lessons."),

        ["workspaces"] = ("Workspaces", @"A workspace is a named configuration that includes one or more source directories, file extension and directory filters, per-workspace settings, and project notes.

Creating: Click New in the header bar, enter a name, then add source directories.

Switching: Use the dropdown in the header bar, or Claude can switch programmatically using switch_project. No restart needed — hot-swap between projects mid-conversation.

Multi-Project: You can add multiple source directories to one workspace. For example, a main app + a shared library. CodeMerger indexes both and Claude sees them as one unified codebase. Claude can also merge workspaces on the fly using switch_project with comma-separated names."),

        ["tools-exploration"] = ("Exploration Tools (Read-Only)", @"get_project_overview — Always call this first in a new session. Returns framework type, file count, token count, namespaces, key entry points, and the tool usage guide.

list_files — List all indexed files with namespaces, classifications (View, Model, Service, Controller, Config), and token counts. Filter by namespace or classification.

get_file — Get the full content of a file. For large files (300+ lines), prefer get_lines or get_method_body instead.

get_lines — Get specific line ranges with visible whitespace markers (tabs →, spaces ·). Essential for debugging str_replace failures.

search_code — Semantic search for type names, method names, namespaces. Use this instead of grep when looking for C# symbols.

get_type — Shows a class's complete API: members with full signatures, base types, interfaces. Use this instead of get_file when you just need to understand what a class exposes.

get_dependencies — Shows what a type uses and what uses it. Always call this before modifying any public type, method signature, or property.

find_implementations — Find all concrete classes that implement an interface or extend a base class.

grep — Text/regex search across all file contents with line numbers and context. Only use for: XAML content, string literals, comments, non-C# files.

get_context — Describe your task in plain English, get the most relevant files ranked by relevance with content loaded. One call replaces 3-5 individual file reads.

get_xaml_tree — Parse a XAML file into a compact visual hierarchy with line numbers, element names, x:Name, bindings, and content. Replaces 5-8 grep calls for XAML navigation."),

        ["tools-semantic"] = ("Semantic Tools (Code Intelligence)", @"find_references — Find all references to any symbol (type, method, property, field) across the project. Semantic analysis, not text matching. Use this instead of grep for C# symbols.

get_callers — Get all methods that call a specific method. Essential before modifying method signatures — shows exactly what will break.

get_callees — Get all methods that a specific method calls. Understand a method's dependencies before modifying it.

get_method_body — Get one method's full source code by name. Use instead of get_file when you only need one method — much more efficient."),

        ["tools-editing"] = ("Editing Tools", @"str_replace — Replace a unique string in a file (must appear exactly once). Creates .bak backup. Smart error messages show closest fuzzy match with specific differences. Use normalizeIndent: true to ignore whitespace differences.

write_file — Write complete file content (create or overwrite). Creates .bak backup. Use preview: true to see a diff without writing.

delete_file — Delete a file with .bak backup. Use undo to restore.

grep_replace — Regex find-and-replace across all project files. Always preview first (default). Supports regex capture groups ($1, $2). Optional fileFilter to limit scope. Preview numbers each match; use excludeMatches to skip specific matches, or excludePattern to skip lines matching a regex.

undo — Restore a file from its most recent .bak backup.

replace_lines — Replace a range of lines by line number. Use when you know exact line numbers from get_lines. Simpler than str_replace for line-range edits. Supports preview mode. Creates .bak backup.

move_file — Move or rename a file and update all using statements and references. Always preview first."),

        ["tools-refactoring"] = ("Refactoring Tools", @"rename_symbol — Rename a type, method, property, or field across all files. Always use instead of str_replace for renaming. Call get_dependencies first. Preview before applying.

extract_method — Select a line range and extract it into a new method. Returns modified content for you to write.

add_parameter — Add a parameter to a method and update all call sites with a default value. Preview first.

generate_interface — Generate an interface from a class's public members. Returns code to write to a new file.

implement_interface — Generate stub implementations for all interface members in a class.

generate_constructor — Generate a constructor that initializes selected fields and properties."),

        ["tools-build"] = ("Build & Validation", @"build — Run dotnet build on the project. Returns errors and warnings with source context — shows actual lines of code around each error. quickCheck option: fast Roslyn syntax-only check (~1 second) instead of full build (10-30 seconds). Use quickCheck between individual edits, full build when done."),

        ["tools-project"] = ("Project Management Tools", @"list_projects — Show all available workspaces.

switch_project — Hot-swap to a different workspace without restarting. Pass comma-separated names to merge multiple workspaces.

refresh — Re-analyze all files to update the index. Only needed after external edits (made outside CodeMerger, e.g., in Visual Studio).

shutdown — Shut down the MCP server and release all file locks."),

        ["tools-maintenance"] = ("Maintenance Tools", @"clean_backups — List or delete .bak backup files. Call without confirm to preview, then with confirm: true to delete.

find_duplicates — Analyze the codebase for duplicate or similar code blocks, ranked by impact. Follow up with extract_method to refactor."),

        ["tools-notes"] = ("Project Notes", @"notes — Manage project documentation (stored as CODEMERGER_NOTES.md in your project root):
- get: read current notes (call after get_project_overview to load architecture context)
- add: add a note to a section
- update: replace a section's content
- delete: remove a specific line
- clear: clear a section or all notes

Tip: Use notes to document architecture decisions, conventions, and gotchas. Claude reads them at the start of each session, avoiding rediscovery every time."),

        ["tools-lessons"] = ("Lessons (Self-Improvement)", @"lessons — Claude can log lessons it learns while working on your codebase:
- log: record an observation and proposal for improvement
- get: view all logged lessons
- delete: remove a lesson
- sync: download community lessons
- submit: share a lesson with the community"),

        ["tools-git"] = ("Git Integration", @"git_status — Show modified, staged, and untracked files.

git_commit — Stage all changes and commit with a message. Set push: true to also push to remote.

git_push — Push committed changes to remote."),

        ["workflows"] = ("Recommended Workflows", @"Starting a New Session:
1. Claude calls get_project_overview — gets orientation
2. Claude calls notes get — reads architecture context
3. You describe your task, Claude calls get_context to find relevant files
4. Claude drills into specifics with get_type, get_method_body, get_callers
5. Claude makes changes with str_replace, validates with build

Exploring Unfamiliar Code:
- ""What does this project do?"" → get_project_overview
- ""Show me the main classes"" → list_files filtered by classification
- ""What does UserService expose?"" → get_type typeName='UserService'
- ""Who uses UserService?"" → get_dependencies typeName='UserService'
- ""What does ProcessOrder call?"" → get_callees methodName='ProcessOrder'

Making a Change:
1. Establish a roadmap (list of steps)
2. For each step: get_type or get_method_body → str_replace → build quickCheck=true
3. Final build to confirm everything compiles
4. Review with git_status

Refactoring:
- Rename: get_dependencies → rename_symbol with preview → apply
- Extract method: get_lines to identify range → extract_method
- Add parameter: get_callers to see impact → add_parameter with preview → apply
- Find code to clean up: find_duplicates → extract_method on worst offenders"),

        ["tips"] = ("Tips and Best Practices", @"For Better Results with Claude:

1. Be specific. Instead of ""fix the bug,"" say ""fix the bug where trades aren't saved in TradingBot.ExecuteOrder."" Claude can use get_method_body directly instead of searching.

2. Use project notes. Document your architecture, naming conventions, and gotchas. Claude reads them every session and won't ask you to explain the same thing twice.

3. Ask for roadmaps first. Before a big change, ask Claude to lay out the steps. Then approve and execute one step at a time. This prevents Claude from making too many changes at once.

4. Let Claude use the right tools. If Claude starts grepping for a C# method name, remind it to use find_references or get_callers instead.

For Better Performance:

5. Keep your extensions filter tight. Only index file types you actually need.

6. Use ignored directories. Make sure bin, obj, .vs, and node_modules are ignored.

7. One workspace per logical project. If you have a solution with 3 projects, add all 3 directories to one workspace.

8. Use get_method_body over get_file. For a 500-line file where you only need one method, get_method_body saves tokens.

For Safety:

9. Backups are your friend. Keep backup files enabled (default). Every edit creates a .bak you can restore with undo.

10. Preview before applying. Refactoring tools (rename_symbol, move_file, add_parameter, grep_replace) all support preview: true.

11. Build after changes. Ask Claude to run build after a series of edits. Catch errors before they pile up.

12. Use quickCheck for fast validation. build quickCheck=true does syntax-only checking in ~1 second."),

        ["troubleshooting"] = ("Troubleshooting", @"Claude Desktop Doesn't See CodeMerger's Tools:
- Make sure CodeMerger is running (check the system tray)
- Go to the LLMs tab and click Add Config
- Restart Claude Desktop — it only reads MCP config on startup
- Check the config file manually: LLMs tab → Open Config Folder

Claude Keeps Asking for Permission on Every Tool Call:
- Click Add Config in the LLMs tab to refresh the config (rewrites the always-allow list)
- Restart Claude Desktop

str_replace Keeps Failing:
- The search string must appear exactly once in the file
- Use get_lines to see exact content with visible whitespace markers
- Try normalizeIndent: true to ignore indentation differences
- CodeMerger shows the closest fuzzy match when it fails — use the hint

Files Are Missing from the Index:
- Check the Source Directories tab — is your folder listed?
- Check the Extensions filter — is your file type included?
- Check the Ignored Directories — is your folder being skipped?
- Click Scan to re-scan after changing filters
- Use the Found Files tab to verify what's indexed

Build Fails with ""dotnet not found"":
- Install the .NET 8 SDK from dotnet.microsoft.com
- Make sure dotnet is in your system PATH
- Restart CodeMerger after installing

Changes Made in Visual Studio Aren't Reflected:
- Edits through CodeMerger tools auto-update the index
- For external edits, ask Claude to call refresh to re-index

Connection Drops or Times Out:
- Check that CodeMerger is still running in the system tray
- Adjust the timeout threshold in the Parameters tab
- Click Stop Server and restart CodeMerger if issues persist")
    };

    /// <summary>
    /// Get the full manual as a single string.
    /// </summary>
    public static string GetFullManual()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# CodeMerger User Manual\n");

        foreach (var (key, (title, content)) in Sections)
        {
            sb.AppendLine($"## {title}\n");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Website: pcarvalho.com/codemerger");
        sb.AppendLine("Built by Paulo Carvalho — Computational Proteomics @ Fiocruz / UCSD");
        return sb.ToString();
    }

    /// <summary>
    /// Search sections by topic keyword. Returns matching sections.
    /// </summary>
    public static string SearchByTopic(string topic)
    {
        var topicLower = topic.ToLowerInvariant();
        var matches = new List<(string Key, string Title, string Content)>();

        foreach (var (key, (title, content)) in Sections)
        {
            if (key.Contains(topicLower, StringComparison.OrdinalIgnoreCase) ||
                title.Contains(topicLower, StringComparison.OrdinalIgnoreCase) ||
                content.Contains(topicLower, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((key, title, content));
            }
        }

        if (matches.Count == 0)
        {
            // Return available sections list
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"No sections found matching '{topic}'. Available topics:\n");
            foreach (var (key, (title, _)) in Sections)
                sb.AppendLine($"- **{key}** — {title}");
            sb.AppendLine("\nUse one of these as the topic parameter, or omit topic for the full manual.");
            return sb.ToString();
        }

        var result = new System.Text.StringBuilder();
        if (matches.Count == 1)
        {
            result.AppendLine($"## {matches[0].Title}\n");
            result.AppendLine(matches[0].Content);
        }
        else
        {
            result.AppendLine($"Found {matches.Count} sections matching '{topic}':\n");
            foreach (var (key, title, content) in matches)
            {
                result.AppendLine($"## {title}\n");
                result.AppendLine(content);
                result.AppendLine();
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Get section keys and titles for navigation.
    /// </summary>
    public static List<(string Key, string Title)> GetTableOfContents()
    {
        return Sections.Select(s => (s.Key, s.Value.Title)).ToList();
    }
}
