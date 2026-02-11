# Project Notes

## Roadmap

- [2026-02-04 21:15] Phase 3 — Connection State Overhaul — Single source of truth for connection state across all UI controls (ConnectionState enum in AppState, all controls subscribe)
Phase 4 — Live Pulse Tray Icon — Icon visually changes per state: gray/green/animated pulse
Phase 5 — Quick Workspace Switcher — Switch workspace from tray context menu without opening window
Phase 6 — Stdio Relay Fallback — Handle MCP child process crashes gracefully with tray notifications
Phase 7 — Polish & Resilience — Port conflicts, graceful shutdown, Explorer restart survival, settings persistence, final review

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
