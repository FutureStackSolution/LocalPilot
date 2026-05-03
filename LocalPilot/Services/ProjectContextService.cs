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
                                
                                if (!dbSnapshot.TryGetValue(key, out var snapshot) || info.LastWriteTime > snapshot.lastMod)
                                {
                                    // Deep check: If timestamp is newer, check the actual content hash
                                    // This prevents re-indexing if git changed the timestamp but not the code
                                    string currentHash = ComputeHash(File.ReadAllText(f));
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

        private async Task<Dictionary<string, (DateTime lastMod, string hash)>> GetFileHashesFromDbAsync()
        {
            var dict = new Dictionary<string, (DateTime lastMod, string hash)>(StringComparer.OrdinalIgnoreCase);
            await _storage.GetLock().WaitAsync();
            try
            {
                using (var cmd = _storage.GetConnection().CreateCommand())
                {
                    cmd.CommandText = "SELECT Path, LastIndexed, Hash FROM Files";
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string path = reader.GetString(0);
                            DateTime lastMod = reader.GetDateTime(1);
                            string hash = await reader.IsDBNullAsync(2) ? "" : reader.GetString(2);
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
            if (fileInfo.Length > 1024000) return; // 1MB cap

            string content = await Task.Run(() => File.ReadAllText(fullPath), ct);
            if (string.IsNullOrWhiteSpace(content)) return;

            // 1. Semantic Chunking (Roslyn-powered)
            string hash = ComputeHash(content);
            var chunks = GetSemanticChunks(content, Path.GetExtension(fullPath).ToLower());

            // 2. Persist to SQLite
            await _storage.GetLock().WaitAsync(ct);
            try
            {
                using (var transaction = _storage.GetConnection().BeginTransaction())
                {
                    try
                    {
                        // Clear old data for this file
                        using (var cmd = _storage.GetConnection().CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.Parameters.AddWithValue("@Path", relativePath);
                            
                            cmd.CommandText = "DELETE FROM Files WHERE Path = @Path";
                            await cmd.ExecuteNonQueryAsync(ct);
                            
                            cmd.CommandText = "DELETE FROM SearchIndex WHERE Path = @Path";
                            await cmd.ExecuteNonQueryAsync(ct);
                            
                            cmd.CommandText = "DELETE FROM Chunks WHERE Path = @Path";
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        // Insert into Files registry
                        using (var cmd = _storage.GetConnection().CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "INSERT INTO Files (Path, Content, LastIndexed, Hash) VALUES (@Path, @Content, @LastIndexed, @Hash)";
                            cmd.Parameters.AddWithValue("@Path", relativePath);
                            cmd.Parameters.AddWithValue("@Content", content);
                            cmd.Parameters.AddWithValue("@LastIndexed", fileInfo.LastWriteTime);
                            cmd.Parameters.AddWithValue("@Hash", hash);
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        // 🚀 WORLD-CLASS: Process chunks IN PARALLEL for massive speedup
                        string embeddingModel = LocalPilotSettings.Instance.EmbeddingModel;
                        var chunkTasks = chunks.Select(async chunkText =>
                        {
                            float[] v = null;
                            if (!string.IsNullOrWhiteSpace(embeddingModel) && !ollama.CircuitBreakerTripped)
                            {
                                try { v = await ollama.GetEmbeddingsAsync(embeddingModel, chunkText, ct); } catch { }
                            }
                            return new { Text = chunkText, Vector = v };
                        });

                        var processedChunks = await Task.WhenAll(chunkTasks);

                        foreach (var chunk in processedChunks)
                        {
                            long chunkId = 0;
                            // 1. Update Chunks table with vectors (Get the ID for fast JOINs)
                            using (var cmd = _storage.GetConnection().CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "INSERT INTO Chunks (Path, Content, Vector) VALUES (@Path, @Content, @Vector); SELECT last_insert_rowid();";
                                cmd.Parameters.AddWithValue("@Path", relativePath);
                                cmd.Parameters.AddWithValue("@Content", chunk.Text);
                                cmd.Parameters.AddWithValue("@Vector", chunk.Vector != null ? GetRawBytes(chunk.Vector) : null);
                                chunkId = (long)await cmd.ExecuteScalarAsync(ct);
                            }

                            // 2. Update Search Index (FTS5) with ChunkId reference
                            using (var cmd = _storage.GetConnection().CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "INSERT INTO SearchIndex (Content, Path, ChunkId) VALUES (@Content, @Path, @ChunkId)";
                                cmd.Parameters.AddWithValue("@Content", chunk.Text);
                                cmd.Parameters.AddWithValue("@Path", relativePath);
                                cmd.Parameters.AddWithValue("@ChunkId", chunkId);
                                await cmd.ExecuteNonQueryAsync(ct);
                            }
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

        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query)) return string.Empty;

                // 1. Get Query Embedding (in parallel with FTS fetch)
                string embeddingModel = LocalPilotSettings.Instance.EmbeddingModel;
                Task<float[]> queryVectorTask = null;
                if (!string.IsNullOrWhiteSpace(embeddingModel) && !ollama.CircuitBreakerTripped)
                {
                    queryVectorTask = ollama.GetEmbeddingsAsync(embeddingModel, query);
                }

                var candidates = new List<(string path, string content, double bm25Score, float[] vector)>();

                // 2. 🚀 FAST HYBRID FETCH: Keywords (BM25) + Metadata (Vectors)
                using (var cmd = _storage.GetConnection().CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT si.Path, si.Content, bm25(SearchIndex) as rank, c.Vector 
                        FROM SearchIndex si
                        JOIN Chunks c ON si.ChunkId = c.Id
                        WHERE SearchIndex MATCH @query 
                        ORDER BY rank 
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@query", query);
                    cmd.Parameters.AddWithValue("@limit", topN * 4); // Over-sample for re-ranking

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            byte[] vectorBytes = await reader.IsDBNullAsync(3) ? null : (byte[])reader.GetValue(3);
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
                    return new { c.path, c.content, score };
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
        }

        private async Task TryMigrateLegacyIndexAsync(string root)
        {
            string legacyPath = Path.Combine(root, ".localpilot", "index.json");
            if (!File.Exists(legacyPath)) return;

            try
            {
                LocalPilotLogger.Log("[RAG] Migrating legacy JSON index to SQLite...");
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
                
                // Cleanup: Delete legacy file now that data is in SQLite
                File.Delete(legacyPath);
                LocalPilotLogger.Log("[RAG] Migration complete. Legacy index deleted.");
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
                _watcher.Created += (s, e) => { if (IsRelevantFile(e.FullPath)) _pendingFiles.TryAdd(e.FullPath, 0); };
                _watcher.Deleted += (s, e) => 
                {
                    _ = Task.Run(async () => {
                        await _storage.ExecuteAsync("DELETE FROM Files WHERE Path = @Path", new { Path = GetRelativePath(e.FullPath) });
                        await _storage.ExecuteAsync("DELETE FROM SearchIndex WHERE Path = @Path", new { Path = GetRelativePath(e.FullPath) });
                    });
                };
                
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
