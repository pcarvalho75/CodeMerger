# Project Notes

## Roadmap

## Connection State + Tray + Polish + Extensions - ALL COMPLETE (Mar 2026)

All phases A-D done. 4 commits: 02d1c25, fe32269, c06fed1, c44951d.

### Phase A: Connection State Overhaul - COMPLETE
AppState single source of truth (4 states: Disconnected, Connected, Restarting, Error). All controls subscribe. McpConnectionService internals private.

### Phase B: Live Pulse Tray Icon - COMPLETE
TrayIconService: colored dot overlay icons, pulse animation (400ms toggle, 2s timeout), context menu wiring, Explorer restart survival.

### Phase C: Polish & Resilience - COMPLETE
Port retry (3 attempts), graceful shutdown (single ordered exit path), TaskbarCreated handling, settings saved on switch+exit.

### Phase D: Extensions & Filters UI - COMPLETE
Tag chip UI replaces TextBox for Extensions and Ignored Directories. WrapPanel of removable pills, Add via TextBox/Enter, 5 presets (C#, Python, Web, Data, Docs), duplicate detection. Workspace.ParseList() deduplicates 6 parsing sites across App, LLMsTab, McpServer, GitService, WorkspaceService, FileScannerService.

### Review findings fixed
GDI handle leak (DestroyIcon P/Invoke), dead code removed (ClaudeActivityFlash, StatusUpdated, CurrentWorkspace, Connecting state), redundant SetClaudeConnected call, Debug.Assert thread guards on setters.

## Tool Overhaul Summary

Completed 2026-02-08. Phases 1-6 and 8 done. Phase 7 (get_xaml_bindings) skipped — not useful without MVVM.

Final state: 37 tools (down from 45). Description tokens reduced ~32% (McpToolRegistry.cs: 15,791 → 10,797).

Key changes:
- Consolidated: get_diagnostics→build(quickCheck), preview_write→write_file(preview), git_commit_push→git_commit(push), 5 lesson tools→1, 5 note tools→1
- Replaced: get_type_hierarchy → find_implementations
- Added: get_method_body
- Enhanced: get_type shows full signatures with param names + [overload] markers
- get_project_overview now includes Tool Guide section
- All stale tool name references cleaned up

## Architecture

- [2026-02-10 23:13] WPF MCP server for Claude Desktop integration. Provides semantic code analysis tools over stdio.
- McpServer: stdio transport, routes JSON-RPC to tool handlers
- McpToolRegistry: all tool definitions and descriptions (single source of truth)
- McpReadToolHandler: get_project_overview, get_file, get_context, list_files, search_code, grep, get_lines, get_method_body
- McpSemanticToolHandler: find_references, get_callers, get_callees, find_implementations, get_type, get_dependencies
- McpRefactoringToolHandler: str_replace, write_file, rename_symbol, move_file, add_parameter, extract_method
- CodeAnalyzer: Roslyn syntax-tree parsing (NOT semantic compilation). Extracts types, members, call sites per file
- SemanticAnalyzer: consumes CodeAnalyzer output. Cross-file analysis via string-based heuristics on call sites
- WorkspaceAnalysis: holds all FileAnalysis objects, the unified project model
- ProjectManager: multi-project support, hot-swap via switch_project
- Dark theme WPF UI with activity monitor panel
- [2026-02-12 18:01] CodeAnalyzer.ExtractMethodCalls now tracks both method invocations AND property/field accesses (reads/writes). Common .NET/LINQ members filtered via IsCommonFrameworkMember(). CallSite.IsPropertyAccess flag distinguishes the two. SemanticAnalyzer.FindUsages labels property accesses as UsageKind.Reference. Property expression bodies, accessor blocks, initializers, field initializers, and constructor expression bodies all extract references. Write tools (str_replace, write_file, etc.) use synchronous re-indexing via UpdateSingleFileSync so find_references sees fresh data immediately after edits.
- [2026-02-16 23:21] ## Recent Changes (Feb 2026)
- Added `codemerger_replace_lines` tool (McpWriteToolHandler) — line-range replacement with preview support
- `grep_replace` now has: numbered matches in preview, `excludeMatches` param to skip specific matches, line-by-line apply (matches preview logic)
- `write_file` warns when new .cs files are created outside any .csproj directory (CheckCsprojProximity in RefactoringService)
- `find_references` shows staleness warning when files indexed before last edit (LastIndexedUtc on FileAnalysis, _lastEditTimestamp on McpServer)
- McpSemanticToolHandler constructor now takes Func&lt;DateTime&gt; getLastEditTimestamp parameter

- [2026-03-09 22:56] ## CompilationService (added Mar 2026)

Phase 1A+1B of Evolution Roadmap. Full CSharpCompilation with SemanticModel for compiler-grade symbol resolution.

- CompilationService.cs: builds manual CSharpCompilation from workspace .cs files + bin/Debug/{tfm}/ DLLs + runtime refs
- Incremental: UpdateFile() replaces single syntax tree without full rebuild
- SemanticAnalyzer enhanced: FindUsages tries semantic path first (exact ISymbol resolution), falls back to heuristic string matching
- Results tagged with ResolutionMode: "semantic" or "heuristic" in SymbolUsageResult
- Wired: McpServer.PerformIndexing → builds compilation after indexing; UpdateSingleFileSync → incremental update; InitializeHandlers → passes to McpSemanticToolHandler → SemanticAnalyzer
- No new NuGet packages needed (uses existing Microsoft.CodeAnalysis.CSharp 5.0.0)

Next: Phase 1D (compilation_status tool), Phase 2 (BlastRadiusAnalyzer), Phase 4 (BuildHealer)

## CompilationService (added Mar 2026)

## CompilationService + Blast Radius (added Mar 2026)

## Evolution Roadmap (Mar 2026)

## Evolution Roadmap — COMPLETE (Mar 2026)

## Evolution Roadmap — COMPLETE (Mar 2026)

All 4 features + safety + custom rules implemented. Tool count: 41 (up from 37).

### New Files (4)

**CompilationService.cs** — Full CSharpCompilation with SemanticModel
- Manual CSharpCompilation from .cs files + bin/Debug/{tfm}/ DLLs + runtime refs
- Incremental UpdateFile() via ReplaceSyntaxTree, RemoveFile()
- APIs: GetSemanticModel, ResolveSymbol, GetExpressionType, FindSymbolByName, FindAllReferences, FindNamespaceForType

**BlastRadiusAnalyzer.cs** — Impact analysis before edits
- BFS call graph traversal at configurable depth, override tracking
- Risk scoring (0-10): fan-out, test gaps, XAML bindings, external deps, critical path keywords

**BuildHealer.cs** — Auto-fix common build errors
- Classifies: CS0246→missing_using, CS0104→ambiguous_ref, CS1002→missing_semicolon
- Fix generators: add using (via CompilationService or 50+ well-known types), fully qualify, add semicolons
- .heal.bak backups before each fix, RollbackFixes restores all on regression, CleanupHealBackups on success/finish

**IntentRefactoringEngine.cs** — Codebase-wide refactoring in one call
- 5 built-in intents: add_xml_doc, add_null_checks, extract_interfaces, enforce_async_naming, add_sealed
- Custom rules from CODEMERGER_RULES.json: report-only (findPattern) or transform (findPattern + replacePattern)
- CustomRuleIntentHandler: regex-based, exclude patterns, file filters, auto-loaded at startup

### Enhanced Files
- SemanticAnalyzer: semantic-first FindUsages with heuristic fallback
- McpServer: CompilationService lifecycle, incremental update, all dispatches
- McpWorkspaceToolHandler: auto-heal loop with .heal.bak backup, rollback on regression, cleanup on success
- McpRefactoringToolHandler: ApplyPattern handler, passes inputDirectories for custom rule loading
- McpSemanticToolHandler: BlastRadius handler
- McpMaintenanceToolHandler: CompilationStatus handler
- McpToolRegistry: all new tools registered

### CODEMERGER_RULES.json format
```json
{ "customIntents": [
  { "name": "rule_name", "description": "...", "keywords": ["..."],
    "findPattern": "regex", "replacePattern": "replacement (optional)",
    "excludePattern": "skip if matches (optional)", "fileFilter": ".cs",
    "message": "violation message" }
] }
```
