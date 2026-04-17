using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Service to scan the workspace and generate a "Project Map" (file list + shallow snippets).
    /// Supports disk-based persistence in the .localpilot directory.
    /// </summary>
    public class ProjectMapService
    {
        private static readonly ProjectMapService _instance = new ProjectMapService();
        public static ProjectMapService Instance => _instance;

        public ProjectMapService()
        {
            // 🚀 SENIOR ARCHITECT PATTERN: Hook into IDE events for Incremental Updates
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Community.VisualStudio.Toolkit.VS.Events.DocumentEvents.Saved += OnDocumentSaved;
            });
        }

        private void OnDocumentSaved(string filePath)
        {
            // Trigger background re-indexing of the saved file
            _ = Task.Run(async () => {
                try {
                    if (!AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())) return;
                    string content = File.ReadAllText(filePath);
                    
                    // Clear old entries for this file before re-indexing
                    lock (_symbolIndex)
                    {
                        foreach (var key in _symbolIndex.Keys.ToList())
                        {
                            _symbolIndex[key].RemoveAll(l => l.FilePath == filePath);
                        }
                    }
                    IndexSymbols(filePath, content);
                    LocalPilotLogger.Log($"[SymbolIndex] Incrementally updated: {Path.GetFileName(filePath)}");
                } catch { /* Silent fail for background indexing */ }
            });
        }
        private static readonly string[] ExcludedDirs = {
            "bin", "obj", ".git", ".vs", "node_modules", "packages", "vendor", "dist", "build", "testresults", "artifacts"
        };

        private static readonly string[] AllowedExtensions = {
            ".cs", ".js", ".ts", ".tsx", ".py", ".go", ".rs", ".cpp", ".h", ".hpp", ".swift", ".java", ".md", ".txt", ".json", ".xml", ".html", ".css", ".csproj", ".sln", ".slnx", ".cshtml", ".vbhtml", ".sh", ".ps1", ".yml", ".yaml"
        };

        private Dictionary<string, List<SymbolLocation>> _symbolIndex = new Dictionary<string, List<SymbolLocation>>(StringComparer.OrdinalIgnoreCase);
        private string _cachedMap = null;
        private string _lastRoot = null;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private DateTime _lastCacheTime = DateTime.MinValue;

        /// <summary>
        /// Generates or retrieves a shallow snapshot of the workspace.
        /// Reads the first ~500 bytes of each relevant text file.
        /// </summary>
        public async Task<string> GenerateProjectMapAsync(string rootPath, int maxBytesPerFile = 500, int maxTotalBytes = 800000)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                return "Project root not found.";

            // 1. Try Loading from Disk first if cache is empty
            if (_cachedMap == null)
            {
                await LoadFromDiskAsync(rootPath);
            }

            // Return cache if it's fresh (within 5 minutes) and same root
            if (_cachedMap != null && _lastRoot == rootPath && (DateTime.Now - _lastCacheTime).TotalMinutes < 5)
            {
                return _cachedMap;
            }

            await _lock.WaitAsync();
            try
            {
                // Double check after lock
                if (_cachedMap != null && _lastRoot == rootPath && (DateTime.Now - _lastCacheTime).TotalMinutes < 5)
                    return _cachedMap;

                LocalPilotLogger.Log($"[ProjectMap] Starting high-speed scan of {rootPath}...");
                
                var sb = new StringBuilder();
                sb.AppendLine("<PROJECT_MAP>");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Workspace: {rootPath}");
                sb.AppendLine("---");

                var directoryInfo = new DirectoryInfo(rootPath);
                var files = GetProjectFiles(directoryInfo).ToList();
                int currentBytes = 0;

                // Process files in small parallel batches for ultra-fast I/O
                var tasks = files.Select(async file =>
                {
                    try
                    {
                        string relativePath = GetRelativePath(rootPath, file.FullName);
                        string head = await ReadFilePrefixAsync(file.FullName, maxBytesPerFile);
                        return (Path: relativePath, Content: head);
                    }
                    catch { return (Path: null, Content: null); }
                });

                var results = await Task.WhenAll(tasks);

                foreach (var res in results)
                {
                    if (res.Path == null) continue;
                    
                    string block = $"\nFILE: {res.Path}\n{res.Content}\n---";
                    byte[] b = Encoding.UTF8.GetBytes(block);
                    
                    if (currentBytes + b.Length > maxTotalBytes)
                    {
                        sb.AppendLine("\n[Snapshot truncated: context limit reached]");
                        break;
                    }

                    sb.Append(block);
                    currentBytes += b.Length;
                }

                sb.AppendLine("\n</PROJECT_MAP>");
                
                _cachedMap = sb.ToString();
                _lastRoot = rootPath;
                _lastCacheTime = DateTime.Now;

                // 2. Persist to disk for smarter loading next session
                await SaveToDiskAsync(rootPath);
                
                LocalPilotLogger.Log($"[ProjectMap] Snapshot complete ({_cachedMap.Length} bytes). Processed {files.Count} files.");
                return _cachedMap;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveToDiskAsync(string root)
        {
            try
            {
                string dir = Path.Combine(root, ".localpilot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                
                string path = Path.Combine(dir, "map.txt");
                await Task.Run(() => File.WriteAllText(path, _cachedMap));
                LocalPilotLogger.Log($"[ProjectMap] Persisted map to {path}");
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[ProjectMap] Save failed", ex); }
        }

        private async Task LoadFromDiskAsync(string root)
        {
            try
            {
                string path = Path.Combine(root, ".localpilot", "map.txt");
                if (File.Exists(path))
                {
                    _cachedMap = await Task.Run(() => File.ReadAllText(path));
                    _lastRoot = root;
                    _lastCacheTime = File.GetLastWriteTime(path);
                    LocalPilotLogger.Log("[ProjectMap] Loaded persistent map from disk");
                }
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[ProjectMap] Load failed", ex); }
        }

        private IEnumerable<FileInfo> GetProjectFiles(DirectoryInfo dir)
        {
            return dir.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !IsExcluded(f.FullName))
                .Where(f => AllowedExtensions.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Extension == ".sln" || f.Extension == ".csproj" ? 0 : 1)
                .Take(400); 
        }

        private bool IsExcluded(string path)
        {
            foreach (var d in ExcludedDirs)
            {
                if (path.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar) || 
                    path.Contains(Path.AltDirectorySeparatorChar + d + Path.AltDirectorySeparatorChar))
                    return true;
            }
            return false;
        }

        public async Task<string> GetLiveContentAsync(string filePath)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var doc = await Community.VisualStudio.Toolkit.VS.Documents.GetDocumentViewAsync(filePath);
                if (doc?.TextBuffer != null)
                {
                    return doc.TextBuffer.CurrentSnapshot.GetText();
                }
            }
            catch { /* Fallback to disk */ }

            return await Task.Run(() => File.ReadAllText(filePath));
        }

        public async Task<Stream> GetLiveStreamAsync(string filePath)
        {
            // 🛡️ MEMORY CEILING FIX: Convert live content to stream
            string content = await GetLiveContentAsync(filePath);
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }

        private async Task<string> ReadFilePrefixAsync(string filePath, int bytesToRead)
        {
            try
            {
                if (IsBinaryFile(filePath)) return "[Binary/Excluded]";

                // 🚀 SENIOR ARCHITECT FIX: Prioritize Live Buffer over Disk
                string content = await GetLiveContentAsync(filePath);
                
                string text = content.Length > bytesToRead ? content.Substring(0, bytesToRead) : content;
                
                // 🚀 POPULATE FAST INDEX
                IndexSymbols(filePath, text);

                if (content.Length > bytesToRead) return text + "... [Truncated]";
                return text;
            }
            catch { return "[Access Denied]"; }
        }

        private bool IsBinaryFile(string filePath)
        {
            try
            {
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
            if (_symbolIndex.TryGetValue(name, out var list)) return list;
            return new List<SymbolLocation>();
        }

        private void IndexSymbols(string filePath, string content)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".cs" && ext != ".js" && ext != ".ts" && ext != ".py") return;

            // 🚀 Lightning Fast Regex Indexer
            // Targets: Classes, Interfaces, Methods, Functions
            var patterns = new[] {
                @"(?:class|interface|struct)\s+(?<name>[a-zA-Z_]\w*)", // Type declarations
                @"(?:public|private|internal|protected|static|async|virtual)?\s+(?:(?<type>[a-zA-Z_][\w\<\>\[\]]*)\s+)?(?<name>[a-zA-Z_]\w*)\s*\((?<args>[^\)]*)\)\s*\{" // Method-like
            };

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(content, pattern);
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

                    lock (_symbolIndex)
                    {
                        if (!_symbolIndex.ContainsKey(name)) _symbolIndex[name] = new List<SymbolLocation>();
                        // Prevent duplicates
                        if (!_symbolIndex[name].Any(l => l.FilePath == filePath && l.Line == loc.Line))
                        {
                            _symbolIndex[name].Add(loc);
                        }
                    }
                }
            }
        }

        private int GetLineNumber(string content, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < content.Length; i++)
            {
                if (content[i] == '\n') line++;
            }
            return line;
        }

        private string GetRelativePath(string rootPath, string fullPath)
        {
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                rootPath += Path.DirectorySeparatorChar;

            return fullPath.Replace(rootPath, "");
        }
    }
}
