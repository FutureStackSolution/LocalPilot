using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Stores pre-turn file snapshots so any agent turn can be reverted in one click,
    /// including restoring closed files and deleting newly created files.
    /// </summary>
    public sealed class TurnCheckpointService
    {
        public static TurnCheckpointService Instance { get; } = new TurnCheckpointService();

        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _checkpoints
            = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private TurnCheckpointService() { }

        /// <summary>
        /// Saves a snapshot of original file contents for a completed turn and returns its checkpoint ID.
        /// A null original value indicates the file did not exist prior to the turn.
        /// </summary>
        public string SaveCheckpoint(Dictionary<string, (string original, string improved)> stagedChanges)
        {
            if (stagedChanges == null || stagedChanges.Count == 0) return null;

            string id = Guid.NewGuid().ToString("N");
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in stagedChanges)
            {
                snapshot[kvp.Key] = kvp.Value.original;
            }

            _checkpoints[id] = snapshot;
            return id;
        }

        public bool HasCheckpoint(string checkpointId) =>
            !string.IsNullOrEmpty(checkpointId) && _checkpoints.ContainsKey(checkpointId);

        /// <summary>
        /// Reverts all files captured in the given checkpoint, clears inline diffs for those files,
        /// and synchronizes Roslyn.
        /// </summary>
        public async Task<int> RevertCheckpointAsync(string checkpointId)
        {
            if (string.IsNullOrEmpty(checkpointId) || !_checkpoints.TryRemove(checkpointId, out var snapshot))
                return 0;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            int revertedCount = 0;

            foreach (var kvp in snapshot)
            {
                string path = kvp.Key;
                string original = kvp.Value;

                try
                {
                    if (original == null)
                    {
                        // File was newly created during this turn
                        if (File.Exists(path)) File.Delete(path);
                    }
                    else
                    {
                        var docView = await VS.Documents.GetDocumentViewAsync(path);
                        if (docView?.TextBuffer != null)
                        {
                            using (var edit = docView.TextBuffer.CreateEdit())
                            {
                                edit.Replace(0, docView.TextBuffer.CurrentSnapshot.Length, original);
                                edit.Apply();
                            }
                        }
                        else
                        {
                            File.WriteAllText(path, original);
                        }

                        var roslyn = SymbolIndexService.Instance.GetRoslynProvider();
                        if (roslyn != null) await roslyn.SynchronizeDocumentAsync(path);
                    }

                    InlineDiffService.Instance.ClearFile(path);
                    revertedCount++;
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError($"[Checkpoint] Failed to revert {path}", ex);
                }
            }

            return revertedCount;
        }
    }
}
