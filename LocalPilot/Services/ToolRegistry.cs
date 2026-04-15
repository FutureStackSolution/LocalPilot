using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Community.VisualStudio.Toolkit;

namespace LocalPilot.Services
{
    /// <summary>
    /// Interface for all agent-runnable tools.
    /// </summary>
    public interface IAgentTool
    {
        string Name { get; }
        string Description { get; }
        string ParameterSchema { get; } // JSON Schema description

        Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct);
    }

    /// <summary>
    /// Registry for all available agent tools.
    /// This service provides safe, workspace-aware interfaces to file and shell actions.
    /// </summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools = new Dictionary<string, IAgentTool>();
        public string WorkspaceRoot { get; set; } = string.Empty;

        public ToolRegistry()
        {
            // Register default tools
            RegisterTool(new ReadFileTool(this));
            RegisterTool(new WriteFileTool(this));
            RegisterTool(new ListDirTool(this));
            RegisterTool(new RunTerminalTool(this));
            RegisterTool(new GrepTool(this));
            RegisterTool(new ReplaceTextTool(this));
            RegisterTool(new ListErrorsTool(this));
            RegisterTool(new DeleteFileTool(this));
        }

        public void RegisterTool(IAgentTool tool)
        {
            _tools[tool.Name] = tool;
        }

        public IEnumerable<IAgentTool> GetAllTools() => _tools.Values;

        public bool HasTool(string name) => _tools.ContainsKey(name);

        public async Task<ToolResponse> ExecuteToolAsync(string name, Dictionary<string, object> args, CancellationToken ct)
        {
            if (!_tools.TryGetValue(name, out var tool))
            {
                return new ToolResponse { IsError = true, Output = $"Tool '{name}' not found." };
            }

            try
            {
                return await tool.ExecuteAsync(args, ct);
            }
            catch (Exception ex)
            {
                return new ToolResponse { IsError = true, Output = $"Execution failed: {ex.Message}" };
            }
        }

        public string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return WorkspaceRoot;

            // 1. Already absolute and exists: use as-is
            if (Path.IsPathRooted(path) && File.Exists(path)) return path;

            // 2. Try relative to workspace root
            string combined = Path.IsPathRooted(path) ? path : Path.Combine(WorkspaceRoot ?? "", path);
            if (File.Exists(combined)) return combined;

            // 3. Fuzzy fallback: search workspace for a file with the same name.
            // Handles the case where the model says "Program.cs" but it's in a subdirectory.
            if (!string.IsNullOrEmpty(WorkspaceRoot) && Directory.Exists(WorkspaceRoot))
            {
                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        var matches = Directory.GetFiles(WorkspaceRoot, fileName, SearchOption.AllDirectories)
                            .Where(f => !f.Contains("\\.git\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                            .ToList();

                        if (matches.Count == 1)
                        {
                            LocalPilotLogger.Log($"[ResolvePath] Fuzzy matched '{path}' -> '{matches[0]}'");
                            return matches[0];
                        }
                        if (matches.Count > 1)
                        {
                            var best = matches.FirstOrDefault(m => m.Replace("\\", "/").Contains(path.Replace("\\", "/")));
                            if (best != null) return best;
                            LocalPilotLogger.Log($"[ResolvePath] Multiple matches for '{fileName}', returning first.");
                            return matches[0];
                        }
                    }
                    catch { }
                }
            }

            // 4. Return combined path — tool will give a clear error if still not found
            return combined;
        }
    }

    // ── Concrete Tool Implementations ──────────────────────────────────────────
    public class ReadFileTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public ReadFileTool(ToolRegistry registry) => _registry = registry;

        public string Name => "read_file";
        public string Description => "Read the full contents of a file at an absolute path.";
        public string ParameterSchema => "{ \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null) 
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            
            try
            {
                // Enterprise Sync: Check if file is open in editor for the 'most fresh' content
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var docView = await VS.Documents.GetDocumentViewAsync(path);
                if (docView?.TextBuffer != null)
                {
                    return new ToolResponse { Output = docView.TextBuffer.CurrentSnapshot.GetText() };
                }

                if (!File.Exists(path)) return new ToolResponse { IsError = true, Output = $"File not found: {path}" };
                var text = File.ReadAllText(path);
                return new ToolResponse { Output = text };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = ex.Message }; }
        }
    }

    public class WriteFileTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public WriteFileTool(ToolRegistry registry) => _registry = registry;

        public string Name => "write_file";
        public string Description => "Writes content to a file. Overwrites if exists. Creates directories if needed. Also ensures the file is part of the Visual Studio project.";
        public string ParameterSchema => "{ \"path\": \"string\", \"content\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };
            
            if (!args.TryGetValue("content", out var contentObj) || contentObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'content' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            var content = contentObj.ToString();

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                bool isNew = !File.Exists(path);

                // Enterprise Sync: If open in editor, write through buffer to allow UNDO and see changes immediately
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var docView = await VS.Documents.GetDocumentViewAsync(path);
                if (docView?.TextBuffer != null)
                {
                    using (var edit = docView.TextBuffer.CreateEdit())
                    {
                        edit.Replace(0, docView.TextBuffer.CurrentSnapshot.Length, content);
                        edit.Apply();
                    }
                }
                else
                {
                    File.WriteAllText(path, content);
                }

                // Modern VS Integration: Add to project if new and in a legacy project model
                if (isNew)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var project = await VS.Solutions.GetActiveProjectAsync();
                    if (project != null)
                    {
                        await project.AddExistingFilesAsync(path);
                    }
                }

                return new ToolResponse { Output = isNew ? "File created and added to project successfully." : "File updated successfully." };
            }
            catch (Exception ex)
            {
                return new ToolResponse { IsError = true, Output = $"FileSystem error: {ex.Message}" };
            }
        }
    }

    public class ListDirTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public ListDirTool(ToolRegistry registry) => _registry = registry;

        public string Name => "list_directory";
        public string Description => "Lists the child files and directories of a path.";
        public string ParameterSchema => "{ \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            if (!Directory.Exists(path)) return new ToolResponse { IsError = true, Output = $"Directory not found: {path}" };

            var entries = Directory.GetFileSystemEntries(path);
            return new ToolResponse { Output = string.Join("\n", entries) };
        }
    }

    public class RunTerminalTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public RunTerminalTool(ToolRegistry registry) => _registry = registry;

        public string Name => "run_terminal";
        public string Description => "Run a shell command on the host system within the workspace.";
        public string ParameterSchema => "{ \"command\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("command", out var cmdObj) || cmdObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'command' argument." };

            var command = cmdObj.ToString();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(_registry.WorkspaceRoot) 
                        ? AppDomain.CurrentDomain.BaseDirectory 
                        : _registry.WorkspaceRoot
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    return new ToolResponse 
                    { 
                        Output = string.IsNullOrEmpty(error) ? output : $"Output:\n{output}\nErrors:\n{error}" 
                    };
                }
            }
            catch (Exception ex)
            {
                return new ToolResponse { IsError = true, Output = $"Process error: {ex.Message}" };
            }
        }
    }

    public class GrepTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public GrepTool(ToolRegistry registry) => _registry = registry;
        public string Name => "grep_search";
        public string Description => "Search for a string pattern in all files within a directory recursively.";
        public string ParameterSchema => "{ \"pattern\": \"string\", \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            var pattern = args["pattern"].ToString();
            var rawPath = args.ContainsKey("path") ? args["path"].ToString() : _registry.WorkspaceRoot;
            var path = _registry.ResolvePath(rawPath);

            try
            {
                var matches = new List<string>();
                var excludedFolders = new[] { ".git", ".vs", "bin", "obj", "node_modules", ".gemini" };

                var files = File.Exists(path) 
                    ? new List<string> { path } 
                    : Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                               .Where(f => !excludedFolders.Any(e => f.Contains(Path.DirectorySeparatorChar + e + Path.DirectorySeparatorChar)))
                               .ToList();

                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        // Buffer Sync: Check if file is open in VS for live content
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var docView = await VS.Documents.GetDocumentViewAsync(file);
                        
                        string[] lines;
                        if (docView?.TextBuffer != null)
                        {
                            lines = docView.TextBuffer.CurrentSnapshot.GetText().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                        }
                        else
                        {
                            lines = File.ReadAllLines(file);
                        }

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                matches.Add($"{file}:{i + 1}: {lines[i].Trim()}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { /* Skip files we can't read */ }
                    catch (IOException) { /* Skip files in use */ }
                }
                return new ToolResponse { Output = matches.Any() ? string.Join("\n", matches) : "No matches found." };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = $"Search failed: {ex.Message}" }; }
        }
    }

    public class ReplaceTextTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public ReplaceTextTool(ToolRegistry registry) => _registry = registry;
        public string Name => "replace_text";
        public string Description => "Replace all occurrences of a string in a specific file.";
        public string ParameterSchema => "{ \"path\": \"string\", \"old_text\": \"string\", \"new_text\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            var path = _registry.ResolvePath(args["path"].ToString());
            var oldText = args["old_text"].ToString();
            var newText = args["new_text"].ToString();

            try
            {
                // CRITICAL: Always read from the VS buffer first (live content),
                // not from disk (stale content). This matches what ReadFileTool does.
                string content = null;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var docView = await VS.Documents.GetDocumentViewAsync(path);
                if (docView?.TextBuffer != null)
                {
                    content = docView.TextBuffer.CurrentSnapshot.GetText();
                    LocalPilotLogger.Log($"[ReplaceText] Reading from VS buffer: {path}");
                }
                else if (File.Exists(path))
                {
                    content = File.ReadAllText(path);
                    LocalPilotLogger.Log($"[ReplaceText] Reading from disk: {path}");
                }
                else
                {
                    return new ToolResponse { IsError = true, Output = $"File not found: {path}" };
                }
                
                // Normalization: LLMs often use \n, Windows uses \r\n.
                // We'll prioritize the exact match, then try normalized line endings.
                if (!content.Contains(oldText))
                {
                    // Try normalizing line endings in both query and content for the search
                    string normalizedOld = oldText.Replace("\r\n", "\n").Trim();
                    string normalizedContent = content.Replace("\r\n", "\n");

                    var index = normalizedContent.IndexOf(normalizedOld, StringComparison.OrdinalIgnoreCase);
                    if (index == -1)
                    {
                        return new ToolResponse { IsError = true, Output = $"Error: Could not find the specified text in '{path}'. Please ensure the 'old_text' matches the file content exactly, including whitespace." };
                    }
                    
                    // We found it! But we need to replace it in the ORIGINAL content with ORIGINAL line endings.
                    // This is tricky. A simpler way: if we can't find exact, but find normalized, let's just 
                    // replace the first block that matches the normalized text.
                    
                    // Direct replacement of normalized text is safer if we also normalize the whole file,
                    // but we don't want to force \n on a Windows project.
                    // For now, let's just use the case-insensitive fallback if the exact match fails.
                    var caseIndex = content.IndexOf(oldText, StringComparison.OrdinalIgnoreCase);
                    if (caseIndex != -1)
                    {
                        var actualOldText = content.Substring(caseIndex, oldText.Length);
                        oldText = actualOldText;
                    }
                    else
                    {
                        return new ToolResponse { IsError = true, Output = $"Error: String '{oldText}' not found. Potential line-ending or whitespace mismatch." };
                    }
                }

                string newContent;
                bool oldIsIdentifier = System.Text.RegularExpressions.Regex.IsMatch(oldText, @"^[A-Za-z_]\w*$");
                bool newIsIdentifier = System.Text.RegularExpressions.Regex.IsMatch(newText, @"^[A-Za-z_]\w*$");

                if (oldIsIdentifier && newIsIdentifier)
                {
                    // Symbol-safe replacement: match full identifier tokens only.
                    // Prevents repeat runs from turning GetIdleTimeJv into GetIdleTimeJvJv.
                    var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(oldText)}\b";
                    newContent = System.Text.RegularExpressions.Regex.Replace(content, pattern, newText);
                }
                else
                {
                    newContent = content.Replace(oldText, newText);
                }

                // Enterprise Sync: If open in editor, write through buffer
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                docView = await VS.Documents.GetDocumentViewAsync(path);
                if (docView?.TextBuffer != null)
                {
                    using (var edit = docView.TextBuffer.CreateEdit())
                    {
                        edit.Replace(0, docView.TextBuffer.CurrentSnapshot.Length, newContent);
                        edit.Apply();
                    }
                }
                else
                {
                    File.WriteAllText(path, newContent);
                }

                return new ToolResponse { Output = $"Successfully updated {path}." };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = ex.Message }; }
        }
    }

    public class ListErrorsTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public ListErrorsTool(ToolRegistry registry) => _registry = registry;
        public string Name => "list_errors";
        public string Description => "Lists all current build errors and warnings from the Visual Studio Error List.";
        public string ParameterSchema => "{}";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                if (dte == null) return new ToolResponse { IsError = true, Output = "DTE2 not available." };

                var sb = new System.Text.StringBuilder();
                var errorList = dte.ToolWindows.ErrorList;
                var items = errorList.ErrorItems;

                for (int i = 1; i <= items.Count; i++)
                {
                    var item = items.Item(i);
                    sb.AppendLine($"[{item.ErrorLevel}] {item.Description} (File: {item.FileName}, Line: {item.Line})");
                }

                return new ToolResponse { Output = items.Count > 0 ? sb.ToString() : "No errors or warnings found." };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = ex.Message }; }
        }
    }

    public class DeleteFileTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public DeleteFileTool(ToolRegistry registry) => _registry = registry;
        public string Name => "delete_file";
        public string Description => "Deletes a file permanently from the file system.";
        public string ParameterSchema => "{ \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());

            try
            {
                // Try the VS-native way first (handles closing Windows and Project sync)
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE.DTE;
                    if (dte?.Solution != null)
                    {
                        var item = dte.Solution.FindProjectItem(path);
                        if (item != null)
                        {
                            item.Delete();
                            return new ToolResponse { Output = $"Successfully deleted {path} (via Project Item)." };
                        }
                    }
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.Log($"[DeleteFile] VS Project deletion failed, falling back to FS: {ex.Message}");
                }

                if (!File.Exists(path))
                {
                    return new ToolResponse { IsError = true, Output = $"File not found: {path}" };
                }

                File.Delete(path);
                return new ToolResponse { Output = $"Successfully deleted {path} (via Filesystem)." };
            }
            catch (Exception ex)
            {
                return new ToolResponse { IsError = true, Output = $"Failed to delete file: {ex.Message}" };
            }
        }
    }
}
