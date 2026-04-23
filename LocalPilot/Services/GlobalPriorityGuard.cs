using System;

namespace LocalPilot.Services
{
    /// <summary>
    /// Coordinates resource priority across LocalPilot services.
    /// Allows the Agentic Loop to signal background tasks (RAG indexing, Nexus syncing)
    /// to yield CPU and GPU resources during active user conversations.
    /// </summary>
    public static class GlobalPriorityGuard
    {
        private static bool _isAgentActive = false;
        private static DateTime _lastActiveTime = DateTime.MinValue;
        private static System.Threading.CancellationTokenSource _yieldCts = new System.Threading.CancellationTokenSource();

        /// <summary>
        /// A token that is cancelled when an agent turn starts.
        /// Background tasks should use this to abort immediately.
        /// </summary>
        public static System.Threading.CancellationToken YieldToken => _yieldCts.Token;

        /// <summary>
        /// Signal that the agent is starting an intensive task.
        /// </summary>
        public static void StartAgentTurn()
        {
            _isAgentActive = true;
            _lastActiveTime = DateTime.Now;
            
            // 🚀 FORCE CANCELLATION: Tell all background tasks to stop NOW
            _yieldCts.Cancel();
            _yieldCts = new System.Threading.CancellationTokenSource();
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
            // Yield if agent is currently active
            if (_isAgentActive) return true;

            // 🚀 SMART COOLDOWN: Yield for 30s after any agent activity.
            // This prevents the "Brain Sync" from spinning up your fans while you are still reading the explanation.
            if ((DateTime.Now - _lastActiveTime).TotalSeconds < 30) return true;

            return false;
        }
    }
}
