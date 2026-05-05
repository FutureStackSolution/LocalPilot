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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalPilot.Services
{

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

        private class LegacyCodeChunk
        {
            public string FilePath { get; set; }
            public string Content { get; set; }
            public DateTime LastModified { get; set; }
        }

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
                    LocalPilotLogger.Log($"Solution changed. Active root: {currentRoot}", LogCategory.Agent);
                    _solutionRoot = currentRoot;
                    
                    // Legacy migration check
                    await TryMigrateLegacyIndexAsync(_solutionRoot);
                }

                await Task.Run(async () =>
                {
                    try
                    {
                        LocalPilotLogger.Log("Starting differential SQLite sync...", LogCategory.Agent);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        
                        var allFiles = SafeEnumerateFiles(new DirectoryInfo(_solutionRoot)).ToList();

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
                                
                                if (!dbSnapshot.TryGetValue(key, out var snapshot) || info.LastWriteTime > snapshot.lastMod)
                                {
                                    string currentContent = await ReadSafeTextAsync(f);
                                    string currentHash = ComputeHash(currentContent);
                                    if (snapshot.hash != currentHash)
                                    {
                                        filesToUpdate.Add(f);
                                    }
                                }
                            }
                            catch { }
                        }

                        if (filesToUpdate.Any())
                        {
                            LocalPilotLogger.Log($"Updating {filesToUpdate.Count} files in persistent storage...", LogCategory.Agent);
                            await ParallelUpdateAsync(filesToUpdate, ollama, ct);
                            sw.Stop();
                            LocalPilotLogger.Log($"[RAG] SQLite sync complete in {sw.ElapsedMilliseconds}ms.", LogCategory.Performance);
                        }
                        else
                        {
                            LocalPilotLogger.Log("SQLite index is up to date.", LogCategory.Agent);
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

        private async Task<Dictionary<string, (DateTime lastMod, string hash)>> GetFileHashesFromDbAsync()
        {
            var dict = new Dictionary<string, (DateTime lastMod, string hash)>(StringComparer.OrdinalIgnoreCase);
            await _storage.GetLock().WaitAsync().ConfigureAwait(false);
            try
            {
                var connection = _storage.GetConnection();
                if (connection == null) return dict;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Path, LastIndexed, Hash FROM Files";
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            if (await reader.IsDBNullAsync(0).ConfigureAwait(false)) continue;
                            string path = reader.GetString(0);
                            DateTime lastMod = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? DateTime.MinValue : reader.GetDateTime(1);
                            string hash = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? "" : reader.GetString(2);
                            dict[path.ToLowerInvariant()] = (lastMod, hash);
                        }
                    }
                }
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Failed to read DB snapshot", ex); }
            finally
            {
                _storage.GetLock().Release();
            }
            return dict;
        }

        private async Task<string> ReadSafeTextAsync(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 2 * 1024 * 1024) return string.Empty; // Skip files > 2MB for stability
                return await Task.Run(() => File.ReadAllText(path));
            }
            catch { return string.Empty; }
        }

        private async Task ParallelUpdateAsync(List<string> files, OllamaService ollama, CancellationToken ct)
        {
            int concurrency = Math.Max(1, Math.Min(8, LocalPilotSettings.Instance.BackgroundIndexingConcurrency));
            int batchSize = 50; // Process 50 files per transaction batch
            
            for (int i = 0; i < files.Count; i += batchSize)
            {
                if (ollama.CircuitBreakerTripped || ct.IsCancellationRequested || GlobalPriorityGuard.ShouldYield()) break;

                var currentBatch = files.Skip(i).Take(batchSize).ToList();
                var batchData = new ConcurrentBag<(string path, string content, List<string> chunks, List<float[]> vectors)>();

                // 1. Parallel Prefetch & Embed (CPU + Ollama)
                using (var semaphore = new SemaphoreSlim(concurrency))
                {
                    var tasks = currentBatch.Select(async file =>
                    {
                        // 🚀 RESOURCE HYGIENE: Yield immediately if the Agent starts a turn
                        if (GlobalPriorityGuard.ShouldYield() || ct.IsCancellationRequested) return;

                        await semaphore.WaitAsync(ct);
                        try
                        {
                            string content = await ReadSafeTextAsync(file);
                            if (string.IsNullOrWhiteSpace(content)) return;

                            var chunks = GetSemanticChunks(content, Path.GetExtension(file).ToLower());
                            if (!chunks.Any()) return;

                            var vectors = await ollama.GetEmbeddingsBatchAsync(LocalPilotSettings.Instance.EmbeddingModel, chunks, ct);
                            if (vectors != null)
                            {
                                batchData.Add((GetRelativePath(file), content, chunks, vectors));
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);
                }

                // 2. Atomic SQLite Batch Write (One transaction for 25 files)
                if (batchData.Any())
                {
                    await CommitBatchToDbAsync(batchData.ToList(), ct);
                }
            }
        }

        private async Task CommitBatchToDbAsync(List<(string path, string content, List<string> chunks, List<float[]> vectors)> batch, CancellationToken ct)
        {
            await _storage.GetLock().WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = _storage.GetConnection();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // 🚀 PREPARE COMMANDS ONCE PER BATCH
                        using (var delCmd = connection.CreateCommand())
                        using (var insFileCmd = connection.CreateCommand())
                        using (var insChunkCmd = connection.CreateCommand())
                        using (var insSearchCmd = connection.CreateCommand())
                        {
                            delCmd.Transaction = transaction;
                            delCmd.CommandText = "DELETE FROM Files WHERE Path = @P; DELETE FROM SearchIndex WHERE Path = @P; DELETE FROM Chunks WHERE Path = @P;";
                            var pDel = delCmd.Parameters.Add("@P", SqliteType.Text);

                            insFileCmd.Transaction = transaction;
                            insFileCmd.CommandText = "INSERT INTO Files (Path, Content, LastIndexed, Hash) VALUES (@P, @C, @L, @H)";
                            var pFileP = insFileCmd.Parameters.Add("@P", SqliteType.Text);
                            var pFileC = insFileCmd.Parameters.Add("@C", SqliteType.Text);
                            var pFileL = insFileCmd.Parameters.Add("@L", SqliteType.Text);
                            var pFileH = insFileCmd.Parameters.Add("@H", SqliteType.Text);

                            insChunkCmd.Transaction = transaction;
                            insChunkCmd.CommandText = "INSERT INTO Chunks (Path, Content, Vector) VALUES (@P, @C, @V); SELECT last_insert_rowid();";
                            var pChunkP = insChunkCmd.Parameters.Add("@P", SqliteType.Text);
                            var pChunkC = insChunkCmd.Parameters.Add("@C", SqliteType.Text);
                            var pChunkV = insChunkCmd.Parameters.Add("@V", SqliteType.Blob);

                            insSearchCmd.Transaction = transaction;
                            insSearchCmd.CommandText = "INSERT INTO SearchIndex (Content, Path, ChunkId) VALUES (@C, @P, @Id)";
                            var pSearchC = insSearchCmd.Parameters.Add("@C", SqliteType.Text);
                            var pSearchP = insSearchCmd.Parameters.Add("@P", SqliteType.Text);
                            var pSearchId = insSearchCmd.Parameters.Add("@Id", SqliteType.Integer);

                            foreach (var item in batch)
                            {
                                var fileInfo = new FileInfo(_solutionRoot + "\\" + item.path);
                                string hash = ComputeHash(item.content);

                                // 1. Cleanup
                                pDel.Value = item.path;
                                await delCmd.ExecuteNonQueryAsync(ct);

                                // 2. File Entry
                                pFileP.Value = item.path;
                                pFileC.Value = item.content;
                                pFileL.Value = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                                pFileH.Value = hash;
                                await insFileCmd.ExecuteNonQueryAsync(ct);

                                // 3. Chunks & Search Index
                                for (int j = 0; j < item.chunks.Count; j++)
                                {
                                    pChunkP.Value = item.path;
                                    pChunkC.Value = item.chunks[j];
                                    pChunkV.Value = item.vectors[j] != null ? (object)GetRawBytes(item.vectors[j]) : DBNull.Value;
                                    long chunkId = (long)await insChunkCmd.ExecuteScalarAsync(ct);

                                    pSearchC.Value = item.chunks[j];
                                    pSearchP.Value = item.path;
                                    pSearchId.Value = chunkId;
                                    await insSearchCmd.ExecuteNonQueryAsync(ct);
                                }
                            }
                        }
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        LocalPilotLogger.LogError("[RAG] Bulk DB transaction failed", ex);
                    }
                }
            }
            finally { _storage.GetLock().Release(); }
        }

        private List<string> GetSemanticChunks(string content, string ext)
        {
            var chunks = new List<string>();
            if (ext == ".cs")
            {
                try
                {
                    // 🚀 WORLD-CLASS: Roslyn-powered C# Chunking
                    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content);
                    var root = tree.GetRoot();
                    var nodes = root.DescendantNodes().Where(n => 
                        n is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax ||
                        n is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax ||
                        n is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);

                    foreach (var node in nodes)
                    {
                        string chunk = node.ToFullString();
                        if (chunk.Length > 100) chunks.Add(chunk);
                    }
                }
                catch { /* Fallback */ }
            }
            else if (ext == ".js" || ext == ".ts" || ext == ".tsx" || ext == ".py" || ext == ".go" || ext == ".rs")
            {
                // 🚀 WORLD-CLASS: Regex-based Semantic Fallback for Web/Systems languages
                var patterns = new[] {
                    @"^(?:export\s+)?(?:class|interface|struct|type|trait|enum)\s+\w+",
                    @"^(?:export\s+)?(?:async\s+)?(?:function|func|def)\s+\w+",
                    @"^(?:const|let|var)\s+\w+\s*=\s*(?:async\s*)?\([^)]*\)\s*=>",
                    @"^\s*\[Http[a-zA-Z]+(?:\("".*""\))?\]",
                };
                
                var lines = content.Split('\n');
                var currentChunk = new StringBuilder();
                
                foreach (var line in lines)
                {
                    bool isHeader = patterns.Any(p => Regex.IsMatch(line.Trim(), p));
                    if (isHeader && currentChunk.Length > 200)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }
                    currentChunk.AppendLine(line);
                    
                    if (currentChunk.Length > 2000)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }
                }
                if (currentChunk.Length > 50) chunks.Add(currentChunk.ToString());
            }

            if (!chunks.Any())
            {
                const int chunkSize = 2000;
                for (int i = 0; i < content.Length; i += 1800)
                {
                    chunks.Add(content.Substring(i, Math.Min(chunkSize, content.Length - i)));
                }
            }
            return chunks.Distinct().ToList();
        }

        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;

            string embeddingModel = LocalPilotSettings.Instance.EmbeddingModel;
            Task<float[]> queryVectorTask = null;
            if (!string.IsNullOrWhiteSpace(embeddingModel) && !ollama.CircuitBreakerTripped)
            {
                queryVectorTask = ollama.GetEmbeddingsAsync(embeddingModel, query);
            }

            // 🚀 EXPERT: Use the global lock even for reads to prevent 'Database is locked' 
            // during heavy background indexing/renaming.
            await _storage.GetLock().WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = _storage.GetConnection();
                if (connection == null) return string.Empty;

                // Clean up the query to prevent FTS5 syntax crashes (e.g. from parentheses or asterisks)
                string sanitizedQuery = Regex.Replace(query, @"[^\w\s]", " ").Trim();
                if (string.IsNullOrEmpty(sanitizedQuery)) return string.Empty;

                var candidates = new List<(string path, string content, double bm25Score, float[] vector)>();

                // 2. 🚀 FAST HYBRID FETCH: Keywords (BM25) + Metadata (Vectors)
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT si.Path, si.Content, si.rank, c.Vector 
                        FROM SearchIndex si
                        LEFT JOIN Chunks c ON si.ChunkId = c.Id
                        WHERE SearchIndex MATCH @query 
                        ORDER BY rank 
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@query", sanitizedQuery);
                    cmd.Parameters.AddWithValue("@limit", topN * 4); // Over-sample for re-ranking

                    using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            byte[] vectorBytes = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : (byte[])reader.GetValue(3);
                            float[] vec = ParseRawBytes(vectorBytes);
                            // bm25 rank is negative (lower is better), so we flip it for scoring
                            candidates.Add((reader.GetString(0), reader.GetString(1), -reader.GetDouble(2), vec));
                        }
                    }
                }

                if (!candidates.Any()) return string.Empty;

                float[] queryVector = null;
                if (queryVectorTask != null) 
                {
                    try { queryVector = await queryVectorTask; } catch { }
                }

                // 3. 🧠 SEMANTIC RE-RANKING: Vector Similarity + BM25 Fusion
                var rankedResults = candidates.Select(c =>
                {
                    double vectorScore = (queryVector != null && c.vector != null) ? CosineSimilarity(queryVector, c.vector) : 0;
                    // Weighted Fusion: 80% Semantic, 20% Keyword
                    double score = vectorScore > 0 ? (vectorScore * 0.8) + (Math.Min(0.5, c.bm25Score / 100.0) * 0.2) : c.bm25Score;
                    return new { path = c.path, content = c.content, score };
                })
                .OrderByDescending(x => x.score)
                .Take(topN)
                .ToList();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("\n<grounding_context>");
                foreach (var r in rankedResults)
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
            finally
            {
                _storage.GetLock().Release();
            }
        }

        private async Task TryMigrateLegacyIndexAsync(string root)
        {
            string legacyPath = Path.Combine(root, ".localpilot", "index.json");
            if (!File.Exists(legacyPath)) return;

            try
            {
                LocalPilotLogger.Log("Migrating legacy JSON index to SQLite...", LogCategory.Storage);
                string json = File.ReadAllText(legacyPath);
                
                Dictionary<string, List<LegacyCodeChunk>> loaded = null;
                try
                {
                    loaded = JsonConvert.DeserializeObject<Dictionary<string, List<LegacyCodeChunk>>>(json);
                }
                catch (JsonSerializationException)
                {
                    // Fallback: If it's a flat array (legacy format), group it by FilePath
                    var flatList = JsonConvert.DeserializeObject<List<LegacyCodeChunk>>(json);
                    if (flatList != null)
                    {
                        loaded = flatList.Where(c => !string.IsNullOrEmpty(c.FilePath))
                                         .GroupBy(c => c.FilePath)
                                         .ToDictionary(g => g.Key, g => g.ToList());
                    }
                }
                
                if (loaded != null && loaded.Count > 0)
                {
                    foreach (var kvp in loaded)
                    {
                        string path = kvp.Key;
                        string content = string.Join("\n", kvp.Value.Select(c => c.Content));
                        DateTime lastMod = kvp.Value.FirstOrDefault()?.LastModified ?? DateTime.Now;
                        
                        await _storage.ExecuteAsync(@"
                            INSERT OR REPLACE INTO Files (Path, Content, LastIndexed, Metadata) 
                            VALUES (@Path, @Content, @LastIndexed, '')", 
                            new { Path = path, Content = content, LastIndexed = lastMod });
                        
                        await _storage.ExecuteAsync("INSERT INTO SearchIndex (Content, Path) VALUES (@Content, @Path)",
                            new { Content = content, Path = path });
                    }
                }
                
                // Cleanup: Delete legacy file now that data is in SQLite
                File.Delete(legacyPath);
                LocalPilotLogger.Log("Migration complete. Legacy index deleted.", LogCategory.Storage);
            }
            catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Legacy migration failed", ex); }
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
                _watcher.Created += (s, e) => { if (IsRelevantFile(e.FullPath)) _pendingFiles.TryAdd(e.FullPath, 0); };
                _watcher.Deleted += (s, e) => { _pendingFiles.TryAdd(e.FullPath, 0); };
                _watcher.Renamed += (s, e) => {
                    _pendingFiles.TryAdd(e.OldFullPath, 0);
                    _pendingFiles.TryAdd(e.FullPath, 0);
                };

                LocalPilotLogger.Log($"FileSystemWatcher active on: {_solutionRoot}", LogCategory.Storage);
                
                // Start the background processing loop for the queue
                _ = Task.Run(async () => {
                    while (!_watcherCts.IsCancellationRequested)
                    {
                        await Task.Delay(5000); // Process every 5 seconds
                        if (_pendingFiles.IsEmpty) continue;

                        while (_pendingFiles.TryRemove(_pendingFiles.Keys.FirstOrDefault() ?? "", out _))
                        {
                            // Atomic clear — re-scanning is safer for consistent state after batches
                        }
                        
                        LocalPilotLogger.Log("Incremental changes detected. Re-indexing...", LogCategory.Storage);
                        await IndexSolutionAsync(ollama, _watcherCts.Token);
                    }
                });
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] Failed to setup FileSystemWatcher", ex);
            }
        }

        private static byte[] GetRawBytes(float[] floats)
        {
            byte[] dest = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, dest, 0, dest.Length);
            return dest;
        }

        private static string ComputeHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                byte[] hash = md5.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static float[] ParseRawBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length % sizeof(float) != 0) return null;
            float[] floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }

        private static float CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0;
            float dot = 0, mag1 = 0, mag2 = 0;
            for (int i = 0; i < v1.Length; i++)
            {
                dot += v1[i] * v2[i];
                mag1 += v1[i] * v1[i];
                mag2 += v2[i] * v2[i];
            }
            if (mag1 <= 0 || mag2 <= 0) return 0;
            return dot / (float)(Math.Sqrt(mag1) * Math.Sqrt(mag2));
        }
    }
}
