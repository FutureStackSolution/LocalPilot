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
using Microsoft.Data.Sqlite;

namespace LocalPilot.Services
{
    /// <summary>
    /// LocalPilot Nexus: The full-stack dependency graph service.
    /// UPGRADED (v4.0): Uses SQLite Storage Engine for persistent graph storage.
    /// Eliminates JSON serialization stutters and allows for massive graph scale.
    /// </summary>
    public class NexusService : IDisposable
    {
        private static readonly NexusService _instance = new NexusService();
        public static NexusService Instance => _instance;

        private readonly StorageService _storage = StorageService.Instance;
        private volatile NexusGraph _graph = new NexusGraph();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private string _workspaceRoot = string.Empty;
        
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, byte> _pendingFiles = new ConcurrentDictionary<string, byte>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _workerTask;

        private NexusService() 
        {
            _workerTask = Task.Run(ProcessQueueAsync);
        }

        public async Task InitializeAsync(string workspaceRoot)
        {
            try
            {
                _workspaceRoot = workspaceRoot;
                await TryMigrateLegacyNexusAsync(workspaceRoot);
                await RefreshGraphFromDbAsync();
                SetupWatcher(workspaceRoot);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Nexus] InitializeAsync failed", ex);
            }
        }

        public NexusGraph GetGraph() => _graph;

        private async Task RefreshGraphFromDbAsync()
        {
            var newGraph = new NexusGraph();
            await _storage.GetLock().WaitAsync().ConfigureAwait(false);
            try
            {
                var connection = _storage.GetConnection();
                if (connection == null) return;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Id, Label, Type, Metadata FROM NexusNodes";
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            newGraph.Nodes.Add(new NexusNode {
                                Id = reader.GetString(0),
                                Name = reader.GetString(1),
                                Type = (NexusNodeType)Enum.Parse(typeof(NexusNodeType), reader.GetString(2)),
                                FilePath = reader.GetString(3) // Using Metadata column for FilePath
                            });
                        }
                    }

