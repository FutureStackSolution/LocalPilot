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
    /// Optimized with Incremental Scanning, Parallel Processing, and Debounced Updates.
    /// </summary>
    public class NexusService : IDisposable
    {
        private static readonly NexusService _instance = new NexusService();
        public static NexusService Instance => _instance;

        private NexusGraph _graph = new NexusGraph();
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
            _workspaceRoot = workspaceRoot;
            await LoadFromDiskAsync();
            SetupWatcher(workspaceRoot);
        }

        public NexusGraph GetGraph() => _graph;

        private void SetupWatcher(string path)
        {
            if (_watcher != null) { _watcher.Dispose(); }
            if (!Directory.Exists(path)) return;

            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = "*.*"
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e) => QueueFile(e.FullPath);
        private void OnFileDeleted(object sender, FileSystemEventArgs e) => QueueFile(e.FullPath, isDeleted: true);
        private void OnFileRenamed(object sender, RenamedEventArgs e) 
        {
            QueueFile(e.OldFullPath, isDeleted: true);
            QueueFile(e.FullPath);
        }

        private void QueueFile(string path, bool isDeleted = false)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".cs" || ext == ".ts" || ext == ".tsx")
            {
                if (path.Contains("\\obj\\") || path.Contains("\\bin\\") || path.Contains("\\node_modules\\")) return;
                
                _pendingFiles.TryAdd(path, 0);
            }
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_pendingFiles.IsEmpty)
                    {
                        await Task.Delay(2000, _cts.Token); // Debounce: Wait 2s for more changes
                        continue;
                    }

                    var filesToProcess = _pendingFiles.Keys.ToList();
                    _pendingFiles.Clear();

                    await _lock.WaitAsync(_cts.Token);
                    try
                    {
                        LocalPilotLogger.Log($"[Nexus] Performing incremental update for {filesToProcess.Count} files...", LogCategory.Agent);
                        foreach (var file in filesToProcess)
                        {
                            UpdateGraphForFile(file);
                        }
                        PerformBridging(_graph);
                        _graph.LastUpdated = DateTime.Now;
                        await SaveToDiskAsync();
                    }
                    finally { _lock.Release(); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { LocalPilotLogger.LogError("[Nexus] Queue processing failed", ex); }
            }
        }

        /// <summary>
        /// 🚀 HIGH PERFORMANCE: Full rebuild using Parallel.ForEach.
        /// </summary>
        public async Task RebuildGraphAsync(CancellationToken ct = default)
        {
            if (!await _lock.WaitAsync(0)) return;
            try
            {
                LocalPilotLogger.Log("[Nexus] Rebuilding full-stack dependency graph...", LogCategory.Agent);
                var newGraph = new NexusGraph();
                
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                _workspaceRoot = Path.GetDirectoryName(solution.FullPath);

                // 🚀 PARALLEL SCAN ENGINE
                var allFiles = Directory.EnumerateFiles(_workspaceRoot, "*.*", SearchOption.AllDirectories)
                    .Where(f => {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return (ext == ".cs" || ext == ".ts" || ext == ".tsx") &&
                               !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\");
                    }).ToList();

                Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, file =>
                {
                    AnalyzeFile(file, newGraph);
                });

                PerformBridging(newGraph);
                _graph = newGraph;
                _graph.LastUpdated = DateTime.Now;

                await SaveToDiskAsync();
                LocalPilotLogger.Log($"[Nexus] Graph synchronized: {_graph.Nodes.Count} nodes, {_graph.Edges.Count} edges.", LogCategory.Agent);
            }
            finally { _lock.Release(); }
        }

        private void UpdateGraphForFile(string filePath)
        {
            // 1. Remove old nodes and edges associated with this file
            _graph.Nodes.RemoveAll(n => n.FilePath == filePath);
            _graph.Edges.RemoveAll(e => e.FromId == filePath || (_graph.Nodes.Any(n => n.Id == e.ToId && n.FilePath == filePath)));

            // 2. Re-analyze if file exists (not deleted)
            if (File.Exists(filePath))
            {
                AnalyzeFile(filePath, _graph);
            }
        }

        private void AnalyzeFile(string file, NexusGraph graph)
        {
            try
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string content = File.ReadAllText(file);

                if (ext == ".cs") AnalyzeCSharpFile(file, content, graph);
                else if (ext == ".ts" || ext == ".tsx") AnalyzeTypeScriptFile(file, content, graph);
            }
            catch { }
        }

        private void AnalyzeCSharpFile(string file, string content, NexusGraph graph)
        {
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
                lock (graph) { graph.Nodes.Add(controllerNode); }

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
                        if (!graph.Nodes.Contains(endpointNode)) graph.Nodes.Add(endpointNode);
                        graph.Edges.Add(new NexusEdge { FromId = endpointNode.Id, ToId = controllerNode.Id, Type = NexusEdgeType.MapsTo });
                    }
                }
            }
        }

        private void AnalyzeTypeScriptFile(string file, string content, NexusGraph graph)
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
                lock (graph) { graph.Nodes.Add(node); }

                var httpMatches = Regex.Matches(content, @"\.(?<verb>get|post|put|delete|patch)\s*<[^>]*>?\s*\(\s*[`'""](?<url>[^`'""\s)]+)[`'""]");
                foreach (Match m in httpMatches)
                {
                    string verb = m.Groups["verb"].Value.ToUpper();
                    string endpointId = $"api://{verb}/{NormalizeFrontendUrl(m.Groups["url"].Value)}";
                    lock (graph) { graph.Edges.Add(new NexusEdge { FromId = node.Id, ToId = endpointId, Type = NexusEdgeType.Calls, Description = $"Calls {verb} {m.Groups["url"].Value}" }); }
                }
            }
        }

        private void PerformBridging(NexusGraph graph)
        {
            lock (graph)
            {
                var callEdges = graph.Edges.Where(e => e.ToId.StartsWith("api://")).ToList();
                foreach (var edge in callEdges)
                {
                    if (!graph.Nodes.Any(n => n.Id == edge.ToId))
                    {
                        graph.Nodes.Add(new NexusNode { Id = edge.ToId, Name = edge.ToId.Replace("api://", "").Replace("/", " "), Type = NexusNodeType.ApiEndpoint, Language = "contract" });
                    }
                }
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
            if (url.Contains("://")) try { url = new Uri(url).AbsolutePath; } catch { }
            url = Regex.Replace(url, @"\$\{[^}]+\}", "{id}");
            url = Regex.Replace(url, @":\w+", "{id}");
            return url.Trim('/');
        }

        private async Task SaveToDiskAsync()
        {
            try { 
                string dir = Path.Combine(_workspaceRoot, ".localpilot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "nexus.json"), JsonConvert.SerializeObject(_graph)); 
            } catch { }
        }

        private async Task LoadFromDiskAsync()
        {
            try { 
                string path = Path.Combine(_workspaceRoot, ".localpilot", "nexus.json");
                if (File.Exists(path)) _graph = JsonConvert.DeserializeObject<NexusGraph>(File.ReadAllText(path)) ?? new NexusGraph();
            } catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _watcher?.Dispose();
        }
    }
}
