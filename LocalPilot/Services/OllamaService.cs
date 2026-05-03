using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Communicates with a local Ollama instance over HTTP.
    /// Supports streaming completions, chat with native tool calling, and model listing.
    /// </summary>
    public class OllamaService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly HttpClient _backgroundHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private string _baseUrl;

        private volatile bool _circuitBreakerTripped = false;
        public bool CircuitBreakerTripped
        {
            get => _circuitBreakerTripped;
            private set => _circuitBreakerTripped = value;
        }
        private int _consecutiveFailures = 0;

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        }

        public void UpdateBaseUrl(string baseUrl)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        }

        // ── 🚀 World-Class: Background Warmup ─────────────────────────────────
        /// <summary>
        /// Sends a small, non-streaming request to Ollama to ensure the model is loaded 
        /// into memory BEFORE the user interacts with it.
        /// </summary>
        public async Task WarmupAsync(string model, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(model) || CircuitBreakerTripped) return;
            try
            {
                var payload = new { model, prompt = "Hi", stream = false, keep_alive = "10m" };
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, ct).ConfigureAwait(false);
                LocalPilotLogger.Log($"[Ollama] Warmup complete for model: {model}", LogCategory.Ollama);
            }
            catch { /* Best effort only */ }
        }

        // ── Model listing ──────────────────────────────────────────────────────
        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var names = new List<string>();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
                sw.Stop();
                LocalPilotLogger.Log($"[Ollama] Fetching models took {sw.ElapsedMilliseconds}ms. Success: {response.IsSuccessStatusCode}", LogCategory.Ollama);
                if (!response.IsSuccessStatusCode) return names;

                var json = await response.Content.ReadAsStringAsync();
                var obj  = JObject.Parse(json);
                var arr  = obj["models"] as JArray;
                if (arr == null) return names;

                foreach (var m in arr)
                    names.Add(m["name"]?.ToString() ?? string.Empty);
            }
            catch { /* Ollama not running — return empty list */ }
            return names;
        }

        // ── Connectivity check ─────────────────────────────────────────────────
        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    CircuitBreakerTripped = false;
                    _consecutiveFailures = 0;
                }
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Semantic Embeddings ────────────────────────────────────────────────
        private DateTime _circuitBreakerCooldownUntil = DateTime.MinValue;

        private static string ComputeHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                byte[] hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public async Task<float[]> GetEmbeddingsAsync(string model, string prompt, CancellationToken ct = default)
        {
            var results = await GetEmbeddingsBatchAsync(model, new List<string> { prompt }, ct);
            return results != null && results.Count > 0 ? results[0] : null;
        }

        public async Task<List<float[]>> GetEmbeddingsBatchAsync(string model, List<string> prompts, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(model) || prompts == null || prompts.Count == 0) return new List<float[]>();

            if (CircuitBreakerTripped)
            {
                if (DateTime.Now < _circuitBreakerCooldownUntil) return prompts.Select(_ => (float[])null).ToList();
                CircuitBreakerTripped = false;
                System.Threading.Interlocked.Exchange(ref _consecutiveFailures, 0);
            }

            var finalResults = new float[prompts.Count];
            var resultsList = new List<float[]>(new float[prompts.Count][]);
            var missingIndices = new List<int>();
            var missingPrompts = new List<string>();

            // 1. Check Cache
            for (int i = 0; i < prompts.Count; i++)
            {
                string p = prompts[i];
                if (string.IsNullOrWhiteSpace(p)) continue;

                string hash = ComputeHash(p);
                string cacheKey = $"{model}:{hash}";
                var cached = await StorageService.Instance.GetCachedEmbeddingAsync(cacheKey);
                
                if (cached != null)
                {
                    resultsList[i] = cached;
                }
                else
                {
                    missingIndices.Add(i);
                    missingPrompts.Add(p);
                }
            }

            if (missingIndices.Count == 0) return resultsList;

            // 2. Fetch Missing from Ollama
            LocalPilotLogger.Log($"[Storage] Embedding Cache MISS ({missingIndices.Count}/{prompts.Count} chunks) - Requesting from Ollama...", LogCategory.Storage, LogSeverity.Debug);

            try
            {
                // Try /api/embed (Batch supported)
                var payload = new { model, input = missingPrompts, keep_alive = "10m" };
                var body = JsonConvert.SerializeObject(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    requestCts.CancelAfter(TimeSpan.FromSeconds(300)); // 5-minute timeout for batch
                    var response = await _backgroundHttpClient.PostAsync($"{_baseUrl}/api/embed", content, requestCts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var obj = JObject.Parse(json);
                        var embeddings = obj["embeddings"] as JArray;
                        
                        if (embeddings != null && embeddings.Count == missingIndices.Count)
                        {
                            ResetCircuitBreaker();
                            for (int i = 0; i < missingIndices.Count; i++)
                            {
                                var vec = embeddings[i].ToObject<float[]>();
                                resultsList[missingIndices[i]] = vec;
                                
                                // Store in cache
                                if (vec != null)
                                {
                                    string hash = ComputeHash(missingPrompts[i]);
                                    await StorageService.Instance.StoreCachedEmbeddingAsync($"{model}:{hash}", vec);
                                }
                            }
                            return resultsList;
                        }
                    }
                }
            }
            catch { /* Fallback to legacy individual calls if /api/embed fails */ }

            // 3. Fallback to /api/embeddings (Individual calls)
            // This ensures compatibility with older Ollama versions
            for (int i = 0; i < missingIndices.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                
                var prompt = missingPrompts[i];
                var vec = await GetEmbeddingsLegacyAsync(model, prompt, ct);
                resultsList[missingIndices[i]] = vec;

                if (vec != null)
                {
                    string hash = ComputeHash(prompt);
                    await StorageService.Instance.StoreCachedEmbeddingAsync($"{model}:{hash}", vec);
                }
            }

            return resultsList;
        }

        private async Task<float[]> GetEmbeddingsLegacyAsync(string model, string prompt, CancellationToken ct)
        {
            try
            {
                var payload = new { model, prompt, keep_alive = "10m" };
                var body = JsonConvert.SerializeObject(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    requestCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var response = await _backgroundHttpClient.PostAsync($"{_baseUrl}/api/embeddings", content, requestCts.Token).ConfigureAwait(false);
                    
                    if (!response.IsSuccessStatusCode) return null;

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var obj = JObject.Parse(json);
                    var vec = obj["embedding"] as JArray;
                    return vec?.ToObject<float[]>();
                }
            }
            catch { return null; }
        }

        private void HandleFailure()
        {
            int failures = System.Threading.Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= 5)
            {
                CircuitBreakerTripped = true;
                // v1.8 Resilience: Set 5 minute cooldown to prevent hammering a dead service
                _circuitBreakerCooldownUntil = DateTime.Now.AddMinutes(5);
                LocalPilotLogger.Log("[Ollama] CIRCUIT BREAKER TRIPPED. Too many consecutive failures. Cooldown active for 5 mins.", LogCategory.Ollama, LogSeverity.Warning);
            }
        }

        private void ResetCircuitBreaker()
        {
            if (CircuitBreakerTripped)
                LocalPilotLogger.Log("[Ollama] Connection restored. Resetting circuit breaker.", LogCategory.Ollama);
            
            System.Threading.Interlocked.Exchange(ref _consecutiveFailures, 0);
            CircuitBreakerTripped = false;
        }

        // ── Code completion (generate endpoint) ───────────────────────────────
        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            string prompt,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CircuitBreakerTripped)
            {
                yield return "\n[LocalPilot] Ollama is currently unreachable. Please check if Ollama is running.";
                yield break;
            }
            var payload = new
            {
                model,
                prompt,
                stream  = true,
                options = options ?? new OllamaOptions(),
                keep_alive = "5m" // Default keep_alive for completions
            };

            var body    = JsonConvert.SerializeObject(payload);
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response = null;
            string errorMessage = null;
            bool isCancelled = false;
            try
            {
                response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException)
            {
                isCancelled = true;
            }
            catch (Exception ex)
            {
                errorMessage = $"\n[LocalPilot Error] Could not reach Ollama: {ex.Message}";
                HandleFailure();
            }

            if (isCancelled) yield break;

            if (errorMessage != null)
            {
                yield return errorMessage;
                yield break;
            }

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader) { SupportMultipleContent = true })
            {
                while (await jsonReader.ReadAsync(ct))
                {
                    if (jsonReader.TokenType != JsonToken.StartObject) continue;

                    var obj = await JObject.LoadAsync(jsonReader, ct);
                    
                    var token = obj["response"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(token))
                        yield return token;

                    if (obj["done"]?.Value<bool>() == true)
                        break;
                }
            }
        }

        // ── Chat completion (SIMPLE — no tool calling, for regular chat) ──────
        /// <summary>Yields chat response tokens one by one as they stream. No tool support.</summary>
        public async IAsyncEnumerable<string> StreamChatAsync(
            string model,
            List<ChatMessage> messages,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Delegate to the advanced method but ignore tool calls
            await foreach (var result in StreamChatWithToolsAsync(model, messages, null, options, ct))
            {
                if (result.IsTextToken)
                    yield return result.TextToken;
            }
        }

        // ── Chat completion WITH NATIVE TOOL CALLING ──────────────────────────
        /// <summary>
        /// Streams chat responses and returns structured tool calls from Ollama's native API.
        /// </summary>
        public async IAsyncEnumerable<ChatStreamResult> StreamChatWithToolsAsync(
            string model,
            List<ChatMessage> messages,
            List<OllamaToolDefinition> tools = null,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CircuitBreakerTripped)
            {
                yield return ChatStreamResult.Text("\n⚠️ **LocalPilot Error:** Ollama is currently unreachable. The circuit breaker is tripped due to multiple connection failures.\n\nPlease ensure Ollama is running and accessible at " + _baseUrl);
                yield break;
            }

            // ── GUARD: Detect embedding-only models early ─────────────────────────
            if (!string.IsNullOrEmpty(model) &&
                (model.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("nomic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("bge-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("e5-", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                LocalPilotLogger.LogError($"[Ollama] Misconfiguration: '{model}' is an embedding model.", null, LogCategory.Ollama);
                yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Configuration Error:** The selected Chat Model (`{model}`) is an embedding-only model.");
                yield break;
            }

            // 🚀 DYNAMIC TIMEOUT ENGINE: Set a total timeout for chat requests based on task complexity
            using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                requestCts.CancelAfter(TimeSpan.FromSeconds(options?.RequestTimeoutSeconds ?? 60));

                // Build payload
                var payload = new
                {
                    model,
                    messages,
                    tools,
                    stream = true,
                    options = options ?? new OllamaOptions(),
                    keep_alive = "5m"
                };

                string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                
                HttpResponseMessage response = null;
                string errorDetails = null;
                bool toolSupportFailed = false;
                bool isCancelled = false;

                try
                {
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", content, requestCts.Token).ConfigureAwait(false);
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        if (tools != null && tools.Count > 0)
                        {
                            toolSupportFailed = true;
                            LocalPilotLogger.Log($"[Ollama] Model '{model}' does not support native tool calling. Falling back.", LogCategory.Ollama, LogSeverity.Warning);
                        }
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (OperationCanceledException) { isCancelled = true; }
                catch (Exception ex)
                {
                    errorDetails = ex.Message;
                    HandleFailure();
                }

                if (isCancelled)
                {
                    int timeout = options?.RequestTimeoutSeconds ?? 60;
                    yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Error:** The request to Ollama timed out after {timeout} seconds. Your hardware might be under heavy load or the context is too large. Try reducing the amount of context (e.g. closing unnecessary files) or selecting a smaller model.");
                    yield break;
                }

                if (errorDetails != null)
                {
                    yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Error:** {errorDetails}\n\nPlease check if Ollama is running correctly.");
                    yield break;
                }

                // 🚀 FALLBACK: If tools caused a 400, retry without tools
                if (toolSupportFailed)
                {
                    yield return ChatStreamResult.Text("\n> [!NOTE]\n> The selected model does not support native tool calling. Falling back to text parsing.\n\n");
                    var fallbackPayload = new { model, messages, stream = true, options = options ?? new OllamaOptions(), keep_alive = "5m" };
                    var fallbackContent = new StringContent(JsonConvert.SerializeObject(fallbackPayload), Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", fallbackContent, requestCts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }

                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(reader) { SupportMultipleContent = true })
                {
                    bool isDone = false;
                    while (!isDone)
                    {
                        JObject obj = null;
                        string streamError = null;

                        try
                        {
                            if (await jsonReader.ReadAsync(requestCts.Token))
                            {
                                if (jsonReader.TokenType != JsonToken.StartObject) continue;
                                obj = await JObject.LoadAsync(jsonReader, requestCts.Token);
                            }
                            else
                            {
                                isDone = true;
                            }
                        }
                        catch (JsonException ex)
                        {
                            LocalPilotLogger.LogError("[Ollama] Stream JSON parsing failed", ex, LogCategory.Ollama);
                            streamError = "Failed to parse Ollama response. The connection may have been interrupted.";
                            isDone = true;
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            LocalPilotLogger.LogError("[Ollama] Stream reading failed", ex, LogCategory.Ollama);
                            streamError = ex.Message;
                            isDone = true;
                        }

                        if (streamError != null)
                        {
                            yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Error:** {streamError}");
                            break;
                        }

                        if (obj != null)
                        {
                            var message = obj["message"];
                            if (message != null)
                            {
                                var token = message["content"]?.ToString();
                                if (!string.IsNullOrEmpty(token)) yield return ChatStreamResult.Text(token);

                                var toolCalls = message["tool_calls"] as JArray;
                                if (toolCalls != null)
                                {
                                    foreach (var tc in toolCalls)
                                    {
                                        var func = tc["function"];
                                        if (func == null) continue;

                                        yield return ChatStreamResult.ToolCall(
                                            func["name"]?.ToString(),
                                            func["arguments"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
                                        );
                                    }
                                }
                            }
                            if (obj["done"]?.Value<bool>() == true) isDone = true;
                        }
                    }
                }
            }
        }

    // ── Non-streaming chat (convenience) ───────────────────────────────────
        public async Task<string> ChatAsync(
            string model,
            List<ChatMessage> messages,
            OllamaOptions options = null,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var token in StreamChatAsync(model, messages, options, ct))
                sb.Append(token);
            return sb.ToString();
        }

        // ── 🚀 World-Class: GPU VRAM Watchdog ────────────────────────────────
        /// <summary>
        /// Queries Ollama's memory state to detect if GPU VRAM is under pressure.
        /// Helps prevent system-wide freezes caused by model swapping.
        /// </summary>
        public async Task<VramStatus> GetVramStatusAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/ps", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new VramStatus { IsHealthy = true };

                var json = await response.Content.ReadAsStringAsync();
                var obj = JObject.Parse(json);
                var models = obj["models"] as JArray;
                
                if (models == null || models.Count == 0) return new VramStatus { IsHealthy = true, TotalVramUsedMb = 0 };

                long totalUsed = 0;
                foreach (var m in models) {
                    totalUsed += (long)(m["size_vram"] ?? 0);
                }

                double usedGb = totalUsed / (1024.0 * 1024 * 1024);
                return new VramStatus 
                { 
                    IsHealthy = usedGb < 12.0, // Assuming a standard 12GB-16GB card threshold
                    TotalVramUsedMb = (int)(totalUsed / (1024 * 1024)),
                    ActiveModelsCount = models.Count
                };
            }
            catch { return new VramStatus { IsHealthy = true }; }
        }

    }

    // ── Supporting types ───────────────────────────────────────────────────────

    /// <summary>
    /// Result from a chat stream — either a text token or a structured tool call.
    /// This replaces the old approach of parsing ```json blocks from text output.
    /// </summary>
    public class ChatStreamResult
    {
        public bool IsTextToken { get; private set; }
        public bool IsToolCall { get; private set; }
        
        public string TextToken { get; private set; }
        
        public string ToolName { get; private set; }
        public Dictionary<string, object> ToolArguments { get; private set; }

        public static ChatStreamResult Text(string token) => new ChatStreamResult 
        { 
            IsTextToken = true, 
            TextToken = token 
        };

        public static ChatStreamResult ToolCall(string name, Dictionary<string, object> args) => new ChatStreamResult 
        { 
            IsToolCall = true, 
            ToolName = name, 
            ToolArguments = args 
        };
    }

    /// <summary>
    /// Ollama tool definition for native function calling.
    /// Matches the JSON schema expected by Ollama's /api/chat endpoint.
    /// See: https://ollama.com/blog/tool-support
    /// </summary>
    public class OllamaToolDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "function";

        [JsonProperty("function")]
        public OllamaFunctionDefinition Function { get; set; }
    }

    public class OllamaFunctionDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("parameters")]
        public OllamaParameterDefinition Parameters { get; set; }
    }

    public class OllamaParameterDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "object";

        [JsonProperty("properties")]
        public Dictionary<string, OllamaPropertyDefinition> Properties { get; set; } = new Dictionary<string, OllamaPropertyDefinition>();

        [JsonProperty("required")]
        public List<string> Required { get; set; } = new List<string>();
    }

    public class OllamaPropertyDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "string";

        [JsonProperty("description")]
        public string Description { get; set; }
    }

    public class OllamaOptions
    {
        [JsonProperty("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonProperty("top_p")]
        public double TopP { get; set; } = 0.9;

        [JsonProperty("top_k")]
        public int TopK { get; set; } = 40;

        [JsonProperty("repeat_penalty")]
        public double RepeatPenalty { get; set; } = 1.1;

        [JsonProperty("num_ctx")]
        public int NumCtx { get; set; } = LocalPilot.Settings.LocalPilotSettings.Instance.ContextWindowSize;

        [JsonProperty("num_predict")]
        public int NumPredict { get; set; } = LocalPilot.Settings.LocalPilotSettings.Instance.MaxOutputTokens;

        [JsonProperty("stop")]
        public List<string> Stop { get; set; } = new List<string>();

        [JsonProperty("keep_alive")]
        public string KeepAlive { get; set; } = "5m";

        /// <summary>Internal: Total time to wait for the request to complete.</summary>
        [JsonIgnore]
        public int RequestTimeoutSeconds { get; set; } = 60;
    }

    public class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }      // "system" | "user" | "assistant" | "tool"

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    public class VramStatus
    {
        public bool IsHealthy { get; set; }
        public int TotalVramUsedMb { get; set; }
        public int ActiveModelsCount { get; set; }
    }
}