                    cmd.CommandText = "SELECT SourceId, TargetId, Type FROM NexusEdges";
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            newGraph.Edges.Add(new NexusEdge {
                                FromId = reader.GetString(0),
                                ToId = reader.GetString(1),
                                Type = (NexusEdgeType)Enum.Parse(typeof(NexusEdgeType), reader.GetString(2))
                            });
                        }
                    }
                }
                _graph = newGraph;
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[Nexus] RefreshGraphFromDbAsync failed", ex); }
            finally { _storage.GetLock().Release(); }
        }

        private void SetupWatcher(string path)
        {
            try
            {
                if (_watcher != null) { try { _watcher.Dispose(); } catch { } }
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                _watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, Filter = "*.*", EnableRaisingEvents = true };
                _watcher.Changed += (s, e) => QueueFile(e.FullPath);
                _watcher.Created += (s, e) => QueueFile(e.FullPath);
                _watcher.Deleted += (s, e) => QueueFile(e.FullPath, true);
            }
            catch { }
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
                    await Task.Delay(5000, _cts.Token);
                    if (_pendingFiles.IsEmpty) continue;

                    var files = _pendingFiles.Keys.ToList();
                    _pendingFiles.Clear();

                    if (!await _lock.WaitAsync(0)) continue;
                    try
                    {
                        foreach (var file in files) await UpdateGraphForFileAsync(file);
                        await RefreshGraphFromDbAsync();
                    }
                    finally { _lock.Release(); }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(5000); }
            }
        }

        public async Task RebuildGraphAsync(CancellationToken ct = default)
        {
            if (!await _lock.WaitAsync(0)) return;
            try
            {
                LocalPilotLogger.Log("[Nexus] Rebuilding persistent dependency graph...");
                
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                _workspaceRoot = Path.GetDirectoryName(solution.FullPath);

                // Switch to background thread for the heavy scan
                await Task.Run(async () => 
                {
                    var allFiles = SafeEnumerateFiles(new DirectoryInfo(_workspaceRoot)).ToList();

                    // Clear tables for rebuild
                    await _storage.ExecuteAsync("DELETE FROM NexusNodes");
                    await _storage.ExecuteAsync("DELETE FROM NexusEdges");

                    // Process in batches to avoid overwhelming the system
                    int batchSize = 10;
                    for (int i = 0; i < allFiles.Count; i += batchSize)
                    {
                        var batch = allFiles.Skip(i).Take(batchSize);
                        await Task.WhenAll(batch.Select(f => UpdateGraphForFileAsync(f)));
                    }
                });

                await RefreshGraphFromDbAsync();
                LocalPilotLogger.Log($"[Nexus] Sync complete: {_graph.Nodes.Count} nodes.");
            }
            finally { _lock.Release(); }
        }

        private async Task UpdateGraphForFileAsync(string filePath)
        {
            // 1. Clear old data for this file
            await _storage.ExecuteAsync("DELETE FROM NexusNodes WHERE Metadata = @Path", new { Path = filePath }).ConfigureAwait(false);
            await _storage.ExecuteAsync("DELETE FROM NexusEdges WHERE SourceId = @Path OR TargetId IN (SELECT Id FROM NexusNodes WHERE Metadata = @Path)", new { Path = filePath }).ConfigureAwait(false);

            if (!File.Exists(filePath)) return;

            // 2. Re-analyze and persist
            var tempGraph = new NexusGraph();
            AnalyzeFile(filePath, tempGraph);

            await _storage.GetLock().WaitAsync().ConfigureAwait(false);
            try
            {
                var connection = _storage.GetConnection();
                if (connection == null) return;

                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var node in tempGraph.Nodes)
                    {
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "INSERT OR REPLACE INTO NexusNodes (Id, Label, Type, Metadata) VALUES (@Id, @Label, @Type, @Path)";
                            cmd.Parameters.AddWithValue("@Id", node.Id);
                            cmd.Parameters.AddWithValue("@Label", node.Name);
                            cmd.Parameters.AddWithValue("@Type", node.Type.ToString());
                            cmd.Parameters.AddWithValue("@Path", filePath);
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    foreach (var edge in tempGraph.Edges)
                    {
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "INSERT OR REPLACE INTO NexusEdges (SourceId, TargetId, Type) VALUES (@Src, @Tgt, @Type)";
                            cmd.Parameters.AddWithValue("@Src", edge.FromId);
                            cmd.Parameters.AddWithValue("@Tgt", edge.ToId);
                            cmd.Parameters.AddWithValue("@Type", edge.Type.ToString());
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Nexus] Failed to update graph for {filePath}", ex);
            }
            finally
            {
                _storage.GetLock().Release();
            }
        }

        private void AnalyzeFile(string file, NexusGraph graph)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 1024 * 1024) return;
                string content = File.ReadAllText(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (ext == ".cs") AnalyzeCSharpFile(file, content, graph);
                else if (ext == ".ts" || ext == ".tsx") AnalyzeTypeScriptFile(file, content, graph);
            }
            catch { }
        }

        private void AnalyzeCSharpFile(string file, string content, NexusGraph graph)
        {
            bool isModel = file.Contains("\\Models\\") || file.Contains("\\Dtos\\");
            if (isModel) graph.Nodes.Add(new NexusNode { Id = file, Name = Path.GetFileNameWithoutExtension(file), Type = NexusNodeType.DataModel, FilePath = file });

            if (content.Contains(": ControllerBase") || content.Contains("[ApiController]"))
            {
                var ctrl = new NexusNode { Id = file, Name = Path.GetFileNameWithoutExtension(file), Type = NexusNodeType.Controller, FilePath = file };
                graph.Nodes.Add(ctrl);

                var matches = Regex.Matches(content, @"\[(?<verb>Http[a-zA-Z]+)(?:\(""(?<route>[^""]*)""\))?\]");
                foreach (Match m in matches)
                {
                    string verb = m.Groups["verb"].Value.Replace("Http", "").ToUpper();
                    string endpointId = $"api://{verb}/{m.Groups["route"].Value.Trim('/')}";
                    graph.Nodes.Add(new NexusNode { Id = endpointId, Name = endpointId, Type = NexusNodeType.ApiEndpoint });
                    graph.Edges.Add(new NexusEdge { FromId = endpointId, ToId = ctrl.Id, Type = NexusEdgeType.MapsTo });
                }
            }
        }

        private void AnalyzeTypeScriptFile(string file, string content, NexusGraph graph)
        {
            bool isComp = content.Contains("@Component") || content.Contains("React.Component");
            if (isComp)
            {
                var node = new NexusNode { Id = file, Name = Path.GetFileNameWithoutExtension(file), Type = NexusNodeType.Component, FilePath = file };
                graph.Nodes.Add(node);

                var httpMatches = Regex.Matches(content, @"\.(?<verb>get|post|put|delete)\s*\(\s*[`'""'](?<url>[^`'""\s)]+)[`'""]");
                foreach (Match m in httpMatches)
                {
                    string endpointId = $"api://{m.Groups["verb"].Value.ToUpper()}/{m.Groups["url"].Value.Trim('/')}";
                    graph.Edges.Add(new NexusEdge { FromId = node.Id, ToId = endpointId, Type = NexusEdgeType.Calls });
                }
            }
        }

        private async Task TryMigrateLegacyNexusAsync(string root)
        {
            string path = Path.Combine(root, ".localpilot", "nexus.json");
            if (!File.Exists(path)) return;
            try
            {
                LocalPilotLogger.Log("[Nexus] Migrating legacy graph to SQLite...");
                var loaded = JsonConvert.DeserializeObject<NexusGraph>(File.ReadAllText(path));
                if (loaded != null)
                {
                    foreach (var n in loaded.Nodes) await _storage.ExecuteAsync("INSERT OR REPLACE INTO NexusNodes (Id, Label, Type, Metadata) VALUES (@Id, @L, @T, @P)", new { Id = n.Id, L = n.Name, T = n.Type.ToString(), P = n.FilePath });
                    foreach (var e in loaded.Edges) await _storage.ExecuteAsync("INSERT OR REPLACE INTO NexusEdges (SourceId, TargetId, Type) VALUES (@S, @T, @Ty)", new { S = e.FromId, T = e.ToId, Ty = e.Type.ToString() });
                }
                File.Delete(path);
                LocalPilotLogger.Log("[Nexus] Migration complete. Legacy graph deleted.");
            }
            catch { }
        }

        private IEnumerable<string> SafeEnumerateFiles(DirectoryInfo dir)
        {
            var files = new List<string>();
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(dir);

            while (queue.Count > 0)
            {
                var currentDir = queue.Dequeue();
                try
                {
                    string name = currentDir.Name;
                    if (name == "bin" || name == "obj" || name == ".git" || name == "node_modules") continue;

                    foreach (var file in currentDir.GetFiles())
                    {
                        var ext = file.Extension.ToLowerInvariant();
                        if (ext == ".cs" || ext == ".ts" || ext == ".tsx")
                            files.Add(file.FullName);
                    }

                    foreach (var subDir in currentDir.GetDirectories())
                        queue.Enqueue(subDir);
                }
                catch { }
            }
            return files;
        }

        public void Dispose() { _cts.Cancel(); _watcher?.Dispose(); }
    }
}
