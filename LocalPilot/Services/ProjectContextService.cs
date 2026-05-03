using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LocalPilot.Settings;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace LocalPilot.Services
{
    public class CodeChunk
    {
        public string FilePath { get; set; }
        public string Content { get; set; }
        
        [JsonIgnore]
        public float[] Vector { get; set; }

        [JsonProperty("V")]
        public string VectorBase64
        {
            get => Vector == null ? null : Convert.ToBase64String(GetRawBytes(Vector));
            set => Vector = string.IsNullOrEmpty(value) ? null : GetFloats(Convert.FromBase64String(value));
        }

        public DateTime LastModified { get; set; }

        private static byte[] GetRawBytes(float[] floats)
        {
            byte[] dest = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, dest, 0, dest.Length);
            return dest;
        }

        private static float[] GetFloats(byte[] bytes)
        {
            float[] dest = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, dest, 0, bytes.Length);
            return dest;
        }
    }

    /// <summary>
    /// Local RAG Service: Indexes the Visual Studio solution and provides semantic search.
    /// UPGRADED (v4.0): Uses SQLite Storage Engine for "Out-of-Memory" indexing.
    /// This eliminates IDE freezes during index saves and keeps RAM usage near-zero.
    /// </summary>
    public class ProjectContextService
    {
        private static readonly ProjectContextService _instance = new ProjectContextService();
        public static ProjectContextService Instance => _instance;

        private readonly StorageService _storage = StorageService.Instance;
        private readonly SemaphoreSlim _indexLock = new SemaphoreSlim(1, 1);
        private string _solutionRoot = string.Empty;
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, byte> _pendingFiles = new ConcurrentDictionary<string, byte>();
        private CancellationTokenSource _watcherCts;
        private volatile bool _isIndexing = false;

        private ProjectContextService() { }

        public async Task IndexSolutionAsync(OllamaService ollama, CancellationToken ct = default)
        {
            if (_isIndexing) return;
            if (!await _indexLock.WaitAsync(0)) return;
            _isIndexing = true;

            try
            {
                string currentRoot = null;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                currentRoot = Path.GetDirectoryName(solution.FullPath);

                if (string.IsNullOrEmpty(currentRoot) || !Directory.Exists(currentRoot)) return;

                if (_solutionRoot != currentRoot)
                {
                    LocalPilotLogger.Log($"[RAG] Solution changed. Active root: {currentRoot}", LogCategory.Agent);
                    _solutionRoot = currentRoot;
                    
                    // Legacy migration check
                    await TryMigrateLegacyIndexAsync(_solutionRoot);
                }

                await Task.Run(async () =>
                {
                    try
                    {
                        LocalPilotLogger.Log("[RAG] Starting differential SQLite sync...", LogCategory.Agent);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        
                        var allFiles = Directory.EnumerateFiles(_solutionRoot, "*.*", SearchOption.AllDirectories)
                                                .Where(IsRelevantFile).ToList();

                        var filesToUpdate = new List<string>();
                        
                        // Differential check against DB
                        var dbSnapshot = await GetFileHashesFromDbAsync();

                        foreach (var f in allFiles)
                        {
                            try
                            {
                                var info = new FileInfo(f);
                                var relPath = GetRelativePath(f);
                                string key = relPath.ToLowerInvariant();
                                
                                if (!dbSnapshot.TryGetValue(key, out var lastModified) || info.LastWriteTime > lastModified)
                                {
                                    filesToUpdate.Add(f);
                                }
                            }
                            catch { }
                        }

                        if (filesToUpdate.Any())
                        {
                            LocalPilotLogger.Log($"[RAG] Updating {filesToUpdate.Count} files in persistent storage...", LogCategory.Agent);
                            await ParallelUpdateAsync(filesToUpdate, ollama, ct);
                            sw.Stop();
                            LocalPilotLogger.Log($"[RAG] SQLite sync complete in {sw.ElapsedMilliseconds}ms.", LogCategory.Performance);
                        }
                        else
                        {
                            LocalPilotLogger.Log("[RAG] SQLite index is up to date.", LogCategory.Agent);
                        }
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError("[RAG] Background indexing failed", ex);
                    }
                });

                SetupIncrementalWatcher(ollama);
            }
            finally
            {
                _isIndexing = false;
                _indexLock.Release();
            }
        }

        private async Task<Dictionary<string, DateTime>> GetFileHashesFromDbAsync()
        {
            var dict = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cmd = _storage.GetConnection().CreateCommand())
                {
                    cmd.CommandText = "SELECT Path, LastIndexed FROM Files";
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string path = reader.GetString(0);
                            DateTime lastMod = reader.GetDateTime(1);
                            dict[path.ToLowerInvariant()] = lastMod;
                        }
                    }
                }
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Failed to read DB snapshot", ex); }
            return dict;
        }

        private async Task ParallelUpdateAsync(List<string> files, OllamaService ollama, CancellationToken ct)
        {
            int concurrency = Math.Max(1, Math.Min(8, LocalPilotSettings.Instance.BackgroundIndexingConcurrency));
            using (var semaphore = new SemaphoreSlim(concurrency))
            {
                var tasks = files.Select(async file =>
                {
                    if (ollama.CircuitBreakerTripped || ct.IsCancellationRequested) return;

                    await semaphore.WaitAsync(ct);
                    try
                    {
                        await ProcessFileAsync(file, GetRelativePath(file), ollama, ct);
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError($"[RAG] Failed to index {Path.GetFileName(file)}", ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
        }

        private async Task ProcessFileAsync(string fullPath, string relativePath, OllamaService ollama, CancellationToken ct)
        {
            if (!File.Exists(fullPath)) return;
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 512000) return; // 500KB cap

            string content = await Task.Run(() => File.ReadAllText(fullPath), ct);
            if (string.IsNullOrWhiteSpace(content)) return;

            // 1. Generate Embeddings (if enabled)
            float[] vector = null;
            string embeddingModel = LocalPilotSettings.Instance.EmbeddingModel;
            if (!string.IsNullOrWhiteSpace(embeddingModel) && !ollama.CircuitBreakerTripped)
            {
                try { vector = await ollama.GetEmbeddingsAsync(embeddingModel, content, ct); } catch { }
            }

            // 2. Persist to SQLite
            await _storage.GetLock().WaitAsync(ct);
            try
            {
                using (var transaction = _storage.GetConnection().BeginTransaction())
                {
                    try
                    {
                        // Update Files table
                        using (var cmd = _storage.GetConnection().CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT OR REPLACE INTO Files (Path, Content, LastIndexed, Metadata) 
                                VALUES (@Path, @Content, @LastIndexed, @Metadata)";
                            cmd.Parameters.AddWithValue("@Path", relativePath);
                            cmd.Parameters.AddWithValue("@Content", content);
                            cmd.Parameters.AddWithValue("@LastIndexed", fileInfo.LastWriteTime);
                            cmd.Parameters.AddWithValue("@Metadata", vector != null ? Convert.ToBase64String(GetRawBytes(vector)) : "");
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        // Update Search Index (FTS5)
                        using (var cmd = _storage.GetConnection().CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "DELETE FROM SearchIndex WHERE Path = @Path";
                            cmd.Parameters.AddWithValue("@Path", relativePath);
                            await cmd.ExecuteNonQueryAsync(ct);

                            cmd.CommandText = "INSERT INTO SearchIndex (Content, Path) VALUES (@Content, @Path)";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@Content", content);
                            cmd.Parameters.AddWithValue("@Path", relativePath);
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        LocalPilotLogger.LogError($"[RAG] DB Update failed for {relativePath}", ex);
                    }
                }
            }
            finally
            {
                _storage.GetLock().Release();
            }
        }

        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return string.Empty;

                var results = new List<(string path, string content, double score)>();

                // 🚀 ULTRA-FAST FTS5 SEARCH
                using (var cmd = _storage.GetConnection().CreateCommand())
                {
                    // Porter stemming + BM25 ranking built-in to SQLite
                    cmd.CommandText = @"
                        SELECT Path, Content, bm25(SearchIndex) as rank 
                        FROM SearchIndex 
                        WHERE Content MATCH @query 
                        ORDER BY rank 
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@query", query);
                    cmd.Parameters.AddWithValue("@limit", topN * 2);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add((reader.GetString(0), reader.GetString(1), -reader.GetDouble(2)));
                        }
                    }
                }

                if (!results.Any()) return string.Empty;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("\n<grounding_context>");
                foreach (var r in results.OrderByDescending(x => x.score).Take(topN))
                {
                    sb.AppendLine($"  <file_snippet path=\"{r.path}\">");
                    sb.AppendLine(r.content);
                    sb.AppendLine("  </file_snippet>");
                }
                sb.AppendLine("</grounding_context>");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] SearchContextAsync failed", ex);
                return string.Empty;
            }
        }

        private async Task TryMigrateLegacyIndexAsync(string root)
        {
            string legacyPath = Path.Combine(root, ".localpilot", "index.json");
            if (!File.Exists(legacyPath)) return;

            try
            {
                LocalPilotLogger.Log("[RAG] Migrating legacy JSON index to SQLite...");
                string json = File.ReadAllText(legacyPath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, List<CodeChunk>>>(json);
                
                if (loaded != null)
                {
                    foreach (var kvp in loaded)
                    {
                        string path = kvp.Key;
                        string content = string.Join("\n", kvp.Value.Select(c => c.Content));
                        DateTime lastMod = kvp.Value.FirstOrDefault()?.LastModified ?? DateTime.Now;
                        
                        // Fake a process call to insert into DB
                        // (Optimization: In a real migration we'd do this in a single transaction)
                        await _storage.ExecuteAsync(@"
                            INSERT OR REPLACE INTO Files (Path, Content, LastIndexed, Metadata) 
                            VALUES (@Path, @Content, @LastIndexed, '')", 
                            new { Path = path, Content = content, LastIndexed = lastMod });
                        
                        await _storage.ExecuteAsync("INSERT INTO SearchIndex (Content, Path) VALUES (@Content, @Path)",
                            new { Content = content, Path = path });
                    }
                }
                
                // Cleanup
                File.Move(legacyPath, legacyPath + ".old");
                LocalPilotLogger.Log("[RAG] Migration complete.");
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Legacy migration failed", ex); }
        }

        private bool IsRelevantFile(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            string[] allowed = { ".cs", ".vb", ".cshtml", ".c", ".cpp", ".h", ".py", ".js", ".ts", ".tsx", ".html", ".css", ".json", ".md", ".xml", ".xaml" };
            if (path.Contains("\\obj\\") || path.Contains("\\bin\\") || path.Contains("\\node_modules\\") || path.Contains("\\.git\\")) return false;
            return allowed.Contains(ext);
        }

        private string GetRelativePath(string fullPath)
        {
            if (!string.IsNullOrEmpty(_solutionRoot) && fullPath.StartsWith(_solutionRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(_solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            return fullPath.TrimStart(Path.DirectorySeparatorChar);
        }

        private void SetupIncrementalWatcher(OllamaService ollama)
        {
            if (_watcher != null) return;
            try
            {
                _watcherCts = new CancellationTokenSource();
                _watcher = new FileSystemWatcher(_solutionRoot) { IncludeSubdirectories = true, Filter = "*.*", EnableRaisingEvents = true };
                _watcher.Changed += (s, e) => { if (IsRelevantFile(e.FullPath)) _pendingFiles.TryAdd(e.FullPath, 0); };
                
                _ = Task.Run(async () => {
                    while (!_watcherCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, _watcherCts.Token);
                        if (_pendingFiles.IsEmpty) continue;

                        var batch = _pendingFiles.Keys.ToList();
                        _pendingFiles.Clear();
                        await ParallelUpdateAsync(batch, ollama, _watcherCts.Token);
                    }
                });
            }
            catch { }
        }

        private static byte[] GetRawBytes(float[] floats)
        {
            byte[] dest = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, dest, 0, dest.Length);
            return dest;
        }
    }
}
