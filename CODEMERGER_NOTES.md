# Project Notes

## Workflow Preferences

- [2026-01-31 22:49] ROADMAP FIRST: Always establish a roadmap before coding. For large roadmap steps, create mini-roadmaps within each step. Prioritize roadmap planning over jumping into code. Do one step at a time and wait for confirmation before proceeding.

## Tool Usage Guidelines

- [2026-01-31 22:50] TOOL USAGE PRIORITY - Use the RIGHT tool for the job, not just str_replace for everything:

REFACTORING (use these INSTEAD of manual str_replace when applicable):
- rename_symbol: ANY time a class, method, property, or variable is being renamed. Updates ALL references across the project. Always preview first.
- move_file: ANY time a file is being relocated or renamed. Updates all using statements. Always preview first.
- add_parameter: ANY time adding a parameter to a method. Updates ALL call sites automatically. Always preview first.
- extract_method: When a block of code should become its own method. Use get_lines first to identify the range.
- generate_interface: When creating an interface from an existing class's public members.
- implement_interface: When a class needs stub implementations of an interface.
- generate_constructor: When a class needs a constructor for its fields/properties.

ANALYSIS (use these BEFORE making changes):
- get_dependencies: ALWAYS before renaming, moving, or changing public members. Shows impact scope.
- get_callers / get_callees: Before modifying method signatures or behavior.
- find_references: To see everywhere a symbol is used (semantic, not just text).
- get_context: For task-based exploration - smarter than manual search+grep.
- find_duplicates: Before refactoring to identify consolidation targets.

DIAGNOSTICS:
- get_diagnostics: Quick Roslyn check after edits.
- build: Full MSBuild verification after a series of changes.
