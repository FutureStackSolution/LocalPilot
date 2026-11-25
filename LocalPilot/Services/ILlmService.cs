using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Unified contract for local and remote Large Language Model providers
    /// (e.g. native Ollama, OpenAI, LM Studio, vLLM, llama-server, OpenRouter, Groq).
    /// </summary>
    public interface ILlmService
    {
        bool CircuitBreakerTripped { get; }
        void UpdateBaseUrl(string baseUrl);
        Task<bool> IsAvailableAsync(CancellationToken ct = default);
        Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default);
        Task WarmupAsync(string model, CancellationToken ct = default);
        Task<VramStatus> GetVramStatusAsync(CancellationToken ct = default);
        IAsyncEnumerable<string> StreamCompletionAsync(string model, string prompt, OllamaOptions options = null, CancellationToken ct = default);
        IAsyncEnumerable<string> StreamChatAsync(string model, List<ChatMessage> messages, OllamaOptions options = null, CancellationToken ct = default);
        IAsyncEnumerable<ChatStreamResult> StreamChatWithToolsAsync(string model, List<ChatMessage> messages, List<OllamaToolDefinition> tools = null, OllamaOptions options = null, CancellationToken ct = default);
        Task<string> ChatAsync(string model, List<ChatMessage> messages, OllamaOptions options = null, CancellationToken ct = default);
        Task<float[]> GetEmbeddingsAsync(string model, string prompt, CancellationToken ct = default);
        Task<List<float[]>> GetEmbeddingsBatchAsync(string model, List<string> prompts, CancellationToken ct = default);
        Task<int> UnloadAllModelsAsync(CancellationToken ct = default);
    }
}
