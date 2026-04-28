<task_type>semantic_rename</task_type>

<instruction>
Perform a project-wide rename of the specified symbol.

STEP 1 — DETECT LANGUAGE: Check the active file extension.
- If `.cs` → use `rename_symbol` (Roslyn, solution-wide atomic rename). Then verify with `list_errors`.
- If `.cpp`, `.h`, `.py`, `.ts`, `.go`, or any other language → `rename_symbol` will NOT work. Proceed to STEP 2.

STEP 2 (non-C# only) — TEXT-BASED RENAME:
1. `grep_search` the old name across the entire workspace root to find every occurrence.
2. For each file with matches: `read_file` to confirm context, then `replace_text` with exact old→new substitution.
3. After all files are updated, `list_errors` to verify no broken references remain.

Never skip files. Never use `rename_symbol` on non-C# projects.
</instruction>

<rename_target>
{codeBlock}
</rename_target>
