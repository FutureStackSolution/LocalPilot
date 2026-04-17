# INSTRUCTION
Perform a project-wide semantic rename of the specified symbol.
1. Use `rename_symbol` to update the definition and all references atomically.
2. If `rename_symbol` fails, analyze the failure and perform a manual refactor across relevant files using `grep_search` and `replace_file_content`.
3. Ensure the new name follows the project's naming conventions and maintains semantic clarity.

# TARGET
{codeBlock}
