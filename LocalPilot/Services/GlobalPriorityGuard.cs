using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Coordinates resource priority across LocalPilot services.
    /// Allows the Agentic Loop to signal background tasks (RAG indexing, Nexus syncing)
    /// to yield CPU and GPU resources during active user conversations.
    /// </summary>
    public static class GlobalPriorityGuard
    {
        private static volatile bool _isAgentActive = false;
        private static DateTime _lastActiveTime = DateTime.MinValue;
        private static readonly object _lock = new object();
        private static CancellationTokenSource _yieldCts = new CancellationTokenSource();

        /// <summary>
        /// A token that is cancelled when an agent turn starts.
        /// Background tasks should use this to abort immediately.
        /// Thread-safe: reads are protected by a snapshot of the CTS reference.
        /// </summary>
        public static CancellationToken YieldToken
        {
            get
            {
                lock (_lock)
                {
                    return _yieldCts.Token;
                }
            }
        }

        /// <summary>
        /// Signal that the agent is starting an intensive task.
        /// </summary>
        public static void StartAgentTurn()
        {
            _isAgentActive = true;
            _lastActiveTime = DateTime.Now;
            
            lock (_lock)
            {
                // Cancel the old CTS and dispose it safely
                try { _yieldCts.Cancel(); } catch { }
                var oldCts = _yieldCts;
                _yieldCts = new CancellationTokenSource();
                
                // 🚀 EXPERT: Dispose old CTS in the background to avoid blocking and potential race
                _ = Task.Run(async () => {
                    try { await Task.Delay(100); oldCts.Dispose(); } catch { }
                });
            }
        }

        /// <summary>
        /// Signal that the agent has finished its task.
        /// </summary>
        public static void EndAgentTurn()
        {
            _isAgentActive = false;
        }

        /// <summary>
        /// Background services should check this before starting heavy work.
        /// Returns true if background work should be paused or yielded.
        /// </summary>
        public static bool ShouldYield()
        {
            if (_isAgentActive) return true;

            // Smart cooldown: yield for 30s after any agent activity
            if ((DateTime.Now - _lastActiveTime).TotalSeconds < 30) return true;

            return false;
        }
    }
}
