using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace LocalPilot.Services
{
    /// <summary>
    /// LocalPilot Nexus: The full-stack dependency graph service.
    /// Hardened with defensive error handling — this service MUST NOT crash the IDE under any circumstances.
    /// </summary>
    public class NexusService : IDisposable
    {
        private static readonly NexusService _instance = new NexusService();
        public static NexusService Instance => _instance;

        private volatile NexusGraph _graph = new NexusGraph();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private string _workspaceRoot = string.Empty;
        
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, byte> _pendingFiles = new ConcurrentDictionary<string, byte>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _workerTask;

        private NexusService() 
        {
            // Start background consumer for debounced incremental updates
            _workerTask = Task.Run(ProcessQueueAsync);
        }

        public async Task InitializeAsync(string workspaceRoot)
        {
            try
            {
                _workspaceRoot = workspaceRoot;
                await LoadFromDiskAsync();
                SetupWatcher(workspaceRoot);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] InitializeAsync failed (non-fatal)", ex);
            }
        }

        /// <summary>
        /// Returns a snapshot of the current graph. Thread-safe via volatile read.
        /// </summary>
        public NexusGraph GetGraph() => _graph;

        private void SetupWatcher(string path)
        {
            try
            {
                if (_watcher != null) { try { _watcher.Dispose(); } catch { } }
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

                _watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    Filter = "*.*",
                    InternalBufferSize = 32768 // 32KB buffer to reduce overflow events
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += (s, e) => { LocalPilotLogger.Log($"[Nexus] FileSystemWatcher error: {e.GetException()?.Message}", LogCategory.Agent); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] Failed to setup FileSystemWatcher (non-fatal)", ex);
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e) { try { QueueFile(e.FullPath); } catch { } }
        private void OnFileDeleted(object sender, FileSystemEventArgs e) { try { QueueFile(e.FullPath, isDeleted: true); } catch { } }
        private void OnFileRenamed(object sender, RenamedEventArgs e) 
        {
            try
            {
                QueueFile(e.OldFullPath, isDeleted: true);
                QueueFile(e.FullPath);
            }
            catch { }
        }

        private void QueueFile(string path, bool isDeleted = false)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".cs" || ext == ".ts" || ext == ".tsx")
                {
                    if (path.Contains("\\obj\\") || path.Contains("\\bin\\") || path.Contains("\\node_modules\\") || path.Contains("\\.git\\")) return;
                    _pendingFiles.TryAdd(path, 0);
                }
            }
            catch { }
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (GlobalPriorityGuard.ShouldYield() || _pendingFiles.IsEmpty)
                    {
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }

                    var filesToProcess = _pendingFiles.Keys.ToList();
                    _pendingFiles.Clear();

                    // Non-blocking lock: skip this batch if a full rebuild is running
                    if (!await _lock.WaitAsync(0))
                    {
                        // Re-queue the files for the next cycle
                        foreach (var f in filesToProcess) _pendingFiles.TryAdd(f, 0);
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }
                    try
                    {
                        LocalPilotLogger.Log($"[Nexus] Performing incremental update for {filesToProcess.Count} files...", LogCategory.Agent);
                        foreach (var file in filesToProcess)
                        {
                            try { UpdateGraphForFile(file); }
                            catch (Exception ex) { LocalPilotLogger.LogError($"[Nexus] Failed to update {Path.GetFileName(file)}", ex); }
                        }
                        PerformBridging(_graph);
                        _graph.LastUpdated = DateTime.Now;
                        await SaveToDiskAsync();
                    }
                    finally { _lock.Release(); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) 
                { 
                    LocalPilotLogger.LogError("[Nexus] Queue processing failed (non-fatal)", ex);
                    try { await Task.Delay(10000, _cts.Token); } catch { break; } // Back off on errors
                }
            }
        }

        /// <summary>
        /// Full rebuild using Parallel.ForEach. Fully defensive.
        /// </summary>
        public async Task RebuildGraphAsync(CancellationToken ct = default)
        {
            // Non-blocking lock: if a rebuild is already running, skip
            if (!await _lock.WaitAsync(0)) return;
            try
            {
                LocalPilotLogger.Log("[Nexus] Rebuilding full-stack dependency graph...", LogCategory.Agent);
                var newGraph = new NexusGraph();
                
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var solution = await VS.Solutions.GetCurrentSolutionAsync();
                    if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                    _workspaceRoot = Path.GetDirectoryName(solution.FullPath);
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[Nexus] Failed to get solution root", ex);
                    return;
                }

                if (string.IsNullOrEmpty(_workspaceRoot) || !Directory.Exists(_workspaceRoot)) return;

                // Switch to background thread for the heavy scan
                await Task.Run(() => 
                {
                    try
                    {
                        List<string> allFiles;
                        try
                        {
                            allFiles = Directory.EnumerateFiles(_workspaceRoot, "*.*", SearchOption.AllDirectories)
                                .Where(f => {
                                    try
                                    {
                                        var ext = Path.GetExtension(f).ToLowerInvariant();
                                        return (ext == ".cs" || ext == ".ts" || ext == ".tsx") &&
                                               !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\");
                                    }
                                    catch { return false; }
                                }).ToList();
                        }
                        catch (Exception ex)
                        {
                            LocalPilotLogger.LogError("[Nexus] File enumeration failed", ex);
                            allFiles = new List<string>();
                        }

                        if (!allFiles.Any()) return;

                        try
                        {
                            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, GlobalPriorityGuard.YieldToken))
                            {
                                Parallel.ForEach(allFiles, new ParallelOptions 
                                { 
                                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), 
                                    CancellationToken = linkedCts.Token 
                                }, file =>
                                {
                                    try { AnalyzeFile(file, newGraph); }
                                    catch (Exception ex) { LocalPilotLogger.LogError($"[Nexus] AnalyzeFile failed for {Path.GetFileName(file)}", ex); }
                                });
                            }
                        }
                        catch (OperationCanceledException) { LocalPilotLogger.Log("[Nexus] Graph rebuild yielded to agent."); }
                        catch (AggregateException ae)
                        {
                            // Log individual failures but don't crash
                            foreach (var inner in ae.InnerExceptions.Take(5))
                            {
                                LocalPilotLogger.LogError("[Nexus] Parallel scan error", inner);
                            }
                        }

                        PerformBridging(newGraph);
                        newGraph.LastUpdated = DateTime.Now;
                        // Atomic swap via volatile write
                        _graph = newGraph;
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError("[Nexus] Background rebuild failed (non-fatal)", ex);
                    }
                });

                await SaveToDiskAsync();
                LocalPilotLogger.Log($"[Nexus] Graph synchronized: {_graph.Nodes.Count} nodes, {_graph.Edges.Count} edges.", LogCategory.Agent);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] RebuildGraphAsync failed catastrophically (non-fatal)", ex);
            }
            finally { _lock.Release(); }
        }

        private void UpdateGraphForFile(string filePath)
        {
            try
            {
                // Remove old nodes and edges associated with this file
                lock (_graph)
                {
                    _graph.Nodes.RemoveAll(n => n.FilePath == filePath);
                    _graph.Edges.RemoveAll(e => e.FromId == filePath || (_graph.Nodes.Any(n => n.Id == e.ToId && n.FilePath == filePath)));
                }

                // Re-analyze if file exists (not deleted)
                if (File.Exists(filePath))
                {
                    AnalyzeFile(filePath, _graph);
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Nexus] UpdateGraphForFile failed for {Path.GetFileName(filePath)}", ex);
            }
        }

        private void AnalyzeFile(string file, NexusGraph graph)
        {
            try
            {
                if (!File.Exists(file)) return;

                // Cap file size to prevent OOM on huge generated files
                var fi = new FileInfo(file);
                if (fi.Length > 1024 * 1024) return; // Skip files > 1MB

                string ext = Path.GetExtension(file).ToLowerInvariant();
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch (IOException) { return; } // File locked
                catch (UnauthorizedAccessException) { return; }

                if (string.IsNullOrWhiteSpace(content)) return;

                if (ext == ".cs") AnalyzeCSharpFile(file, content, graph);
                else if (ext == ".ts" || ext == ".tsx") AnalyzeTypeScriptFile(file, content, graph);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Nexus] AnalyzeFile failed for {Path.GetFileName(file)}", ex);
            }
        }

        private void AnalyzeCSharpFile(string file, string content, NexusGraph graph)
        {
            try
            {
                // Contract Discovery: Detect DTOs and Models for Frontend Bridging
                bool isModel = file.Contains("\\Models\\") || file.Contains("\\Dtos\\") || 
                               Path.GetFileName(file).EndsWith("Dto.cs") || Path.GetFileName(file).EndsWith("Model.cs") ||
                               Path.GetFileName(file).EndsWith("Request.cs") || Path.GetFileName(file).EndsWith("Response.cs");

                if (isModel && !content.Contains(": ControllerBase"))
                {
                    var modelNode = new NexusNode
                    {
                        Id = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        Type = NexusNodeType.DataModel,
                        Language = "cs",
                        FilePath = file
                    };
                    lock (graph) { if (!graph.Nodes.Any(n => n.Id == file)) graph.Nodes.Add(modelNode); }
                }

                if (content.Contains(": ControllerBase") || content.Contains(": Controller") || content.Contains("[ApiController]"))
                {
                    var controllerNode = new NexusNode
                    {
                        Id = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        Type = NexusNodeType.Controller,
                        Language = "cs",
                        FilePath = file
                    };
                    lock (graph) 
                    { 
                        if (!graph.Nodes.Any(n => n.Id == file))
                            graph.Nodes.Add(controllerNode); 
                    }

                    try
                    {
                        var baseRouteMatch = Regex.Match(content, @"\[Route\(""(?<route>[^""]+)""\)\]");
                        string baseRoute = baseRouteMatch.Success ? baseRouteMatch.Groups["route"].Value : "";

                        var actionMatches = Regex.Matches(content, @"\[(?<verb>Http[a-zA-Z]+)(?:\(""(?<route>[^""]*)""\))?\]");
                        foreach (Match m in actionMatches)
                        {
                            string verb = m.Groups["verb"].Value.Replace("Http", "").ToUpper();
                            string fullRoute = CombineRoutes(baseRoute, m.Groups["route"].Value);
                            var endpointNode = new NexusNode
                            {
                                Id = $"api://{verb}/{fullRoute.Trim('/')}",
                                Name = $"{verb} {fullRoute}",
                                Type = NexusNodeType.ApiEndpoint,
                                Language = "contract"
                            };
                            
                            lock (graph)
                            {
                                if (!graph.Nodes.Any(n => n.Id == endpointNode.Id)) graph.Nodes.Add(endpointNode);
                                graph.Edges.Add(new NexusEdge { FromId = endpointNode.Id, ToId = controllerNode.Id, Type = NexusEdgeType.MapsTo });
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException) { } // Defensive: regex can hang on pathological inputs
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Nexus] AnalyzeCSharpFile failed for {Path.GetFileName(file)}", ex);
            }
        }

        private void AnalyzeTypeScriptFile(string file, string content, NexusGraph graph)
        {
            try
            {
                bool isComponent = content.Contains("@Component") || content.Contains("React.Component") || Regex.IsMatch(content, @"function\s+[A-Z]\w*.*return\s+\(");
                bool isService = content.Contains("@Injectable") || file.EndsWith(".service.ts");

                if (isComponent || isService)
                {
                    var node = new NexusNode
                    {
                        Id = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        Type = isComponent ? NexusNodeType.Component : NexusNodeType.FrontendService,
                        Language = Path.GetExtension(file).TrimStart('.'),
                        FilePath = file
                    };
                    lock (graph) 
                    { 
                        if (!graph.Nodes.Any(n => n.Id == file))
                            graph.Nodes.Add(node); 
                    }

                    try
                    {
                        var httpMatches = Regex.Matches(content, @"\.(?<verb>get|post|put|delete|patch)\s*<[^>]*>?\s*\(\s*[`'""'](?<url>[^`'""\s)]+)[`'""]");
                        foreach (Match m in httpMatches)
                        {
                            string verb = m.Groups["verb"].Value.ToUpper();
                            string endpointId = $"api://{verb}/{NormalizeFrontendUrl(m.Groups["url"].Value)}";
                            lock (graph) { graph.Edges.Add(new NexusEdge { FromId = node.Id, ToId = endpointId, Type = NexusEdgeType.Calls, Description = $"Calls {verb} {m.Groups["url"].Value}" }); }
                        }
                    }
                    catch (RegexMatchTimeoutException) { }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Nexus] AnalyzeTypeScriptFile failed for {Path.GetFileName(file)}", ex);
            }
        }

        private void PerformBridging(NexusGraph graph)
        {
            try
            {
                lock (graph)
                {
                    var callEdges = graph.Edges.Where(e => e.ToId != null && e.ToId.StartsWith("api://")).ToList();
                    foreach (var edge in callEdges)
                    {
                        if (!graph.Nodes.Any(n => n.Id == edge.ToId))
                        {
                            graph.Nodes.Add(new NexusNode { Id = edge.ToId, Name = edge.ToId.Replace("api://", "").Replace("/", " "), Type = NexusNodeType.ApiEndpoint, Language = "contract" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] PerformBridging failed (non-fatal)", ex);
            }
        }

        private string CombineRoutes(string @base, string action)
        {
            @base = @base?.Trim('/') ?? "";
            action = action?.Trim('/') ?? "";
            return string.IsNullOrEmpty(@base) ? action : (string.IsNullOrEmpty(action) ? @base : $"{@base}/{action}");
        }

        private string NormalizeFrontendUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                if (url.Contains("://")) try { url = new Uri(url).AbsolutePath; } catch { }
                url = Regex.Replace(url, @"\$\{[^}]+\}", "{id}");
                url = Regex.Replace(url, @":\w+", "{id}");
                return url.Trim('/');
            }
            catch { return url?.Trim('/') ?? ""; }
        }

        private async Task SaveToDiskAsync()
        {
            try 
            { 
                if (string.IsNullOrEmpty(_workspaceRoot)) return;
                string dir = Path.Combine(_workspaceRoot, ".localpilot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Atomic save via temp file to prevent corruption
                string path = Path.Combine(dir, "nexus.json");
                string tempPath = path + ".tmp";
                
                // Snapshot graph under lock to prevent collection-modified during serialization
                string json;
                lock (_graph)
                {
                    json = JsonConvert.SerializeObject(_graph);
                }
                File.WriteAllText(tempPath, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] SaveToDiskAsync failed (non-fatal)", ex);
            }
        }

        private async Task LoadFromDiskAsync()
        {
            try 
            { 
                if (string.IsNullOrEmpty(_workspaceRoot)) return;
                string path = Path.Combine(_workspaceRoot, ".localpilot", "nexus.json");
                if (!File.Exists(path)) return;

                string json = await Task.Run(() => File.ReadAllText(path));
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonConvert.DeserializeObject<NexusGraph>(json);
                if (loaded != null)
                {
                    _graph = loaded;
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] LoadFromDiskAsync failed — starting with empty graph", ex);
                _graph = new NexusGraph();
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _watcher?.Dispose();
            }
            catch { }
        }
    }
}
