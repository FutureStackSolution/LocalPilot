using LocalPilot.Models;

namespace LocalPilot.Chat.ViewModels
{
    /// <summary>
    /// Owns chat-session state transitions for agent turn lifecycle.
    /// Keeps status and streaming transitions out of code-behind.
    /// </summary>
    public sealed class AgentTurnCoordinator
    {
        public AgentStatusViewState BuildStatusState(string modelName, AgentStatus status, string detail)
        {
            return new AgentStatusViewState
            {
                Status = status,
                HeaderText = $"LocalPilot ({modelName}) {status}",
                DetailText = detail ?? string.Empty,
                IsTerminal = status == AgentStatus.Completed || status == AgentStatus.Failed || status == AgentStatus.Idle,
                IsCompletion = status == AgentStatus.Completed,
                IsFailure = status == AgentStatus.Failed,
                IsCancelled = status == AgentStatus.Idle
            };
        }

        public StreamingViewState BuildStreamingState(bool isStreaming, string modelName)
        {
            return new StreamingViewState
            {
                IsStreaming = isStreaming,
                IsInputEnabled = !isStreaming,
                InputOpacity = isStreaming ? 0.6 : 1.0,
                ShowStatusBar = isStreaming,
                StatusText = $"LocalPilot ({modelName}) working",
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
