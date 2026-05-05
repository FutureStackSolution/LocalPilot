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
            RegisterTool(new RenameSymbolTool(this));
            RegisterTool(new RunTestsTool(this));
            RegisterTool(new TraceDependencyTool(this));
            RegisterTool(new AnalyzeImpactTool(this));
        }

        public void RegisterTool(IAgentTool tool)
        {
            _tools[tool.Name] = tool;
        }

        public IEnumerable<IAgentTool> GetAllTools() => _tools.Values;

        public bool HasTool(string name) => _tools.ContainsKey(name);

        /// <summary>
        /// Generates tool definitions in Ollama's native format for the /api/chat tools parameter.
        /// This enables structured tool calling — Ollama returns tool_calls as JSON objects,
        /// not embedded in text, eliminating all parsing/nudging issues.
        /// </summary>
        public List<OllamaToolDefinition> GetOllamaToolDefinitions()
        {
            var definitions = new List<OllamaToolDefinition>();
            
            foreach (var tool in _tools.Values)
            {
                var def = new OllamaToolDefinition
                {
                    Type = "function",
                    Function = new OllamaFunctionDefinition
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Parameters = ParseParameterSchema(tool.ParameterSchema)
                    }
                };
                definitions.Add(def);
            }

            return definitions;
        }

        /// <summary>
        /// Parses our simple parameter schema strings (e.g. '{ "path": "string" }') 
        /// into Ollama's JSON Schema format.
        /// </summary>
        private OllamaParameterDefinition ParseParameterSchema(string schema)
        {
            var paramDef = new OllamaParameterDefinition
            {
                Type = "object",
                Properties = new Dictionary<string, OllamaPropertyDefinition>(),
                Required = new List<string>()
            };

            if (string.IsNullOrEmpty(schema) || schema == "{}") return paramDef;

            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(schema);
                foreach (var prop in obj.Properties())
                {
                    string val = prop.Value?.ToString() ?? "string";
                    bool isOptional = val.Contains("(optional)");
                    string propType = val.Replace("(optional)", "").Trim();

                    paramDef.Properties[prop.Name] = new OllamaPropertyDefinition
                    {
                        Type = propType == "integer" ? "integer" : "string",
                        Description = $"The {prop.Name} parameter"
                    };
                    
                    if (!isOptional)
                    {
                        paramDef.Required.Add(prop.Name);
                    }
                }
            }
            catch
            {
                LocalPilotLogger.Log($"[ToolRegistry] Failed to parse parameter schema: {schema}");
            }

            return paramDef;
        }

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

            // 🚀 SANITIZATION: LLMs sometimes wrap paths in quotes or backticks
            path = path.Trim('\"', '\'', '`', ' ', '\t', '\n', '\r');

            // 🛡️ SECURITY GUARD: Block access to internal metadata
            if (IsInternalMetadata(path))
            {
                throw new UnauthorizedAccessException($"Access to internal metadata directory '.localpilot' is restricted. Please target source files in the project instead.");
            }

            try
            {
                // 1. Already absolute and exists: use as-is
                if (Path.IsPathRooted(path) && File.Exists(path)) return path;

                // 2. Try relative to workspace root
                string combined = Path.IsPathRooted(path) ? path : Path.Combine(WorkspaceRoot ?? "", path);
                if (File.Exists(combined)) return combined;

                // 3. Fuzzy fallback: search workspace for a file with the same name.
                if (!string.IsNullOrEmpty(WorkspaceRoot) && Directory.Exists(WorkspaceRoot))
                {
                    string fileName = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        try
                        {
                            var matches = Directory.GetFiles(WorkspaceRoot, fileName, SearchOption.AllDirectories)
                                .Where(f => !f.Contains("\\.git\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.localpilot\\"))
                                .ToList();

                            if (matches.Count == 1)
                            {
                                LocalPilotLogger.Log($"[ResolvePath] Fuzzy matched '{path}' -> '{matches[0]}'");
                                return matches[0];
                            }
                        }
                        catch { }
                    }
                }

                return combined;
            }
            catch (ArgumentException)
            {
                // This happens if the path contains illegal characters.
                // We'll return the input as-is and let the downstream tool handle the "File Not Found" gracefully
                // rather than crashing the whole agent loop.
                return path;
            }
        }

        private bool IsInternalMetadata(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var pathLower = path.ToLowerInvariant();
            return pathLower.Contains(Path.DirectorySeparatorChar + ".localpilot") || 
                   pathLower.Contains(Path.AltDirectorySeparatorChar + ".localpilot") ||
                   pathLower.StartsWith(".localpilot");
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
        public string Description => "Writes content to a file. By default, fails if file exists unless 'overwrite' is true. Creates directories if needed. Also ensures the file is part of the Visual Studio project.";
        public string ParameterSchema => "{ \"path\": \"string\", \"content\": \"string\", \"overwrite\": \"boolean\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };
            
            if (!args.TryGetValue("content", out var contentObj) || contentObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'content' argument." };

            bool overwrite = false;
            if (args.TryGetValue("overwrite", out var overwriteObj) && overwriteObj != null)
            {
                if (overwriteObj is bool b) overwrite = b;
                else if (bool.TryParse(overwriteObj.ToString(), out bool pb)) overwrite = pb;
            }

            var path = _registry.ResolvePath(pathObj.ToString());
            var content = contentObj.ToString();

            try
            {
                bool exists = File.Exists(path);
                if (exists && !overwrite)
                {
                    return new ToolResponse { IsError = true, Output = $"Error: File already exists at '{path}'. If you intend to overwrite it, set 'overwrite': true in your tool call." };
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                bool isNew = !exists;

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

                return new ToolResponse { Output = isNew ? "File created and added to project successfully." : "File updated successfully (overwritten)." };
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

            var entries = Directory.GetFileSystemEntries(path)
                .Where(e => !e.Contains(Path.DirectorySeparatorChar + ".localpilot") && !e.EndsWith(".localpilot"))
                .ToList();
            return new ToolResponse { Output = string.Join("\n", entries) };
        }
    }

    public class RunTerminalTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public RunTerminalTool(ToolRegistry registry) => _registry = registry;

        public string Name => "run_terminal";
        public string Description => "Run a shell command on the host system within the workspace using cmd.exe. Use for non-interactive commands only (e.g., dotnet build, git status).";
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
                    // Read stdout and stderr concurrently to avoid pipe deadlock
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    // 300-second timeout to prevent hung processes from blocking the agent
                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(300));
                        var completedTask = await Task.WhenAny(
                            Task.WhenAll(outputTask, errorTask),
                            Task.Delay(Timeout.Infinite, timeoutCts.Token)
                        );
                    }

                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch { }
                        return new ToolResponse { IsError = true, Output = $"Command timed out after 300 seconds and was killed." };
                    }

                    string output = await outputTask;
                    string error = await errorTask;

                    return new ToolResponse 
                    { 
                        Output = string.IsNullOrEmpty(error) ? output : $"Output:\n{output}\nErrors:\n{error}" 
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return new ToolResponse { IsError = true, Output = "Command was cancelled." };
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
            if (!args.TryGetValue("pattern", out var patternObj) || patternObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'pattern' argument." };

            var pattern = patternObj.ToString();
            var rawPath = args.ContainsKey("path") ? args["path"].ToString() : _registry.WorkspaceRoot;
            var path = _registry.ResolvePath(rawPath);

            try
            {
                var matches = new List<string>();
                var excludedFolders = new[] { ".git", ".vs", "bin", "obj", "node_modules", ".gemini", ".localpilot" };

                // Optimization: Pre-enumerate files to avoid UI switches inside loop
                var fileList = File.Exists(path) 
                    ? new List<string> { path } 
                    : Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                               .Where(f => !excludedFolders.Any(e => f.Contains(Path.DirectorySeparatorChar + e + Path.DirectorySeparatorChar)))
                               .ToList();

                // Cap results to prevent unbounded memory usage
                const int MaxMatches = 200;
                const long MaxFileSizeBytes = 2 * 1024 * 1024; // 2MB cap per file

                Parallel.ForEach(fileList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, file =>
                {
                    if (ct.IsCancellationRequested || matches.Count >= MaxMatches) return;
                    try
                    {
                        // Skip very large files to avoid OOM
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length > MaxFileSizeBytes) return;

                        var lines = File.ReadAllLines(file);

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                lock (matches)
                                {
                                    if (matches.Count >= MaxMatches) return;
                                    matches.Add($"{file}:{i + 1}: {lines[i].Trim()}");
                                }
                            }
                        }
                    }
                    catch { }
                });

                if (matches.Any())
                {
                    return new ToolResponse { Output = string.Join("\n", matches) };
                }

                return new ToolResponse { Output = "No matches found. Try using find_definitions if searching for a specific class or method." };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = $"Search failed: {ex.Message}" }; }
        }
    }

    public class ReplaceTextTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public ReplaceTextTool(ToolRegistry registry) => _registry = registry;
        public string Name => "replace_text";
        public string Description => "Replace a specific block of text in a file. The 'path' MUST be a specific FILE (e.g., Program.cs), not a directory. The 'old_text' MUST match the file content EXACTLY.";
        public string ParameterSchema => "{ \"path\": \"string\", \"old_text\": \"string\", \"new_text\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };
            if (!args.TryGetValue("old_text", out var oldObj) || oldObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'old_text' argument." };
            if (!args.TryGetValue("new_text", out var newObj) || newObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'new_text' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            var oldText = oldObj.ToString();
            var newText = newObj.ToString();

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
                
                // Normalization: LLMs often use \n, Windows uses \r\n, and sometimes have trailing spaces.
                // We'll prioritize the exact match, then try fuzzy whitespace normalization.
                if (!content.Contains(oldText))
                {
                    // 🚀 FUZZY FALLBACK (v2.0): Match ignoring whitespace variations
                    string escapedOld = System.Text.RegularExpressions.Regex.Escape(oldText);
                    string pattern = System.Text.RegularExpressions.Regex.Replace(escapedOld, @"\s+", @"\s+");
                    var match = System.Text.RegularExpressions.Regex.Match(content, pattern);

                    if (match.Success)
                    {
                        oldText = match.Value;
                        LocalPilotLogger.Log($"[ReplaceText] Fuzzy match found for '{path}' using regex fallback.");
                    }
                    else
                    {
                        // Final attempt: Case-insensitive exact match
                        var caseIndex = content.IndexOf(oldText, StringComparison.OrdinalIgnoreCase);
                        if (caseIndex != -1)
                        {
                            oldText = content.Substring(caseIndex, oldText.Length);
                        }
                        else
                        {
                            return new ToolResponse { IsError = true, Output = $"Error: Could not find the specified text in '{path}'. Please ensure the 'old_text' matches the file content exactly, including whitespace." };
                        }
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

                // 🛡️ WORKSPACE SYNC: Ensure Roslyn sees this non-native edit
                var roslyn = SymbolIndexService.Instance.GetRoslynProvider();
                if (roslyn != null) await roslyn.SynchronizeDocumentAsync(path);

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
                var output = await SymbolIndexService.Instance.GetDiagnosticsAsync(ct);
                return new ToolResponse { Output = output ?? "No errors found." };
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
                    var dte = Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE.DTE;
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

    public class RenameSymbolTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public RenameSymbolTool(ToolRegistry registry) => _registry = registry;
        public string Name => "rename_symbol";
        public string Description => "Renames a symbol project-wide using Roslyn. The 'path' MUST be a specific file. YOU MUST RUN 'read_file' FIRST to get the current correct line and column numbers before calling this tool.";
        public string ParameterSchema => "{ \"path\": \"string\", \"line\": \"integer\", \"column\": \"integer\", \"new_name\": \"string\", \"old_name\": \"string (optional)\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };
            if (!args.TryGetValue("line", out var lineObj) || lineObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'line' argument." };
            if (!args.TryGetValue("column", out var colObj) || colObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'column' argument." };
            if (!args.TryGetValue("new_name", out var nameObj) || nameObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'new_name' argument." };

            string oldName = args.TryGetValue("old_name", out var oldObj) ? oldObj?.ToString() : null;

            var path = _registry.ResolvePath(pathObj.ToString());
            var line = Convert.ToInt32(lineObj);
            var col = Convert.ToInt32(colObj);
            var newName = nameObj.ToString();

            try
            {
                // 🚀 SEMANTIC WARM-UP: Ensure the file is 'known' to the VS workspace cache before refactoring
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.Documents.OpenAsync(path);

                // 🚀 NATIVE LSP REFACTORING (Roslyn)
                // This replaces the unreliable DTE UI-based rename with project-wide semantic renaming.
                var result = await SymbolIndexService.Instance.RenameSymbolAsync(path, line, col, newName, oldName, ct);
                
                if (result.StartsWith("Error") || result.Contains("failed"))
                {
                    return new ToolResponse { IsError = true, Output = result };
                }
                
                return new ToolResponse { Output = result };
            }
            catch (Exception ex) { return new ToolResponse { IsError = true, Output = $"Rename failed: {ex.Message}" }; }
        }
    }

    public class RunTestsTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public RunTestsTool(ToolRegistry registry) => _registry = registry;
        public string Name => "run_tests";
        public string Description => "Runs all unit tests in the solution using 'dotnet test' and returns the pass/fail results. Use this to verify that your code changes didn't break anything.";
        public string ParameterSchema => "{ }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            var root = _registry.WorkspaceRoot;
            if (string.IsNullOrEmpty(root)) return new ToolResponse { IsError = true, Output = "Workspace root not found." };

            string cmd = "dotnet";
            string argStr = "test --label --verbosity quiet";

            // 🧠 AUTO-DETECT TOOLCHAIN
            if (File.Exists(Path.Combine(root, "package.json")))
            {
                cmd = "npm.cmd"; // Windows specific
                argStr = "test";
            }
            else if (File.Exists(Path.Combine(root, "go.mod")))
            {
                cmd = "go";
                argStr = "test ./...";
            }
            else if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")))
            {
                cmd = "pytest";
                argStr = "";
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = argStr,
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = System.Diagnostics.Process.Start(startInfo))
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    string error = await proc.StandardError.ReadToEndAsync();
                    
                    var waitTask = Task.Run(() => proc.WaitForExit(300000)); // 300s timeout
                    await waitTask;

                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        return new ToolResponse { IsError = true, Output = $"{cmd} timed out after 300 seconds." };
                    }

                    if (proc.ExitCode != 0)
                    {
                        return new ToolResponse { IsError = true, Output = $"Tests failed ({cmd} Exit Code {proc.ExitCode}):\n{output}\n{error}" };
                    }
                    return new ToolResponse { Output = $"Tests passed successfully using {cmd}!\n{output}" };
                }
            }
            catch (Exception ex)
            {
                return new ToolResponse { IsError = true, Output = $"Failed to execute {cmd}: {ex.Message}. Ensure the tool is installed in your PATH." };
            }
        }
    }
    public class TraceDependencyTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public TraceDependencyTool(ToolRegistry registry) => _registry = registry;
        public string Name => "trace_dependency";
        public string Description => "Finds end-to-end dependencies for a file (e.g., UI Component -> API Endpoint -> .NET Controller). Use this for cross-language navigation.";
        public string ParameterSchema => "{ \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            var graph = NexusService.Instance.GetGraph();
            
            var node = graph.Nodes.FirstOrDefault(n => n.FilePath != null && n.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (node == null) return new ToolResponse { Output = "No dependency mapping found for this file in the Nexus graph." };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Dependencies for {node.Name} ({node.Type}):");

            // Outgoing edges
            var outgoing = graph.Edges.Where(e => e.FromId == node.Id).ToList();
            if (outgoing.Any())
            {
                sb.AppendLine("\nDirect dependencies (Calls/DependsOn):");
                foreach (var edge in outgoing)
                {
                    var target = graph.Nodes.FirstOrDefault(n => n.Id == edge.ToId);
                    sb.AppendLine($" - {target?.Name ?? edge.ToId} ({target?.Type.ToString() ?? "Unknown"}) {(string.IsNullOrEmpty(edge.Description) ? "" : ": " + edge.Description)}");
                    
                    // Trace one more level (e.g. Endpoint -> Controller)
                    var subEdges = graph.Edges.Where(e => e.FromId == edge.ToId).ToList();
                    foreach (var sub in subEdges)
                    {
                        var subTarget = graph.Nodes.FirstOrDefault(n => n.Id == sub.ToId);
                        sb.AppendLine($"   └─ {subTarget?.Name ?? sub.ToId} ({subTarget?.Type.ToString() ?? "Unknown"})");
                    }
                }
            }

            // Incoming edges
            var incoming = graph.Edges.Where(e => e.ToId == node.Id).ToList();
            if (incoming.Any())
            {
                sb.AppendLine("\nDepended on by (Consumed by):");
                foreach (var edge in incoming)
                {
                    var source = graph.Nodes.FirstOrDefault(n => n.Id == edge.FromId);
                    sb.AppendLine($" - {source?.Name ?? edge.FromId} ({source?.Type.ToString() ?? "Unknown"})");
                }
            }

            return new ToolResponse { Output = sb.ToString() };
        }
    }

    public class AnalyzeImpactTool : IAgentTool
    {
        private readonly ToolRegistry _registry;
        public AnalyzeImpactTool(ToolRegistry registry) => _registry = registry;
        public string Name => "analyze_impact";
        public string Description => "Calculates the 'blast radius' of a change to a file. Shows all affected components and services across the entire stack.";
        public string ParameterSchema => "{ \"path\": \"string\" }";

        public async Task<ToolResponse> ExecuteAsync(Dictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return new ToolResponse { IsError = true, Output = "Missing 'path' argument." };

            var path = _registry.ResolvePath(pathObj.ToString());
            var graph = NexusService.Instance.GetGraph();
            
            var node = graph.Nodes.FirstOrDefault(n => n.FilePath != null && n.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (node == null) return new ToolResponse { Output = "No impact mapping found for this file." };

            var affected = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(node.Id);

            // Simple BFS to find all upstream dependants
            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                var dependants = graph.Edges.Where(e => e.ToId == currentId).Select(e => e.FromId);
                foreach (var depId in dependants)
                {
                    if (affected.Add(depId))
                    {
                        queue.Enqueue(depId);
                    }
                }
            }

            if (affected.Count == 0) return new ToolResponse { Output = "Low Impact: No direct upstream dependants found for this file." };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"CRITICAL IMPACT ANALYSIS for {node.Name}:");
            sb.AppendLine($"Total Affected Components/Services: {affected.Count}");
            
            foreach (var id in affected)
            {
                var n = graph.Nodes.FirstOrDefault(nodeObj => nodeObj.Id == id);
                sb.AppendLine($" - [{(n?.Language ?? "??")}] {n?.Name ?? id} ({n?.Type.ToString() ?? "Unknown"})");
            }

            return new ToolResponse { Output = sb.ToString() };
        }
    }
}
