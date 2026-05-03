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

namespace LocalPilot.Services
{
    public class CodeChunk
    {
        public string FilePath { get; set; }
        public string Content { get; set; }
        
        [Newtonsoft.Json.JsonIgnore]
        public float[] Vector { get; set; }

        [Newtonsoft.Json.JsonProperty("V")]
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
    /// Hardened with defensive error handling — this service MUST NOT crash the IDE under any circumstances.
    /// </summary>
    public class ProjectContextService
    {
        private static readonly ProjectContextService _instance = new ProjectContextService();
        public static ProjectContextService Instance => _instance;

        private readonly ConcurrentDictionary<string, List<CodeChunk>> _index = new ConcurrentDictionary<string, List<CodeChunk>>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _indexLock = new SemaphoreSlim(1, 1);
        private DateTime _lastIndexTime = DateTime.MinValue;
        private DateTime _lastDiskSaveTime = DateTime.MinValue;
        
        private string _solutionRoot = string.Empty;
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, byte> _pendingFiles = new ConcurrentDictionary<string, byte>();
        private CancellationTokenSource _watcherCts;

        // Tracks whether the service is currently indexing (prevents re-entrant crashes)
        private volatile bool _isIndexing = false;

        private ProjectContextService() { }

        /// <summary>
        /// Parallel differential indexing of the solution.
        /// Fully defensive — will never throw or crash the IDE.
        /// </summary>
        public async Task IndexSolutionAsync(OllamaService ollama, CancellationToken ct = default)
        {
            // Guard: prevent re-entrant indexing
            if (_isIndexing) return;

            // Non-blocking lock: if someone else is indexing, just skip
            if (!await _indexLock.WaitAsync(0)) return;
            _isIndexing = true;
            try
            {
                string currentRoot = null;
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var solution = await VS.Solutions.GetCurrentSolutionAsync();
                    if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                    currentRoot = Path.GetDirectoryName(solution.FullPath);
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[RAG] Failed to get solution root", ex);
                    return;
                }

                if (string.IsNullOrEmpty(currentRoot) || !Directory.Exists(currentRoot)) return;

                if (_solutionRoot != currentRoot)
                {
                    LocalPilotLogger.Log($"[RAG] Solution changed. Clearing index for {currentRoot}", LogCategory.Agent);
                    _index.Clear();
                    _solutionRoot = currentRoot;
                    _lastIndexTime = DateTime.MinValue;
                }

                if (_index.IsEmpty) await LoadIndexAsync(_solutionRoot);

                // SWITCH TO BACKGROUND: Don't block the UI thread during solution scanning
                await Task.Run(async () =>
                {
                    try
                    {
                        LocalPilotLogger.Log("[RAG] Starting parallel differential sync...", LogCategory.Agent);
                        
                        // 1. Collect all relevant files (defensive: catch IO errors during enumeration)
                        List<string> allFiles;
                        try
                        {
                            allFiles = Directory.EnumerateFiles(_solutionRoot, "*.*", SearchOption.AllDirectories)
                                .Where(IsRelevantFile).ToList();
                        }
                        catch (Exception ex)
                        {
                            LocalPilotLogger.LogError("[RAG] File enumeration failed — some directories may be inaccessible", ex);
                            // Fallback: try top-level only
                            try
                            {
                                allFiles = Directory.EnumerateFiles(_solutionRoot, "*.*", SearchOption.TopDirectoryOnly)
                                    .Where(IsRelevantFile).ToList();
                            }
                            catch
                            {
                                allFiles = new List<string>();
                            }
                        }

                        // 2. Filter for changed files
                        var filesToUpdate = allFiles.Where(f => {
                            try
                            {
                                var info = new FileInfo(f);
                                if (!info.Exists) return false;
                                var relPath = GetRelativePath(f);
                                if (_index.TryGetValue(relPath, out var chunks) && chunks != null && chunks.Any())
                                {
                                    return info.LastWriteTime > chunks[0].LastModified;
                                }
                                return true;
                            }
                            catch { return false; } // Skip files we can't stat
                        }).ToList();

                        if (filesToUpdate.Any())
                        {
                            LocalPilotLogger.Log($"[RAG] Indexing {filesToUpdate.Count} new or modified files...", LogCategory.Agent);
                            try 
                            {
                                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, GlobalPriorityGuard.YieldToken))
                                {
                                    await ParallelUpdateAsync(filesToUpdate, ollama, linkedCts.Token);
                                }
                            }
                            catch (OperationCanceledException) { LocalPilotLogger.Log("[RAG] Full sync yielded to agent."); }
                            catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Parallel update failed (non-fatal)", ex); }

                            _lastIndexTime = DateTime.Now;
                            await SaveIndexAsync(_solutionRoot);
                        }
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError("[RAG] Background indexing failed (non-fatal)", ex);
                    }
                });

