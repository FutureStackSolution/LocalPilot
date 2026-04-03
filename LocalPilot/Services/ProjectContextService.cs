using Microsoft.VisualStudio.Shell;
using System;
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
        public float[] Vector { get; set; }
    }

    /// <summary>
    /// Local RAG Service: Indexes the Visual Studio solution and provides semantic search.
    /// </summary>
    public class ProjectContextService
    {
        private static readonly ProjectContextService _instance = new ProjectContextService();
        public static ProjectContextService Instance => _instance;

        private readonly List<CodeChunk> _index = new List<CodeChunk>();
        private readonly SemaphoreSlim _indexLock = new SemaphoreSlim(1, 1);
        private DateTime _lastIndexTime = DateTime.MinValue;

        private ProjectContextService() { }

        /// <summary>
        /// Scans the entire solution and generates semantic embeddings for all code files.
        /// Throttled to avoid overwhelming the local Ollama instance.
        /// </summary>
        public async Task IndexSolutionAsync(OllamaService ollama, CancellationToken ct = default)
        {
            if (!await _indexLock.WaitAsync(0)) return;
            try
            {
                LocalPilotLogger.Log("[Index] Starting DTE-based background scan...");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                // 🚦 Professional Status Bar Feedback
                await VS.StatusBar.ShowMessageAsync("LocalPilot: Indexing solution context...");

                var dte = Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE.DTE;
                if (dte == null || dte.Solution == null) return;

                _index.Clear();
                foreach (global::EnvDTE.Project project in dte.Solution.Projects)
                {
                    if (project == null) continue;
                    await ScanProjectItemsAsync(project.ProjectItems, ollama, ct);
                }

                _lastIndexTime = DateTime.Now;
                LocalPilotLogger.Log($"[Index] Finished! Solution indexed with {_index.Count} semantic chunks.");
                
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.StatusBar.ShowMessageAsync("LocalPilot: Indexing complete! Semantic memory active.");
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Index] Failed to process solution context (DTE mode)", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.StatusBar.ShowMessageAsync("LocalPilot: Indexing failed. Check logs.");
            }
            finally
            {
                _indexLock.Release();
            }
        }

        private async Task ScanProjectItemsAsync(global::EnvDTE.ProjectItems items, OllamaService ollama, CancellationToken ct)
        {
            if (items == null) return;
            foreach (global::EnvDTE.ProjectItem item in items)
            {
                if (ct.IsCancellationRequested) break;

                // 1. Recurse into folders / projects
                if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                {
                    await ScanProjectItemsAsync(item.ProjectItems, ollama, ct);
                }

                // 2. Process Files (Efficiently)
                string fullPath = string.Empty;
                try { fullPath = item.FileNames[1]; } catch { continue; }

                if (string.IsNullOrEmpty(fullPath) || !IsRelevantFile(fullPath)) continue;

                try
                {
                    var fileInfo = new FileInfo(fullPath);
                    if (fileInfo.Length > 512000) continue; // Skip files > 500KB to save memory/CPU

                    string content = File.ReadAllText(fullPath);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var chunks = ChunkContent(fullPath, content);
                    foreach (var chunkText in chunks)
                    {
                        var vector = await ollama.GetEmbeddingsAsync(LocalPilotSettings.Instance.ChatModel, chunkText, ct);
                        if (vector != null)
                        {
                            _index.Add(new CodeChunk { FilePath = Path.GetFileName(fullPath), Content = chunkText, Vector = vector });
                        }
                        
                        // Adaptive Throttling: Let the CPU breathe between AI requests
                        await Task.Delay(100, ct); 
                    }
                }
                catch { /* Silent skip for stability */ }
            }
        }

        /// <summary>
        /// Searches the indexed solution for the top semantic matches to a user query.
        /// </summary>
        public async Task<string> SearchContextAsync(OllamaService ollama, string query, int topN = 5)
        {
            var queryVector = await ollama.GetEmbeddingsAsync(LocalPilotSettings.Instance.ChatModel, query);
            if (queryVector == null || _index.Count == 0) return string.Empty;

            // 1. Semantic Similarity Match
            var results = _index
                .Select(c => 
                {
                    double score = CosineSimilarity(queryVector, c.Vector);
                    
                    // Boost score if filename is explicitly mentioned in the query
                    if (query.IndexOf(c.FilePath, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 0.3; 
                        
                    return new { Chunk = c, Score = score };
                })
                .OrderByDescending(r => r.Score)
                .Take(topN)
                .ToList();

            if (!results.Any()) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\n--- PROJECT_SOURCE_CONTEXT ---");
            sb.AppendLine("Use these actual file snippets from the current solution to build your answer:");
            foreach (var r in results)
            {
                if (r.Score < 0.45) continue; // Noise threshold
                sb.AppendLine($"<file path=\"{r.Chunk.FilePath}\">");
                sb.AppendLine(r.Chunk.Content);
                sb.AppendLine("</file>");
            }
            sb.AppendLine("--- END_SOURCE_CONTEXT ---");
            return sb.ToString();
        }

        private bool IsRelevantFile(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            string[] allowed = { ".cs", ".xaml", ".json", ".xml", ".css", ".js", ".md" };
            return allowed.Contains(ext) && !path.Contains("\\obj\\") && !path.Contains("\\bin\\");
        }

        private List<string> ChunkContent(string path, string content)
        {
            // Lightweight 50-line chunking for efficiency
            var lines = content.Split('\n');
            var chunks = new List<string>();
            for (int i = 0; i < lines.Length; i += 50)
            {
                chunks.Add(string.Join("\n", lines.Skip(i).Take(50)));
            }
            return chunks;
        }

        private double CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1.Length != v2.Length) return 0;
            double dot = 0, m1 = 0, m2 = 0;
            for (int i = 0; i < v1.Length; i++)
            {
                dot += (double)v1[i] * v2[i];
                m1 += (double)v1[i] * v1[i];
                m2 += (double)v2[i] * v2[i];
            }
            return dot / (Math.Sqrt(m1) * Math.Sqrt(m2));
        }
    }
}
