using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using LocalPilot.Models;
using EnvDTE;

namespace LocalPilot.Services
{
    /// <summary>
    /// Service to scan the workspace and generate a "Project Map" (file list + shallow snippets).
    /// Hardened with defensive error handling — this service MUST NOT crash the IDE under any circumstances.
    /// </summary>
    public class ProjectMapService
    {
        private static readonly ProjectMapService _instance = new ProjectMapService();
        public static ProjectMapService Instance => _instance;

        private static readonly HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "bin", "obj", ".git", ".vs", "node_modules", "packages", "vendor", "dist", "build", 
            "testresults", "artifacts", ".localpilot", ".angular", "wwwroot", "target", "out",
            ".next", ".nuxt", ".svelte-kit", ".cache", ".vite", ".parcel-cache", "tmp",
            ".eslintcache", "coverage"
        };

        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".cs", ".js", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".py", ".go", ".rs", ".cpp", ".h", ".hpp", ".swift", ".java", ".kt", ".dart", ".rb", ".php", ".sh", ".ps1", ".sql", ".yml", ".yaml", ".json", ".xml", ".md", ".txt", ".tf", ".dockerfile", ".csproj", ".sln", ".slnx"
        };

        private readonly ConcurrentDictionary<string, List<SymbolLocation>> _symbolIndex = new ConcurrentDictionary<string, List<SymbolLocation>>(StringComparer.OrdinalIgnoreCase);
        private string _cachedMap = null;
        private string _lastRoot = null;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private DateTime _lastCacheTime = DateTime.MinValue;
        private DateTime _lastIndexTime = DateTime.MinValue;

        public ProjectMapService()
        {
            // Hook into IDE events for Incremental Updates
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Community.VisualStudio.Toolkit.VS.Events.DocumentEvents.Saved += OnDocumentSaved;
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[ProjectMap] Failed to hook DocumentEvents.Saved", ex);
                }
            });
        }

        private void OnDocumentSaved(string filePath)
        {
            try
            {
                // THROTTLE ENGINE: Prevent "Saving Storm" from pinning the CPU
                if ((DateTime.Now - _lastIndexTime).TotalMilliseconds < 2000) return;
                _lastIndexTime = DateTime.Now;

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                if (!AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())) return;

                // Trigger background re-indexing of the saved file
                _ = Task.Run(async () => {
                    try {
                        string content = File.ReadAllText(filePath);
                        if (content.Length < 10) return; // Skip trivial/empty files

                        // Clear old entries for this file before re-indexing
                        foreach (var key in _symbolIndex.Keys.ToList())
                        {
                            if (_symbolIndex.TryGetValue(key, out var list) && list != null)
                            {
                                lock (list) { list.RemoveAll(l => l.FilePath == filePath); }
                            }
                        }
                        IndexSymbols(filePath, content);
                        LocalPilotLogger.Log($"[SymbolIndex] Incrementally updated: {Path.GetFileName(filePath)}");
                    } catch { /* Silent fail for background indexing */ }
                });
            }
            catch { }
        }

        /// <summary>
        /// Generates or retrieves a shallow snapshot of the workspace.
        /// Fully defensive — will never throw or crash the IDE.
        /// </summary>
        public async Task<string> GenerateProjectMapAsync(string rootPath, int maxBytesPerFile = 250, int maxTotalBytes = 600000)
        {
            try
            {
                if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                    return "Project root not found.";

                // SOLUTION ISOLATION: Clear symbol index if solution changed
                if (_lastRoot != rootPath)
                {
                    LocalPilotLogger.Log($"[ProjectMap] Solution changed. Clearing symbol index for {rootPath}...");
                    _symbolIndex.Clear();
                    _cachedMap = null;
                    _lastRoot = rootPath;
                    _lastCacheTime = DateTime.MinValue;
                }

                // SMART DEBOUNCE: If a map was generated in the last 10 seconds, reuse it 
                if (_cachedMap != null && (DateTime.Now - _lastCacheTime).TotalSeconds < 10)
                    return _cachedMap;

                if (!await _lock.WaitAsync(0)) return _cachedMap ?? "Indexing in progress...";
                try
                {
                    LocalPilotLogger.Log($"[ProjectMap] Generating High-Signal YAML snapshot of {rootPath}...");
                    
                    var openDocContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try {
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var dte = await Community.VisualStudio.Toolkit.VS.GetRequiredServiceAsync<DTE, DTE>();
                        if (dte?.Documents != null)
                        {
                            foreach (Document doc in dte.Documents)
                            {
                                try
                                {
                                    if (doc != null && !string.IsNullOrEmpty(doc.FullName))
                                    {
                                        var view = await Community.VisualStudio.Toolkit.VS.Documents.GetDocumentViewAsync(doc.FullName);
                                        if (view?.TextBuffer != null)
                                        {
                                            openDocContents[doc.FullName] = view.TextBuffer.CurrentSnapshot.GetText();
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    } catch { /* Fail gracefully, we will fallback to disk */ }
                    
                    // 🚀 WORLD-CLASS PERFORMANCE: Parallel Workspace Scan
                    var fileEntries = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var allFiles = SafeEnumerateFiles(new DirectoryInfo(rootPath)).ToList();
                    
                    LocalPilotLogger.Log($"[ProjectMap] Scanning {allFiles.Count} files in parallel...");
                    var scanTasks = allFiles.Select(async file =>
                    {
                        try
                        {
                            string head = await ReadFilePrefixAsync(file.FullName, maxBytesPerFile, openDocContents);
                            if (!string.IsNullOrEmpty(head) && head != "[Binary/Excluded]")
                            {
                                string relativePath = GetRelativePath(rootPath, file.FullName);
                                var sbEntry = new StringBuilder();
                                sbEntry.AppendLine($"- File: {relativePath}");
                                
                                // Grab symbols (now indexed during ReadFilePrefixAsync in parallel)
                                var fileSymbols = _symbolIndex.Values.SelectMany(v => v)
                                    .Where(l => l.FilePath == file.FullName)
                                    .Take(15)
                                    .ToList();
                                
                                if (fileSymbols.Any())
                                {
                                    sbEntry.AppendLine($"  Symbols: [{string.Join(", ", fileSymbols.Select(s => s.Name))}]");
                                }

                                string sanitized = head.Replace("\r", "").Replace("\n", " ").Replace("  ", " ");
                                if (sanitized.Length > 150) sanitized = sanitized.Substring(0, 150) + "...";
                                sbEntry.AppendLine($"  Preview: \"{sanitized.Replace("\"", "'")}\"");
                                
                                fileEntries[file.FullName] = sbEntry.ToString();
                            }
                        }
                        catch { }
                    });

                    await Task.WhenAll(scanTasks);

                    // 3. Assemble the final High-Signal YAML snapshot
                    var sb = new StringBuilder();
                    sb.AppendLine("## WORKSPACE EXECUTIVE SUMMARY (YAML)");
                    sb.AppendLine($"# Generated: {DateTime.Now:O}");
                    sb.AppendLine($"# Root: {rootPath}");
                    
                    var stats = new {
                        Controllers = allFiles.Count(f => f.Name.EndsWith("Controller.cs")),
                        Models = allFiles.Count(f => f.FullName.Contains("\\Models\\") || f.FullName.Contains("\\Dtos\\")),
                        Components = allFiles.Count(f => f.Extension == ".tsx" || f.Extension == ".vue" || f.Extension == ".svelte"),
                        Services = allFiles.Count(f => f.Name.EndsWith("Service.cs")),
                        TotalFiles = allFiles.Count
                    };
                    
                    sb.AppendLine("Architecture:");
                    sb.AppendLine($"  Type: {DetermineProjectType(allFiles)}");
                    sb.AppendLine($"  Scale: {stats.TotalFiles} relevant source files");
                    sb.AppendLine($"  Components: {stats.Components}");
                    sb.AppendLine($"  Controllers: {stats.Controllers}");
                    sb.AppendLine($"  Models: {stats.Models}");
                    sb.AppendLine($"  Services: {stats.Services}");
                    sb.AppendLine("---");

                    foreach (var file in allFiles)
                    {
                        if (fileEntries.TryGetValue(file.FullName, out var entry))
                        {
                            sb.Append(entry);
                            if (sb.Length > maxTotalBytes) 
                            {
                                sb.AppendLine("  # Note: Snapshot truncated due to context window limits.");
                                break;
                            }
                        }
                    }

                    _cachedMap = sb.ToString();
                    _lastRoot = rootPath;
                    _lastCacheTime = DateTime.Now;
                    await SaveToDiskAsync(rootPath);
                    
                    return _cachedMap;
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[ProjectMap] GenerateProjectMapAsync failed catastrophically (non-fatal)", ex);
                return "Failed to generate project map.";
            }
        }

        private IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo dir)
        {
            var files = new List<FileInfo>();
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(dir);

            while (queue.Count > 0 && files.Count < 400)
            {
                var currentDir = queue.Dequeue();
                try
                {
                    if (ExcludedDirs.Contains(currentDir.Name)) continue;

                    foreach (var file in currentDir.GetFiles())
                    {
                        try
                        {
                            if (AllowedExtensions.Contains(file.Extension))
                            {
                                files.Add(file);
                                if (files.Count >= 400) break;
                            }
                        }
                        catch { }
                    }

                    if (files.Count >= 400) break;

                    foreach (var subDir in currentDir.GetDirectories())
                    {
                        try
                        {
                            queue.Enqueue(subDir);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return files.OrderBy(f => f.Extension == ".sln" || f.Extension == ".csproj" ? 0 : 1);
        }

        private async Task SaveToDiskAsync(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return;
                string dir = Path.Combine(root, ".localpilot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                
                string path = Path.Combine(dir, "map.txt");
                string tempPath = path + ".tmp";
                await Task.Run(() => File.WriteAllText(tempPath, _cachedMap));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
            }
            catch { }
        }

        public async Task<string> GetLiveContentAsync(string filePath, Dictionary<string, string> preFetched = null)
        {
            if (string.IsNullOrEmpty(filePath)) return string.Empty;
            if (preFetched != null && preFetched.TryGetValue(filePath, out var content))
                return content;

            try
            {
                if (Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
                {
                    var doc = await Community.VisualStudio.Toolkit.VS.Documents.GetDocumentViewAsync(filePath);
                    if (doc?.TextBuffer != null) return doc.TextBuffer.CurrentSnapshot.GetText();
                }
                else
                {
                    var live = await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                        try
                        {
                            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            var doc = await Community.VisualStudio.Toolkit.VS.Documents.GetDocumentViewAsync(filePath);
                            return doc?.TextBuffer?.CurrentSnapshot?.GetText();
                        }
                        catch { return null; }
                    });
                    if (live != null) return live;
                }
            }
            catch { }

            try
            {
                if (File.Exists(filePath))
                    return await Task.Run(() => File.ReadAllText(filePath));
            }
            catch { }

            return string.Empty;
        }

        private async Task<string> ReadFilePrefixAsync(string filePath, int bytesToRead, Dictionary<string, string> preFetched = null)
        {
            try
            {
                if (!File.Exists(filePath)) return string.Empty;
                if (IsBinaryFile(filePath)) return "[Binary/Excluded]";

                if (preFetched != null && preFetched.TryGetValue(filePath, out var prefetched))
                {
                    string truncated = prefetched.Length > bytesToRead ? prefetched.Substring(0, bytesToRead) : prefetched;
                    IndexSymbols(filePath, truncated);
                    return prefetched.Length > bytesToRead ? truncated + "... [Truncated]" : truncated;
                }

                string text;
                try
                {
                    using (var reader = new StreamReader(filePath))
                    {
                        var buffer = new char[bytesToRead];
                        int read = await reader.ReadAsync(buffer, 0, bytesToRead);
                        text = new string(buffer, 0, read);
                    }
                }
                catch { return "[Access Denied]"; }

                IndexSymbols(filePath, text);

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > bytesToRead) return text + "... [Truncated]";
                return text;
            }
            catch { return "[Access Denied]"; }
        }

        private bool IsBinaryFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var buffer = new byte[512];
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    for (int i = 0; i < bytesRead; i++)
                    {
                        if (buffer[i] == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public IReadOnlyList<SymbolLocation> FindSymbols(string name)
        {
            if (string.IsNullOrEmpty(name)) return new List<SymbolLocation>();
            if (_symbolIndex.TryGetValue(name, out var list) && list != null)
            {
                lock (list) { return list.ToList(); }
            }
            return new List<SymbolLocation>();
        }

        private static readonly Regex[] _symbolPatterns = new[]
        {
            new Regex(@"(?<!\/\/\s*)(?:class|interface|struct|type|trait)\s+(?<name>[a-zA-Z_]\w*)", RegexOptions.Compiled | RegexOptions.ExplicitCapture),
            new Regex(@"(?<!\/\/\s*)@(?:Component|Injectable|Directive|NgModule|Pipe)\s*\(\s*\{", RegexOptions.Compiled | RegexOptions.ExplicitCapture),
            new Regex(@"(?<!\/\/\s*)(?:func|def|function)\s+(?<name>[a-zA-Z_]\w*)\s*\(", RegexOptions.Compiled | RegexOptions.ExplicitCapture),
            new Regex(@"(?<!\/\/\s*)(?:export\s+)?(?:const|let|var)\s+(?<name>[a-zA-Z_]\w*)\s*[:=]\s*(?:\(.*\))\s*=>", RegexOptions.Compiled | RegexOptions.ExplicitCapture),
            new Regex(@"(?<!\/\/\s*)(?:public|private|internal|protected|static|async|virtual)?\s+(?:(?<type>[a-zA-Z_][\w\<\>\[\]]*)\s+)?(?<name>[a-zA-Z_]\w*)\s*\((?<args>[^\)]*)\)\s*\{", RegexOptions.Compiled | RegexOptions.ExplicitCapture)
        };

        private void IndexSymbols(string filePath, string content)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                var supported = new HashSet<string> { ".cs", ".js", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".py", ".go", ".razor", ".cshtml", ".rs", ".rb", ".php", ".java", ".kt", ".sql" };
                if (!supported.Contains(ext)) return;

                foreach (var pattern in _symbolPatterns)
                {
                    var matches = pattern.Matches(content);
                    foreach (Match m in matches)
                    {
                        var name = m.Groups["name"].Value;
                        if (string.IsNullOrEmpty(name) || name == "if" || name == "foreach" || name == "while" || name == "switch") continue;

                        var loc = new SymbolLocation
                        {
                            Name = name,
                            FilePath = filePath,
                            Line = GetLineNumber(content, m.Index),
                            Column = 1,
                            Kind = m.Value.Contains("class") ? "Class" : "Method"
                        };

                        var list = _symbolIndex.GetOrAdd(name, _ => new List<SymbolLocation>());
                        lock (list)
                        {
                            if (!list.Any(l => l.FilePath == filePath && l.Line == loc.Line))
                            {
                                list.Add(loc);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private int GetLineNumber(string content, int index)
        {
            if (string.IsNullOrEmpty(content)) return 1;
            int line = 1;
            for (int i = 0; i < index && i < content.Length; i++)
            {
                if (content[i] == '\n') line++;
            }
            return line;
        }

        private string DetermineProjectType(List<FileInfo> files)
        {
            bool hasCs = files.Any(f => f.Extension == ".cs");
            bool hasWeb = files.Any(f => f.Extension == ".tsx" || f.Extension == ".vue" || f.Extension == ".svelte" || f.Extension == ".html");
            bool hasSql = files.Any(f => f.Extension == ".sql");

            if (hasCs && hasWeb) return "Full-Stack (C#/.NET + Web)";
            if (hasCs) return "Backend (C#/.NET)";
            if (hasWeb) return "Frontend (Web/JS Framework)";
            if (hasSql) return "Database Project";
            return "General Codebase";
        }

        private string GetRelativePath(string rootPath, string fullPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(fullPath)) return fullPath;
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                rootPath += Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(rootPath.Length);
            
            return fullPath;
        }
    }
}