                SetupIncrementalWatcher(ollama);
                
                try
                {
                    // Snapshot the values to avoid iterating a changing collection
                    int chunkCount = _index.Values.ToArray().Sum(v => v?.Count ?? 0);
                    LocalPilotLogger.Log($"[RAG] Sync complete. {chunkCount} chunks ready.");
                }
                catch { LocalPilotLogger.Log("[RAG] Sync complete."); }
            }
            catch (Exception ex)
            {
                // ABSOLUTE LAST RESORT: If anything above leaked, catch it here.
                // Under no circumstances should indexing crash the IDE.
                LocalPilotLogger.LogError("[RAG] IndexSolutionAsync failed catastrophically (non-fatal)", ex);
            }
            finally
            {
                _isIndexing = false;
                _indexLock.Release();
            }
        }

        private async Task ParallelUpdateAsync(List<string> files, OllamaService ollama, CancellationToken ct)
        {
            // Controlled parallelism using user-configured concurrency
            int concurrency = Math.Max(1, Math.Min(8, LocalPilotSettings.Instance.BackgroundIndexingConcurrency));
            using (var semaphore = new SemaphoreSlim(concurrency))
            {
                var tasks = new List<Task>();
                foreach (var file in files)
                {
                    if (ollama.CircuitBreakerTripped)
                    {
                        LocalPilotLogger.Log("[RAG] Aborting indexing due to Circuit Breaker.", LogCategory.Agent);
                        break;
                    }

                    // Check cancellation BEFORE spawning more tasks
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await semaphore.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) { break; }

                    tasks.Add(Task.Run(async () => 
                    {
                        try
                        {
                            await ProcessFileAsync(file, GetRelativePath(file), ollama, ct);
                        }
                        catch (OperationCanceledException) { } // Expected — agent started
                        catch (Exception ex)
                        {
                            // Log but don't propagate — one file failure must not kill the batch
                            LocalPilotLogger.LogError($"[RAG] Failed to index {Path.GetFileName(file)}", ex);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, CancellationToken.None)); // Use None here: we handle cancellation inside
                }

                // Wait for all in-flight tasks with a timeout to prevent infinite hangs
                try
                {
                    var allDone = Task.WhenAll(tasks);
                    if (await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromMinutes(5))) != allDone)
                    {
                        LocalPilotLogger.Log("[RAG] WARNING: Parallel indexing timed out after 5 minutes. Some files may not be indexed.", LogCategory.Agent);
                    }
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[RAG] Task.WhenAll failed (non-fatal)", ex);
                }
            }
        }

        private void SetupIncrementalWatcher(OllamaService ollama)
        {
            if (_watcher != null) return;

            try
            {
                if (!Directory.Exists(_solutionRoot)) return;

                _watcherCts = new CancellationTokenSource();
                _watcher = new FileSystemWatcher(_solutionRoot)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.*",
                    EnableRaisingEvents = true,
                    // Increase buffer size to prevent overflow events under heavy file activity
                    InternalBufferSize = 32768
                };
                
                _watcher.Changed += (s, e) => { try { if (IsRelevantFile(e.FullPath)) _pendingFiles.TryAdd(e.FullPath, 0); } catch { } };
                _watcher.Error += (s, e) => { LocalPilotLogger.Log($"[RAG] FileSystemWatcher error: {e.GetException()?.Message}", LogCategory.Agent); };
                
                // Start background consumer for incremental RAG
                _ = Task.Run(async () => {
                    while (!_watcherCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (GlobalPriorityGuard.ShouldYield() || _pendingFiles.IsEmpty)
                            {
                                await Task.Delay(5000, _watcherCts.Token);
                                continue;
                            }

                            if (!_pendingFiles.IsEmpty)
                            {
                                await Task.Delay(5000, _watcherCts.Token); // Debounce
                                var batch = _pendingFiles.Keys.ToList();
                                _pendingFiles.Clear();
                                
                                // Non-blocking lock: skip this batch if a full sync is running
                                if (!await _indexLock.WaitAsync(0)) continue;
                                try 
                                { 
                                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_watcherCts.Token, GlobalPriorityGuard.YieldToken))
                                    {
                                        await ParallelUpdateAsync(batch, ollama, linkedCts.Token); 
                                        await SaveIndexAsync(_solutionRoot); 
                                    }
                                }
                                catch (OperationCanceledException) { LocalPilotLogger.Log("[RAG] Background sync yielded to agent."); }
                                catch (Exception ex) { LocalPilotLogger.LogError("[RAG] Incremental update failed (non-fatal)", ex); }
                                finally { _indexLock.Release(); }
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            LocalPilotLogger.LogError("[RAG] Watcher consumer loop error (non-fatal)", ex);
                            await Task.Delay(10000); // Back off on errors
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] Failed to setup FileSystemWatcher (non-fatal)", ex);
            }
        }

        private string GetRelativePath(string fullPath)
        {
            if (!string.IsNullOrEmpty(_solutionRoot) && fullPath.StartsWith(_solutionRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(_solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            }
            return fullPath.TrimStart(Path.DirectorySeparatorChar);
        }

        private async Task ProcessFileAsync(string fullPath, string relativePath, OllamaService ollama, CancellationToken ct)
        {
            try
            {
                // Guard: ensure file still exists (could be deleted between enumeration and processing)
                if (!File.Exists(fullPath)) return;

                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists || fileInfo.Length > 256000 || fileInfo.Length == 0) return;

                string content;
                try
                {
                    content = await Task.Run(() => File.ReadAllText(fullPath), ct);
                }
                catch (IOException)
                {
                    // File is locked (VS might have it open for writing)
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(content)) return;

                var chunks = ChunkContent(fullPath, content);
                var newChunks = new List<CodeChunk>();

                // Use a local semaphore-per-file instead of the shared _parallelLock
                // to prevent cross-file contention from deadlocking the pipeline
                foreach (var chunkText in chunks)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        float[] vector = null;
                        string embeddingModel = LocalPilotSettings.Instance.EmbeddingModel;
                        if (!string.IsNullOrWhiteSpace(embeddingModel) && !ollama.CircuitBreakerTripped)
                        {
                            try
                            {
                                vector = await ollama.GetEmbeddingsAsync(embeddingModel, chunkText, ct);
                            }
                            catch (OperationCanceledException) { throw; } // Re-throw cancellation
                            catch (Exception ex)
                            {
                                // Embedding failed for this chunk — still index the text for keyword search
                                LocalPilotLogger.Log($"[RAG] Embedding failed for chunk in {Path.GetFileName(fullPath)}: {ex.Message}");
                            }
                        }
                        
                        // Add chunk even if vector is null (enables keyword search fallback)
                        newChunks.Add(new CodeChunk
                        {
                            FilePath = relativePath,
                            Content = chunkText,
                            Vector = vector,
                            LastModified = fileInfo.LastWriteTime
                        });
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError($"[RAG] Chunk processing failed in {Path.GetFileName(fullPath)}", ex);
                    }
                }

                if (newChunks.Any())
                {
                    _index[relativePath] = newChunks;
                }
                else
                {
                    _index.TryRemove(relativePath, out _);
                }
            }
            catch (OperationCanceledException) { } // Expected
            catch (Exception ex)
            {
                // Absolute last catch — one file must never crash the indexer
                LocalPilotLogger.LogError($"[RAG] ProcessFileAsync failed for {Path.GetFileName(fullPath)}", ex);
            }
        }

        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query) || _index.IsEmpty) return string.Empty;

                float[] queryVector = null;
                if (!string.IsNullOrWhiteSpace(LocalPilotSettings.Instance.EmbeddingModel) && !ollama.CircuitBreakerTripped)
                {
                    try
                    {
                        queryVector = await ollama.GetEmbeddingsAsync(LocalPilotSettings.Instance.EmbeddingModel, query);
                    }
                    catch { } // Non-fatal: fall back to keyword search
                }

                // Snapshot the index to prevent collection-modified exceptions during iteration
                var allChunks = _index.Values.ToArray().Where(v => v != null).SelectMany(x => x);

                var results = allChunks
                    .Select(c => 
                    {
                        try
                        {
                            double score = 0;
                            if (query.IndexOf(Path.GetFileNameWithoutExtension(c.FilePath ?? ""), StringComparison.OrdinalIgnoreCase) >= 0)
                                score += 0.3;

                            if (queryVector != null && c.Vector != null)
                            {
                                score += CosineSimilarity(queryVector, c.Vector);
                            }
                            else
                            {
                                score += CalculateKeywordScore(query, c.Content);
                            }
                            return new { Chunk = c, Score = score };
                        }
                        catch { return new { Chunk = c, Score = 0.0 }; }
                    })
                    .Where(r => r.Score > 0.1)
                    .OrderByDescending(r => r.Score)
                    .Take(topN).ToList();

                if (!results.Any()) return string.Empty;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("\n<grounding_context>");
                foreach (var r in results)
                {
                    sb.AppendLine($"  <file_snippet path=\"{r.Chunk.FilePath}\">");
                    sb.AppendLine(r.Chunk.Content);
                    sb.AppendLine("  </file_snippet>");
                }
                sb.AppendLine("</grounding_context>");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] SearchContextAsync failed (non-fatal)", ex);
                return string.Empty;
            }
        }

        private bool IsRelevantFile(string path)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLower();
                string[] allowed = { 
                    ".cs", ".vb", ".cshtml", ".vbhtml", // .NET
                    ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", // C/C++
                    ".java", ".kt", ".gradle", ".jsp", // Java/Android
                    ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", // JS/TS
                    ".html", ".css", ".scss", ".sass", ".less", // Web/Style
                    ".json", ".md", ".yaml", ".yml", ".xml", // Config/Docs
                    ".vue", ".svelte", ".astro" // Frameworks
                };
                if (path.Contains("\\.localpilot\\") || path.Contains("\\obj\\") || path.Contains("\\bin\\") || 
                    path.Contains("\\node_modules\\") || path.Contains("\\dist\\") || path.Contains("\\.git\\")) return false;
                return allowed.Contains(ext);
            }
            catch { return false; }
        }

        private List<string> ChunkContent(string path, string content)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(content)) return chunks;
            
            int chunkSize = 2500; // Roughly 500-600 tokens, optimized for embedding models
            for (int i = 0; i < content.Length; i += chunkSize)
            {
                chunks.Add(content.Substring(i, Math.Min(chunkSize, content.Length - i)));
            }
            return chunks;
        }

        private double CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length || v1.Length == 0) return 0;
            double dot = 0, m1 = 0, m2 = 0;
            for (int i = 0; i < v1.Length; i++) { dot += (double)v1[i] * v2[i]; m1 += (double)v1[i] * v1[i]; m2 += (double)v2[i] * v2[i]; }
            double denom = Math.Sqrt(m1) * Math.Sqrt(m2);
            if (denom == 0) return 0; // Prevent NaN from zero-magnitude vectors
            return dot / denom;
        }

        private double CalculateKeywordScore(string query, string content)
        {
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(query)) return 0;
            
            var queryWords = query.ToLower().Split(new[] { ' ', '?', '.', ',', ':', ';', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(w => w.Length > 2).ToList(); 
            
            if (queryWords.Count == 0) return 0;

            string contentLower = content.ToLower();
            double score = 0;
            
            foreach (var word in queryWords)
            {
                int index = 0;
                int count = 0;
                while ((index = contentLower.IndexOf(word, index)) != -1)
                {
                    count++;
                    index += word.Length;
                    if (count >= 50) break; // Cap to prevent O(n²) on pathological inputs
                }
                
                if (count > 0)
                {
                    score += (1.0 + Math.Log(count)) * 0.15; 
                }
            }
            return score;
        }

        private async Task SaveIndexAsync(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return;
                string dir = Path.Combine(root, ".localpilot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "index.json");

                // Serialize to a temp file first, then replace atomically to prevent corruption
                string tempPath = path + ".tmp";
                using (var sw = new StreamWriter(tempPath))
                using (var jw = new Newtonsoft.Json.JsonTextWriter(sw))
                {
                    var serializer = new Newtonsoft.Json.JsonSerializer();
                    // Snapshot the dictionary to prevent collection-modified during serialization
                    var snapshot = _index.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    serializer.Serialize(jw, snapshot);
                }
                // Atomic replace
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
                _lastDiskSaveTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] SaveIndexAsync failed (non-fatal)", ex);
            }
        }

        private async Task LoadIndexAsync(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return;
                string path = Path.Combine(root, ".localpilot", "index.json");
                if (!File.Exists(path)) return;

                // Read in background to avoid blocking
                string json = await Task.Run(() => File.ReadAllText(path));
                if (string.IsNullOrWhiteSpace(json)) return;

                // Try to parse as the new Dictionary format first, then fall back to old List format
                try
                {
                    var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<CodeChunk>>>(json);
                    if (loaded != null)
                    {
                        _index.Clear();
                        foreach (var kvp in loaded)
                        {
                            if (kvp.Value != null)
                                _index[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch
                {
                    // Backwards compatibility: try old List<CodeChunk> format
                    try
                    {
                        var loadedList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CodeChunk>>(json);
                        if (loadedList != null)
                        {
                            _index.Clear();
                            foreach (var group in loadedList.GroupBy(c => c.FilePath))
                            {
                                _index[group.Key] = group.ToList();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Corrupt index file — delete it and start fresh
                        LocalPilotLogger.LogError("[RAG] Index file is corrupted. Deleting and reindexing.", ex);
                        try { File.Delete(path); } catch { }
                    }
                }

                _lastIndexTime = File.GetLastWriteTime(path);
                _lastDiskSaveTime = _lastIndexTime;
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RAG] LoadIndexAsync failed (non-fatal)", ex);
            }
        }
    }
}
