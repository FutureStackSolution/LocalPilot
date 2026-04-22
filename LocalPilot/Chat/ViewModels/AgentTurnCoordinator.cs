using LocalPilot.Models;
using LocalPilot.Services;

namespace LocalPilot.Chat.ViewModels
{
    /// <summary>
    /// Owns chat-session state transitions for agent turn lifecycle.
    /// Keeps status and streaming transitions out of code-behind.
    /// </summary>
    public sealed class AgentTurnCoordinator
    {
        public AgentStatusViewState BuildStatusState(string modelName, AgentStatus status, string detail, string action = null)
        {
            string statusString = status.ToString();
            if (status == AgentStatus.Idle) statusString = "Cancelled";
            else if (status == AgentStatus.Thinking) 
            {
                if (!string.IsNullOrEmpty(action))
                {
                    statusString = action.ToLowerInvariant() switch
                    {
                        "explain" => "Explaining",
                        "refactor" => "Refactoring",
                        "document" => "Documenting",
                        "test"     => "Generating Tests",
                        "review"   => "Reviewing",
                        "fix"      => "Fixing Build",
                        _          => "Thinking"
                    };
                }
                else
                {
                    statusString = "Thinking";
                }
            }
            else if (status == AgentStatus.Executing) statusString = "Executing";
            else if (status == AgentStatus.Completed) statusString = "Completed";

            var lastMetric = PerformanceTracer.Instance.GetLastMetric();

            return new AgentStatusViewState
            {
                Status = status,
                HeaderText = $"LocalPilot ({modelName}) - {statusString}",
                DetailText = detail ?? string.Empty,
                IsTerminal = status == AgentStatus.Completed || status == AgentStatus.Failed || status == AgentStatus.Idle,
                IsCompletion = status == AgentStatus.Completed,
                IsFailure = status == AgentStatus.Failed,
                IsCancelled = status == AgentStatus.Idle,
                LatencyMs = lastMetric?.DurationMs ?? 0,
                TokenCount = lastMetric?.TokenCount ?? 0
            };
        }

        public StreamingViewState BuildStreamingState(bool isStreaming, string modelName)
        {
            return new StreamingViewState
            {
                IsStreaming = isStreaming,
                IsInputEnabled = true,
                InputOpacity = 1.0,
                ShowStatusBar = isStreaming,
                StatusText = $"Local({modelName}) thinking",
                DetailText = "Autonomous logic active"
            };
        }
    }

    public sealed class AgentStatusViewState
    {
        public AgentStatus Status { get; set; }
        public string HeaderText { get; set; } = string.Empty;
        public string DetailText { get; set; } = string.Empty;
        public bool IsTerminal { get; set; }
        public bool IsCompletion { get; set; }
        public bool IsFailure { get; set; }
        public bool IsCancelled { get; set; }
        public long LatencyMs { get; set; }
        public int TokenCount { get; set; }
    }

    public sealed class StreamingViewState
    {
        public bool IsStreaming { get; set; }
        public bool IsInputEnabled { get; set; }
        public double InputOpacity { get; set; }
        public bool ShowStatusBar { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string DetailText { get; set; } = string.Empty;
    }
}
