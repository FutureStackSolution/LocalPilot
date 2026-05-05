using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Fires a task and forgets about it, but ensures any exceptions are logged
        /// to prevent the process from crashing and to maintain observability.
        /// </summary>
        public static void FireAndForget(this Task task)
        {
            if (task == null) return;

            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    foreach (var ex in t.Exception.Flatten().InnerExceptions)
                    {
                        LocalPilotLogger.LogError("[AsyncGuard] Background task failed", ex);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
