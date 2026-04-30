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
    /// Optimized with Parallel Incremental Indexing and Memory-Efficient Differential Sync.
    /// </summary>
    public class ProjectContextService
    {
        private static readonly ProjectContextService _instance = new ProjectContextService();
        public static ProjectContextService Instance => _instance;

        private readonly List<CodeChunk> _index = new List<CodeChunk>();
        private readonly SemaphoreSlim _indexLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _parallelLock = new SemaphoreSlim(5, 5); 
        private DateTime _lastIndexTime = DateTime.MinValue;
        private DateTime _lastDiskSaveTime = DateTime.MinValue;
        
        private string _solutionRoot = string.Empty;
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, byte> _pendingFiles = new ConcurrentDictionary<string, byte>();
        private CancellationTokenSource _watcherCts;

        private ProjectContextService() { }

        /// <summary>
        /// 🚀 HIGH PERFORMANCE: Parallel differential indexing of the solution.
        /// </summary>
        public async Task IndexSolutionAsync(OllamaService ollama, CancellationToken ct = default)
        {
            if (!await _indexLock.WaitAsync(0)) return;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;
                var currentRoot = Path.GetDirectoryName(solution.FullPath);
                if (_solutionRoot != currentRoot)
                {
                    LocalPilotLogger.Log($"[RAG] Solution changed. Clearing index for {currentRoot}", LogCategory.Agent);
                    _index.Clear();
                    _solutionRoot = currentRoot;
                    _lastIndexTime = DateTime.MinValue;
                }

                if (_index.Count == 0) await LoadIndexAsync(_solutionRoot);

                LocalPilotLogger.Log("[RAG] Starting parallel differential sync...", LogCategory.Agent);
                
                // 1. Collect all relevant files
                var allFiles = Directory.EnumerateFiles(_solutionRoot, "*.*", SearchOption.AllDirectories)
                    .Where(IsRelevantFile).ToList();

                // 2. Filter for changed files
                var filesToUpdate = allFiles.Where(f => {
                    var info = new FileInfo(f);
                    var existing = _index.FirstOrDefault(c => c.FilePath == GetRelativePath(f));
                    return existing == null || info.LastWriteTime > existing.LastModified;
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
                    _lastIndexTime = DateTime.Now;
                    await SaveIndexAsync(_solutionRoot);
                }

                SetupIncrementalWatcher(ollama);
                LocalPilotLogger.Log($"[RAG] Sync complete. {_index.Count} chunks ready.");
            }
            finally { _indexLock.Release(); }
        }

        private async Task ParallelUpdateAsync(List<string> files, OllamaService ollama, CancellationToken ct)
        {
            // 🚀 CONTROLLED PARALLELISM: Process files using user-configured concurrency to prevent overwhelming the local Ollama server
            int concurrency = Math.Max(1, LocalPilotSettings.Instance.BackgroundIndexingConcurrency);
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

                    ct.ThrowIfCancellationRequested(); // Check for Agent activity
                    await semaphore.WaitAsync(ct);
                    
                    tasks.Add(Task.Run(async () => 
                    {
                        try
                        {
                            await ProcessFileAsync(file, GetRelativePath(file), ollama, ct);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }
        }

        private void SetupIncrementalWatcher(OllamaService ollama)
        {
            if (_watcher != null) return;
            _watcherCts = new CancellationTokenSource();
            _watcher = new FileSystemWatcher(_solutionRoot) { IncludeSubdirectories = true, Filter = "*.*", EnableRaisingEvents = true };
            
            _watcher.Changed += (s, e) => { if (IsRelevantFile(e.FullPath)) _pendingFiles.TryAdd(e.FullPath, 0); };
            
            // Start background consumer for incremental RAG
            _ = Task.Run(async () => {
                while (!_watcherCts.Token.IsCancellationRequested)
                {
                    if (GlobalPriorityGuard.ShouldYield() || _pendingFiles.IsEmpty)
                    {
                        if (GlobalPriorityGuard.ShouldYield() && !_pendingFiles.IsEmpty)
                        {
                            LocalPilotLogger.Log("[RAG] Priority Yield: Pausing brain sync while Agent is active...", LogCategory.Agent);
                        }
                        await Task.Delay(5000); // Deep Sleep during activity
                        continue;
                    }

                    if (!_pendingFiles.IsEmpty)
                    {
                        await Task.Delay(5000); // Debounce RAG updates (more expensive than Nexus)
                        var batch = _pendingFiles.Keys.ToList();
                        _pendingFiles.Clear();
                        
                        await _indexLock.WaitAsync();
                        try 
                        { 
                            // 🚀 LINKED CANCELLATION: Abort if agent starts OR if watcher stops
                            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_watcherCts.Token, GlobalPriorityGuard.YieldToken))
                            {
                                await ParallelUpdateAsync(batch, ollama, linkedCts.Token); 
                                await SaveIndexAsync(_solutionRoot); 
                            }
                        }
                        catch (OperationCanceledException) { LocalPilotLogger.Log("[RAG] Background sync yielded to agent."); }
                        finally { _indexLock.Release(); }
                    }
                    await Task.Delay(2000);
                }
            });
        }

        private string GetRelativePath(string fullPath) => fullPath.Replace(_solutionRoot, "").TrimStart(Path.DirectorySeparatorChar);

        private async Task ProcessFileAsync(string fullPath, string relativePath, OllamaService ollama, CancellationToken ct)
        {
            try
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > 256000) return; // Cap RAG at 256KB for performance

                string content = await Task.Run(() => File.ReadAllText(fullPath));
                if (string.IsNullOrWhiteSpace(content)) return;

                var chunks = ChunkContent(fullPath, content);
                var newChunks = new List<CodeChunk>();

                foreach (var chunkText in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    await _parallelLock.WaitAsync(ct);
                    try
                    {
                        float[] vector = null;
                        if (!string.IsNullOrWhiteSpace(LocalPilotSettings.Instance.EmbeddingModel))
                        {
                            vector = await ollama.GetEmbeddingsAsync(LocalPilotSettings.Instance.EmbeddingModel, chunkText, ct);
                        }
                        
                        // Add chunk even if vector is null (enables keyword search fallback)
                        newChunks.Add(new CodeChunk { FilePath = relativePath, Content = chunkText, Vector = vector, LastModified = fileInfo.LastWriteTime });
                    }
                    finally { _parallelLock.Release(); }
                }

                if (newChunks.Any())
                {
                    lock (_index) { _index.RemoveAll(c => c.FilePath == relativePath); _index.AddRange(newChunks); }
                }
            }
            catch { }
        }

        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5)
        {
            if (string.IsNullOrWhiteSpace(query) || _index.Count == 0) return string.Empty;

            float[] queryVector = null;
            if (!string.IsNullOrWhiteSpace(LocalPilotSettings.Instance.EmbeddingModel) && !ollama.CircuitBreakerTripped)
            {
                queryVector = await ollama.GetEmbeddingsAsync(LocalPilotSettings.Instance.EmbeddingModel, query);
            }

            var results = _index
                .Select(c => 
                {
                    double score = 0;
                    // Boost if filename matches
                    if (query.IndexOf(Path.GetFileNameWithoutExtension(c.FilePath), StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 0.3;

                    if (queryVector != null && c.Vector != null)
                    {
                        // Primary: Semantic Vector Search
                        score += CosineSimilarity(queryVector, c.Vector);
                    }
                    else
                    {
                        // Fallback: Lexical Keyword Search
                        score += CalculateKeywordScore(query, c.Content);
                    }
                    return new { Chunk = c, Score = score };
                })
                .Where(r => r.Score > 0.1) // Lower threshold to allow keyword matches
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

        private bool IsRelevantFile(string path)
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
            if (path.Contains("\\.localpilot\\") || path.Contains("\\obj\\") || path.Contains("\\bin\\") || path.Contains("\\node_modules\\") || path.Contains("\\dist\\")) return false;
            return allowed.Contains(ext);
        }

        private List<string> ChunkContent(string path, string content)
        {
            var chunks = new List<string>();
            int chunkSize = 2500; // Roughly 500-600 tokens, optimized for embedding models
            for (int i = 0; i < content.Length; i += chunkSize)
            {
                chunks.Add(content.Substring(i, Math.Min(chunkSize, content.Length - i)));
            }
            return chunks;
        }

        private double CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0;
            double dot = 0, m1 = 0, m2 = 0;
            for (int i = 0; i < v1.Length; i++) { dot += (double)v1[i] * v2[i]; m1 += (double)v1[i] * v1[i]; m2 += (double)v2[i] * v2[i]; }
            return dot / (Math.Sqrt(m1) * Math.Sqrt(m2));
        }

        private double CalculateKeywordScore(string query, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return 0;
            
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
                }
                
                if (count > 0)
                {
                    // Logarithmic term frequency scaling
                    score += (1.0 + Math.Log(count)) * 0.15; 
                }
            }
            return score;
        }

        private async Task SaveIndexAsync(string root) { try { string dir = Path.Combine(root, ".localpilot"); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "index.json"), Newtonsoft.Json.JsonConvert.SerializeObject(_index)); _lastDiskSaveTime = DateTime.Now; } catch { } }
        private async Task LoadIndexAsync(string root) { try { string path = Path.Combine(root, ".localpilot", "index.json"); if (File.Exists(path)) { var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CodeChunk>>(File.ReadAllText(path)); if (loaded != null) { _index.AddRange(loaded); _lastIndexTime = File.GetLastWriteTime(path); _lastDiskSaveTime = _lastIndexTime; } } } catch { } }
    }
}
