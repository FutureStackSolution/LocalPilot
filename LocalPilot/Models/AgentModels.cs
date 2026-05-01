using Newtonsoft.Json;
using System.Collections.Generic;

namespace LocalPilot.Models
{
    /// <summary>
    /// Represents a tool available for the Agent to use.
    /// </summary>
    public class AgentTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public object Parameters { get; set; } // JSON Schema or similar
    }

    /// <summary>
    /// Represents a tool call request from the LLM.
    /// </summary>
    public class ToolCallRequest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("arguments")]
        public Dictionary<string, object> Arguments { get; set; }
    }

    /// <summary>
    /// Represents the result of a tool execution.
    /// </summary>
    public class ToolResponse
    {
        public string ToolCallId { get; set; }
        public string Output { get; set; }
        public bool IsError { get; set; }
    }

    /// <summary>
    /// Represents an agent's "Thought" vs "Action" status.
    /// </summary>
    public enum AgentStatus
    {
        Thinking,
        Planning,
        ActionPending,
        Executing,
        Completed,
        Failed,
        Idle
    }


    /// <summary>
    /// Represents a found symbol location.
    /// </summary>
    public class SymbolLocation
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Kind { get; set; }
    }
}
