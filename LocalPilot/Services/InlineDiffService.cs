using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    public sealed class DiffHunk
    {
        public string Id { get; set; }
        public string FilePath { get; set; }
        public int StartLine { get; set; }          // 0-based start line in current file
        public int EndLineExclusive { get; set; }   // 0-based exclusive end line in current file
        public List<string> RemovedLines { get; set; } = new List<string>();
        public int AddedLineCount => Math.Max(0, EndLineExclusive - StartLine);
    }

    /// <summary>
    /// Computes line-level diff hunks for staged agent edits and coordinates
    /// per-hunk Accept / Reject actions with inline editor adornments.
    /// </summary>
    public sealed class InlineDiffService
    {
        public static InlineDiffService Instance { get; } = new InlineDiffService();

        private readonly ConcurrentDictionary<string, List<DiffHunk>> _hunksByFile
            = new ConcurrentDictionary<string, List<DiffHunk>>(StringComparer.OrdinalIgnoreCase);

        public event Action<string> OnDiffsChanged;

        private InlineDiffService() { }

        public void SetFileDiff(string filePath, string original, string improved)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var hunks = ComputeHunks(filePath, original ?? "", improved ?? "");
            if (hunks.Count == 0)
            {
                _hunksByFile.TryRemove(filePath, out _);
            }
            else
            {
                _hunksByFile[filePath] = hunks;
            }
            OnDiffsChanged?.Invoke(filePath);
        }

        public IReadOnlyList<DiffHunk> GetHunks(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return Array.Empty<DiffHunk>();
            if (_hunksByFile.TryGetValue(filePath, out var list))
            {
                lock (list) return list.ToList();
            }
            return Array.Empty<DiffHunk>();
        }

        public void ClearFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            if (_hunksByFile.TryRemove(filePath, out _))
            {
                OnDiffsChanged?.Invoke(filePath);
            }
        }

        public void ClearAll()
        {
            var keys = _hunksByFile.Keys.ToList();
            _hunksByFile.Clear();
            foreach (var k in keys) OnDiffsChanged?.Invoke(k);
        }

        public void AcceptHunk(string filePath, string hunkId)
        {
            if (string.IsNullOrEmpty(filePath) || !_hunksByFile.TryGetValue(filePath, out var list)) return;
            lock (list)
            {
                list.RemoveAll(h => h.Id == hunkId);
                if (list.Count == 0) _hunksByFile.TryRemove(filePath, out _);
            }
            OnDiffsChanged?.Invoke(filePath);
        }

        public async Task RejectHunkAsync(string filePath, string hunkId)
        {
            if (string.IsNullOrEmpty(filePath) || !_hunksByFile.TryGetValue(filePath, out var list)) return;

            DiffHunk target = null;
            lock (list)
            {
                target = list.FirstOrDefault(h => h.Id == hunkId);
            }
            if (target == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var docView = await VS.Documents.GetDocumentViewAsync(filePath);
                string currentText = docView?.TextBuffer != null
                    ? docView.TextBuffer.CurrentSnapshot.GetText()
                    : (File.Exists(filePath) ? File.ReadAllText(filePath) : "");

                string sep = currentText.Contains("\r\n") ? "\r\n" : "\n";
                var lines = currentText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

                int start = Math.Max(0, Math.Min(target.StartLine, lines.Count));
                int removeCount = Math.Max(0, Math.Min(target.EndLineExclusive - start, lines.Count - start));
                if (removeCount > 0) lines.RemoveRange(start, removeCount);
                if (target.RemovedLines.Count > 0) lines.InsertRange(start, target.RemovedLines);

                string updated = string.Join(sep, lines);
                if (docView?.TextBuffer != null)
                {
                    using (var edit = docView.TextBuffer.CreateEdit())
                    {
                        edit.Replace(0, docView.TextBuffer.CurrentSnapshot.Length, updated);
                        edit.Apply();
                    }
                }
                else
                {
                    File.WriteAllText(filePath, updated);
                }

                var roslyn = SymbolIndexService.Instance.GetRoslynProvider();
                if (roslyn != null) await roslyn.SynchronizeDocumentAsync(filePath);

                int delta = target.RemovedLines.Count - removeCount;
                lock (list)
                {
                    list.RemoveAll(h => h.Id == hunkId);
                    foreach (var h in list)
                    {
                        if (h.StartLine >= target.EndLineExclusive)
                        {
                            h.StartLine += delta;
                            h.EndLineExclusive += delta;
                        }
                    }
                    if (list.Count == 0) _hunksByFile.TryRemove(filePath, out _);
                }

                OnDiffsChanged?.Invoke(filePath);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[InlineDiff] Failed to reject hunk in {filePath}", ex);
            }
        }

        private static List<DiffHunk> ComputeHunks(string filePath, string original, string improved)
        {
            var oldLines = original.Replace("\r\n", "\n").Split('\n');
            var newLines = improved.Replace("\r\n", "\n").Split('\n');

            // Fast LCS for files up to 2000 lines; fallback to single-span diff for huge files
            if (oldLines.Length * newLines.Length > 2_000_000)
            {
                return new List<DiffHunk>
                {
                    new DiffHunk
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FilePath = filePath,
                        StartLine = 0,
                        EndLineExclusive = newLines.Length,
                        RemovedLines = oldLines.ToList()
                    }
                };
            }

            int m = oldLines.Length, n = newLines.Length;
            var dp = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
            {
                for (int j = n - 1; j >= 0; j--)
                {
                    dp[i, j] = oldLines[i] == newLines[j]
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var hunks = new List<DiffHunk>();
            DiffHunk current = null;
            int oi = 0, ni = 0;

            while (oi < m || ni < n)
            {
                if (oi < m && ni < n && oldLines[oi] == newLines[ni])
                {
                    if (current != null)
                    {
                        hunks.Add(current);
                        current = null;
                    }
                    oi++;
                    ni++;
                }
                else
                {
                    if (current == null)
                    {
                        current = new DiffHunk
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            FilePath = filePath,
                            StartLine = ni,
                            EndLineExclusive = ni
                        };
                    }

                    if (ni < n && (oi == m || dp[oi, ni + 1] >= dp[oi + 1, ni]))
                    {
                        ni++;
                        current.EndLineExclusive = ni;
                    }
                    else
                    {
                        current.RemovedLines.Add(oldLines[oi]);
                        oi++;
                    }
                }
            }

            if (current != null) hunks.Add(current);
            return hunks;
        }
    }
}
