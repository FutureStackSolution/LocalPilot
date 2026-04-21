using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LocalPilot.Services
{
    public class PerformanceMetrics
    {
        public string TaskId { get; set; }
        public int TurnNumber { get; set; }
        public long DurationMs { get; set; }
        public int TokenCount { get; set; } // Estimated
        public string ModelName { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Tracks AI performance metrics to provide 'Elite' visibility into local inference latency.
    /// </summary>
    public class PerformanceTracer
    {
        private static readonly Lazy<PerformanceTracer> _instance = new Lazy<PerformanceTracer>(() => new PerformanceTracer());
        public static PerformanceTracer Instance => _instance.Value;

        private readonly ConcurrentQueue<PerformanceMetrics> _history = new ConcurrentQueue<PerformanceMetrics>();

        public void RecordTurn(string taskId, int turn, long ms, int tokens, string model)
        {
            var metric = new PerformanceMetrics
            {
                TaskId = taskId,
                TurnNumber = turn,
                DurationMs = ms,
                TokenCount = tokens,
                ModelName = model
            };
            _history.Enqueue(metric);
            
            LocalPilotLogger.Log($"[Performance] Turn {turn} completed in {ms}ms ({tokens} tokens) using {model}");
        }

        public PerformanceMetrics GetLastMetric()
        {
            return _history.LastOrDefault();
        }

        public double GetAverageLatency()
        {
            if (_history.IsEmpty) return 0;
            return _history.Average(m => m.DurationMs);
        }
    }
}
