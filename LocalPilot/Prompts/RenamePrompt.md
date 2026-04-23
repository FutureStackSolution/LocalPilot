<task_type>semantic_rename</task_type>

<instruction>
Perform a project-wide semantic rename of the specified symbol.
1. NATIVE TOOLS: Always prioritize `rename_symbol` to update the definition and all references atomically.
2. VERIFICATION: After renaming, check for "missing member" errors using `list_errors`.
3. FALLBACK: If `rename_symbol` fails, use `grep_search` and `replace_text` to manually update references, ensuring exact matches.
Maintain the project's indentation and file formatting exactly.
</instruction>

<rename_target>
{codeBlock}
</rename_target>
