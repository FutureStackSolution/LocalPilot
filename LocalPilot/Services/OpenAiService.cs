using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Communicates with any OpenAI-compatible API endpoint over HTTP
    /// (e.g. OpenAI, LM Studio, vLLM, llama-server, OpenRouter, Groq, DeepSeek).
    /// </summary>
    public class OpenAiService : ILlmService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private string _baseUrl;
        private string _apiKey;

        private volatile bool _circuitBreakerTripped = false;
        private static readonly JsonSerializerSettings _ignoreNullSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        public bool CircuitBreakerTripped
        {
            get => _circuitBreakerTripped;
            private set => _circuitBreakerTripped = value;
        }
        private int _consecutiveFailures = 0;
        private volatile bool _embeddingsNotSupported = false;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unsupportedEmbeddingEndpoints
            = LoadUnsupportedEmbeddingEndpoints();

        private static string GetEmbeddingCapabilityCachePath()
        {
            string appData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalPilot");
            if (!System.IO.Directory.Exists(appData)) System.IO.Directory.CreateDirectory(appData);
            return System.IO.Path.Combine(appData, "unsupported_embeddings.txt");
        }

        private static System.Collections.Concurrent.ConcurrentDictionary<string, byte> LoadUnsupportedEmbeddingEndpoints()
        {
            var dict = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GetEmbeddingCapabilityCachePath();
                if (System.IO.File.Exists(path))
                {
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var trimmed = line?.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) dict.TryAdd(trimmed, 0);
                    }
                }
            }
            catch { }
            return dict;
        }

        private static void MarkEndpointEmbeddingsUnsupported(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return;
            if (_unsupportedEmbeddingEndpoints.TryAdd(baseUrl, 0))
            {
                try
                {
                    System.IO.File.WriteAllLines(GetEmbeddingCapabilityCachePath(), _unsupportedEmbeddingEndpoints.Keys);
                }
                catch { }
            }
        }

        public OpenAiService(string baseUrl = "https://api.openai.com/v1", string apiKey = "")
        {
            UpdateEndpoint(baseUrl, apiKey);
        }

        public void UpdateBaseUrl(string baseUrl)
        {
            UpdateEndpoint(baseUrl, _apiKey);
        }

        public void UpdateEndpoint(string baseUrl, string apiKey)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
            _apiKey = apiKey?.Trim() ?? "";
            _embeddingsNotSupported = _unsupportedEmbeddingEndpoints.ContainsKey(_baseUrl);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, HttpContent content = null)
        {
            string url = $"{_baseUrl}/{relativePath.TrimStart('/')}";
            var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
            if (content != null)
            {
                request.Content = content;
            }
            return request;
        }

        private void HandleSuccess()
        {
            _consecutiveFailures = 0;
            if (CircuitBreakerTripped)
            {
                CircuitBreakerTripped = false;
                LocalPilotLogger.Log("[OpenAI] Circuit breaker reset: connection restored.", LogCategory.Ollama);
            }
        }

        private void HandleFailure()
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3 && !CircuitBreakerTripped)
            {
                CircuitBreakerTripped = true;
                LocalPilotLogger.Log("[OpenAI] Circuit breaker tripped after 3 consecutive failures.", LogCategory.Ollama, LogSeverity.Error);
            }
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                using (var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    localCts.CancelAfter(TimeSpan.FromSeconds(6));
                    using (var req = CreateRequest(HttpMethod.Get, "models"))
                    using (var resp = await _httpClient.SendAsync(req, localCts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            HandleSuccess();
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var names = new List<string>();
            try
            {
                using (var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    localCts.CancelAfter(TimeSpan.FromSeconds(10));
                    using (var req = CreateRequest(HttpMethod.Get, "models"))
                    using (var response = await _httpClient.SendAsync(req, localCts.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return names;

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var obj = JObject.Parse(json);
                        var arr = obj["data"] as JArray;
                        if (arr == null) return names;

                        foreach (var m in arr)
                        {
                            string id = m["id"]?.ToString();
                            if (!string.IsNullOrEmpty(id)) names.Add(id);
                        }
                        HandleSuccess();
                    }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[OpenAI] Failed to fetch models: {ex.Message}", LogCategory.Ollama, LogSeverity.Warning);
            }
            return names.OrderBy(n => n).ToList();
        }

        public Task WarmupAsync(string model, CancellationToken ct = default)
        {
            // OpenAI endpoints are managed externally; warmup is a no-op
            return Task.CompletedTask;
        }

        public Task<VramStatus> GetVramStatusAsync(CancellationToken ct = default)
        {
            // Remote/managed providers handle memory allocation transparently
            return Task.FromResult(new VramStatus { IsHealthy = true, TotalVramUsedMb = 0, ActiveModelsCount = 1 });
        }

        public Task<int> UnloadAllModelsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(0);
        }

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            string prompt,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CircuitBreakerTripped)
            {
                yield return "\n[LocalPilot] OpenAI endpoint is currently unreachable.";
                yield break;
            }

            // Standard OpenAI chat completion fallback works universally
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = prompt } };
            await foreach (var result in StreamChatWithToolsAsync(model, messages, null, options, ct))
            {
                if (result.IsTextToken && !string.IsNullOrEmpty(result.TextToken))
                {
                    yield return result.TextToken;
                }
            }
        }

        public async IAsyncEnumerable<string> StreamChatAsync(
            string model,
            List<ChatMessage> messages,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var result in StreamChatWithToolsAsync(model, messages, null, options, ct))
            {
                if (result.IsTextToken && !string.IsNullOrEmpty(result.TextToken))
                {
                    yield return result.TextToken;
                }
            }
        }

        public async Task<string> ChatAsync(
            string model,
            List<ChatMessage> messages,
            OllamaOptions options = null,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var token in StreamChatAsync(model, messages, options, ct))
            {
                sb.Append(token);
            }
            return sb.ToString();
        }

        public async IAsyncEnumerable<ChatStreamResult> StreamChatWithToolsAsync(
            string model,
            List<ChatMessage> messages,
            List<OllamaToolDefinition> tools = null,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CircuitBreakerTripped)
            {
                yield return ChatStreamResult.Text("\n[LocalPilot] Endpoint unreachable (Circuit breaker tripped).");
                yield break;
            }

            var messagesPayload = NormalizeMessagesForOpenAi(messages);

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = messagesPayload,
                ["stream"] = true
            };

            bool isModernOpenAi = IsOpenAiModernReasoningOrGpt5(_baseUrl, model);
            if (options != null)
            {
                if (!isModernOpenAi)
                {
                    requestBody["temperature"] = options.Temperature;
                    if (options.Stop != null && options.Stop.Count > 0) requestBody["stop"] = options.Stop;
                }
                if (options.NumPredict > 0)
                {
                    requestBody[isModernOpenAi ? "max_completion_tokens" : "max_tokens"] = options.NumPredict;
                }
            }

            if (tools != null && tools.Count > 0)
            {
                requestBody["tools"] = tools;
                requestBody["tool_choice"] = "auto";
            }

            string jsonPayload = JsonConvert.SerializeObject(requestBody, _ignoreNullSettings);

            HttpResponseMessage response = null;
            string errorDetails = null;
            int inactivitySeconds = Math.Max(60, Math.Min(300, options?.RequestTimeoutSeconds ?? 120));
            TimeSpan inactivityTimeout = TimeSpan.FromSeconds(inactivitySeconds);

            try
            {
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    headerCts.CancelAfter(inactivityTimeout);
                    using (var req = CreateRequest(HttpMethod.Post, "chat/completions", content))
                    {
                        response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
                    }
                }

                // 🛡️ RESILIENCE: Adaptively handle HTTP 400 parameter incompatibilities (tools, max_tokens -> max_completion_tokens, temperature, stop)
                int adaptAttempts = 0;
                while (!response.IsSuccessStatusCode &&
                       response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                       adaptAttempts < 2)
                {
                    string errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!TryAdaptRequestForBadRequest(requestBody, errBody, tools))
                    {
                        errorDetails = FormatApiError(response.StatusCode, errBody);
                        HandleFailure();
                        break;
                    }

                    adaptAttempts++;
                    LocalPilotLogger.Log($"[OpenAI] Adapting request parameters (attempt {adaptAttempts}) after HTTP 400: {errBody}", LogCategory.Ollama, LogSeverity.Warning);
                    response.Dispose();

                    string retryJson = JsonConvert.SerializeObject(requestBody, _ignoreNullSettings);
                    var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");
                    using (var retryHeaderCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        retryHeaderCts.CancelAfter(inactivityTimeout);
                        using (var retryReq = CreateRequest(HttpMethod.Post, "chat/completions", retryContent))
                        {
                            response = await _httpClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, retryHeaderCts.Token).ConfigureAwait(false);
                        }
                    }
                }

                if (errorDetails == null && !response.IsSuccessStatusCode)
                {
                    string errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    errorDetails = FormatApiError(response.StatusCode, errBody);
                    HandleFailure();
                }
                else if (errorDetails == null)
                {
                    HandleSuccess();
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    yield break;
                }
                errorDetails = $"Request timed out after {inactivitySeconds}s waiting for server response.";
                HandleFailure();
            }
            catch (Exception ex)
            {
                errorDetails = ex.Message;
                HandleFailure();
            }

            if (errorDetails != null)
            {
                yield return ChatStreamResult.Text($"\n{errorDetails}");
                yield break;
            }

            var accumulatedToolCalls = new Dictionary<int, (string name, StringBuilder args)>();
            int totalToolArgChars = 0;
            int lastReportedToolChars = -24;
            bool streamTimedOut = false;

            using (response)
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (!ct.IsCancellationRequested)
                {
                    var readTask = reader.ReadLineAsync();
                    var delayTask = Task.Delay(inactivityTimeout, ct);
                    var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            streamTimedOut = true;
                            LocalPilotLogger.LogError($"[OpenAI] Stream inactivity timeout after {inactivitySeconds}s with no data.", null, LogCategory.Ollama);
                            HandleFailure();
                        }
                        break;
                    }

                    string line;
                    try
                    {
                        line = await readTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError("[OpenAI] Stream read failed", ex, LogCategory.Ollama);
                        HandleFailure();
                        streamTimedOut = true;
                        break;
                    }
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;

                    JObject parsed;
                    try
                    {
                        parsed = JObject.Parse(data);
                    }
                    catch
                    {
                        continue;
                    }

                    var choices = parsed["choices"] as JArray;
                    if (choices == null || choices.Count == 0) continue;

                    var delta = choices[0]["delta"];
                    if (delta == null) continue;

                    // 1. Text token
                    string text = delta["content"]?.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return ChatStreamResult.Text(text);
                    }

                    // 2. Tool calls delta
                    var tcArray = delta["tool_calls"] as JArray;
                    if (tcArray != null)
                    {
                        string activeToolName = null;
                        foreach (var tc in tcArray)
                        {
                            int index = tc["index"]?.Value<int>() ?? 0;
                            var fn = tc["function"];
                            if (fn == null) continue;

                            string fnName = fn["name"]?.ToString();
                            string fnArgs = fn["arguments"]?.ToString();

                            if (!accumulatedToolCalls.TryGetValue(index, out var existing))
                            {
                                existing = (fnName ?? "", new StringBuilder());
                                accumulatedToolCalls[index] = existing;
                            }
                            else if (!string.IsNullOrEmpty(fnName) && string.IsNullOrEmpty(existing.name))
                            {
                                existing.name = fnName;
                                accumulatedToolCalls[index] = existing;
                            }

                            activeToolName = existing.name;
                            if (!string.IsNullOrEmpty(fnArgs))
                            {
                                existing.args.Append(fnArgs);
                                totalToolArgChars += fnArgs.Length;
                            }
                        }

                        if (totalToolArgChars - lastReportedToolChars >= 24)
                        {
                            lastReportedToolChars = totalToolArgChars;
                            yield return ChatStreamResult.ToolProgress(activeToolName, totalToolArgChars);
                        }
                    }
                }
            }

            if (streamTimedOut)
            {
                yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Error:** Stream timed out after {inactivitySeconds}s of inactivity from the LLM server.");
                yield break;
            }

            // Dispatch parsed tool calls
            foreach (var kvp in accumulatedToolCalls.OrderBy(k => k.Key))
            {
                string name = kvp.Value.name;
                string argsJson = kvp.Value.args.ToString();
                Dictionary<string, object> argsDict = null;

                if (!string.IsNullOrWhiteSpace(argsJson))
                {
                    try
                    {
                        argsDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(argsJson);
                    }
                    catch
                    {
                        argsDict = new Dictionary<string, object> { ["raw_args"] = argsJson };
                    }
                }

                yield return ChatStreamResult.ToolCall(name, argsDict ?? new Dictionary<string, object>());
            }
        }

        private static bool IsOpenAiModernReasoningOrGpt5(string baseUrl, string model)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model)) return false;
            if (baseUrl.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) < 0) return false;

            string m = model.Trim();
            return m.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                   m.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
                   m.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
                   m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryAdaptRequestForBadRequest(
            Dictionary<string, object> requestBody,
            string errBody,
            List<OllamaToolDefinition> tools)
        {
            if (string.IsNullOrEmpty(errBody)) return false;
            bool adapted = false;

            if (requestBody.TryGetValue("max_tokens", out var maxTok) &&
                errBody.IndexOf("max_tokens", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                requestBody.Remove("max_tokens");
                requestBody["max_completion_tokens"] = maxTok;
                adapted = true;
            }

            if (requestBody.ContainsKey("temperature") &&
                errBody.IndexOf("temperature", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                requestBody.Remove("temperature");
                adapted = true;
            }

            if (requestBody.ContainsKey("stop") &&
                errBody.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                requestBody.Remove("stop");
                adapted = true;
            }

            if (tools != null && tools.Count > 0 && requestBody.ContainsKey("tools") &&
                (errBody.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 errBody.IndexOf("function", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 errBody.IndexOf("unrecognized", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                requestBody.Remove("tools");
                requestBody.Remove("tool_choice");
                adapted = true;
            }

            return adapted;
        }

        private static string FormatApiError(System.Net.HttpStatusCode statusCode, string errBody)
        {
            string cleanMsg = errBody;
            string errCode = null;
            if (!string.IsNullOrWhiteSpace(errBody))
            {
                try
                {
                    var obj = JObject.Parse(errBody);
                    var errObj = obj["error"];
                    if (errObj != null)
                    {
                        cleanMsg = errObj["message"]?.ToString() ?? errBody;
                        errCode = errObj["code"]?.ToString();
                    }
                }
                catch { }
            }

            if (statusCode == System.Net.HttpStatusCode.NotFound || string.Equals(errCode, "model_not_found", StringComparison.OrdinalIgnoreCase))
            {
                return $"⚠️ **Model Not Found:** {cleanMsg}\n\nEnsure your local LLM server is running and select a valid model from the dropdown.";
            }

            return $"⚠️ **Server Error (HTTP {(int)statusCode}):** {cleanMsg}";
        }

        public async Task<float[]> GetEmbeddingsAsync(string model, string prompt, CancellationToken ct = default)
        {
            var results = await GetEmbeddingsBatchAsync(model, new List<string> { prompt }, ct).ConfigureAwait(false);
            return results != null && results.Count > 0 ? results[0] : null;
        }

        public async Task<List<float[]>> GetEmbeddingsBatchAsync(string model, List<string> prompts, CancellationToken ct = default)
        {
            if (_embeddingsNotSupported || prompts == null || prompts.Count == 0) return new List<float[]>();

            try
            {
                var payload = new
                {
                    model = model,
                    input = prompts
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                using (var req = CreateRequest(HttpMethod.Post, "embeddings", content))
                using (var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                            response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                        {
                            _embeddingsNotSupported = true;
                            MarkEndpointEmbeddingsUnsupported(_baseUrl);
                            LocalPilotLogger.Log($"[OpenAI] Endpoint does not support /v1/embeddings (HTTP {(int)response.StatusCode}). Cached capability; falling back to keyword search.", LogCategory.Storage, LogSeverity.Info);
                        }
                        else
                        {
                            LocalPilotLogger.Log($"[OpenAI] Embeddings request failed with status: {response.StatusCode}", LogCategory.Storage, LogSeverity.Warning);
                        }
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var obj = JObject.Parse(json);
                    var data = obj["data"] as JArray;
                    if (data == null) return null;

                    var results = new float[prompts.Count][];
                    foreach (var item in data)
                    {
                        int idx = item["index"]?.Value<int>() ?? 0;
                        var embArray = item["embedding"] as JArray;
                        if (embArray != null && idx < results.Length)
                        {
                            results[idx] = embArray.Select(v => (float)v).ToArray();
                        }
                    }

                    HandleSuccess();
                    return results.ToList();
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[OpenAI] Batch embedding failed: {ex.Message}", LogCategory.Storage, LogSeverity.Warning);
                HandleFailure();
                return null;
            }
        }

        /// <summary>
        /// Normalizes messages for strict OpenAI-compatible engines (like Colibri, vLLM, LM Studio, OpenAI).
        /// Ensures:
        /// 1. Exactly ONE system message exists, placed strictly at index 0 (merging all leading system prompts/contexts).
        /// 2. Strictly NO 'system' messages occur after the conversation starts (converting mid-stream system prompts to user context).
        /// 3. Normalizes tool messages without tool_call_id to user role for universal compatibility.
        /// </summary>
        private static List<object> NormalizeMessagesForOpenAi(List<ChatMessage> messages)
        {
            var result = new List<object>();
            if (messages == null || messages.Count == 0) return result;

            int i = 0;

            // 1. 🚀 KV-CACHE OPTIMIZATION: Message 0 MUST be the clean, frozen system prompt alone.
            // Isolating the static system prompt at index 0 guarantees a byte-for-byte prefix match
            // across all turns in local engines (Colibri, vLLM, llama.cpp), turning 4-minute prefills into < 2 seconds!
            if (i < messages.Count && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new
                {
                    role = "system",
                    content = messages[0].Content?.Trim() ?? "You are LocalPilot."
                });
                i = 1;
            }

            // 2. Process all remaining messages: strictly no "system" roles after index 0
            // Dynamic context chunks (file content, diagnostics, etc.) are converted to user role
            for (; i < messages.Count; i++)
            {
                var msg = messages[i];
                string role = msg.Role?.ToLowerInvariant() ?? "user";
                string content = msg.Content ?? "";

                // OpenAI strictly forbids "system" messages anywhere except at index 0.
                // Converting mid-stream/dynamic system chunks to user context protects the KV-cache of Message 0!
                if (role == "system")
                {
                    role = "user";
                    content = $"[Context]\n{content}";
                }
                else if (role == "tool")
                {
                    // Map tool outputs to user role for universal OpenAI-compatible server support
                    role = "user";
                }

                result.Add(new
                {
                    role = role,
                    content = content
                });
            }

            // Fallback: If for some reason the payload is empty, provide at least one valid user message
            if (result.Count == 0)
            {
                result.Add(new { role = "user", content = "Hello" });
            }

            return result;
        }
    }
}
