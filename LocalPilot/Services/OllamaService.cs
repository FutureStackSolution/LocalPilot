using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        private string _baseUrl;

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        }

        public void UpdateBaseUrl(string baseUrl)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
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
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Semantic Embeddings ────────────────────────────────────────────────
        public async Task<float[]> GetEmbeddingsAsync(string model, string prompt, CancellationToken ct = default)
        {
            try
            {
                var payload = new { 
                    model, 
                    prompt, 
                    keep_alive = "10m" // Keep embedding model warm for 10 mins
                };
                var body = JsonConvert.SerializeObject(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/embeddings", content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                var obj = JObject.Parse(json);
                var embeddingArray = obj["embedding"] as JArray;
                
                if (embeddingArray == null) return null;
                return embeddingArray.ToObject<float[]>();
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Ollama] Embedding failed: {ex.Message}", ex);
                return null;
            }
        }

        // ── Code completion (generate endpoint) ───────────────────────────────
        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            string prompt,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
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
            try
            {
                response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex)
            {
                errorMessage = $"\n[LocalPilot Error] Could not reach Ollama: {ex.Message}";
            }

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
        /// This is the core method that eliminates text-based JSON parsing of tool calls.
        /// 
        /// When tools are provided, Ollama's API will return either:
        ///   1. A normal text response (streamed token by token)
        ///   2. A tool_calls array in the response (structured, not embedded in text)
        /// 
        /// The caller receives ChatStreamResult objects that clearly distinguish between
        /// text tokens and tool call requests.
        /// </summary>
        public async IAsyncEnumerable<ChatStreamResult> StreamChatWithToolsAsync(
            string model,
            List<ChatMessage> messages,
            List<OllamaToolDefinition> tools = null,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // ── GUARD: Detect embedding-only models early ─────────────────────────
            // Models like nomic-embed-text, bge-*, e5-* do NOT support /api/chat.
            // Ollama returns HTTP 400 for these. Surface a clear error instead.
            if (!string.IsNullOrEmpty(model) &&
                (model.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("nomic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("bge-",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf("e5-",   StringComparison.OrdinalIgnoreCase) >= 0))
            {
                LocalPilotLogger.LogError($"[Ollama] Misconfiguration: '{model}' is an embedding model and cannot be used for chat. Go to LocalPilot Settings and set a chat-capable model (e.g. llama3).", null, LogCategory.Ollama);
                yield return ChatStreamResult.Text($"\n⚠️ **LocalPilot Configuration Error:** The selected Chat Model (`{model}`) is an embedding-only model and does not support chat or tool calls.\n\nPlease open **Tools → Options → LocalPilot** and change the **Chat Model** to a chat-capable model (e.g. `llama3` or `gemma2`).");
                yield break;
            }

            // Build payload — include tools if provided
            object payload;
            if (tools != null && tools.Count > 0)
            {
                payload = new
                {
                    model,
                    messages,
                    tools,
                    stream = true,
                    options = options ?? new OllamaOptions()
                };
            }
            else
            {
                payload = new
                {
                    model,
                    messages,
                    stream = true,
                    options = options ?? new OllamaOptions(),
                    keep_alive = "5m"
                };
            }

            string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings 
            { 
                NullValueHandling = NullValueHandling.Ignore 
            });
            
            LocalPilotLogger.Log($"POST /api/chat payload:\n{jsonPayload}", LogCategory.Ollama);

            HttpResponseMessage response = null;
            string errorDetails = null;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex)
            {
                errorDetails = ex.Message;
                LocalPilotLogger.LogError("[Ollama] Core Network Error", ex, LogCategory.Ollama);
            }

            if (errorDetails != null)
            {
                yield return ChatStreamResult.Text($"\n[LocalPilot Error] Could not reach Ollama: {errorDetails}");
                yield break;
            }

            var fullResponse = new StringBuilder();
            using (response)
            {
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(reader) { SupportMultipleContent = true })
                {
                    while (await jsonReader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (jsonReader.TokenType != JsonToken.StartObject) continue;
                        var obj = await JObject.LoadAsync(jsonReader, ct).ConfigureAwait(false);
                        
                        // Check for text content
                        var token = obj["message"]?["content"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(token))
                        {
                            fullResponse.Append(token);
                            yield return ChatStreamResult.Text(token);
                        }

                        // ═══════════════════════════════════════════════════════
                        // NATIVE TOOL CALLING: Check for tool_calls in the response
                        // Ollama returns these as structured JSON, NOT embedded in text.
                        // This is the key architectural improvement over parsing ```json blocks.
                        // ═══════════════════════════════════════════════════════
                        var toolCalls = obj["message"]?["tool_calls"] as JArray;
                        if (toolCalls != null && toolCalls.Count > 0)
                        {
                            LocalPilotLogger.Log($"[Ollama] Received {toolCalls.Count} native tool call(s)");
                            foreach (var tc in toolCalls)
                            {
                                var funcObj = tc["function"];
                                if (funcObj == null) continue;

                                string toolName = funcObj["name"]?.ToString();
                                var argsObj = funcObj["arguments"];
                                
                                Dictionary<string, object> args = null;
                                if (argsObj != null)
                                {
                                    try
                                    {
                                        args = argsObj.ToObject<Dictionary<string, object>>();
                                    }
                                    catch
                                    {
                                        args = new Dictionary<string, object>();
                                    }
                                }

                                if (!string.IsNullOrEmpty(toolName))
                                {
                                    LocalPilotLogger.Log($"[Ollama] Tool call: {toolName}({JsonConvert.SerializeObject(args)})");
                                    yield return ChatStreamResult.ToolCall(toolName, args ?? new Dictionary<string, object>());
                                }
                            }
                        }

                        if (obj["done"]?.Value<bool>() == true)
                        {
                            var totalDuration = obj["total_duration"]?.ToString();
                            LocalPilotLogger.Log($"[Ollama] Finished streaming from model: {model} (Duration: {totalDuration}ns)", LogCategory.Ollama);
                            break;
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
        public int NumCtx { get; set; } = 8192;

        [JsonProperty("num_predict")]
        public int NumPredict { get; set; } = 4096;

        [JsonProperty("stop")]
        public List<string> Stop { get; set; } = new List<string>();

        [JsonProperty("keep_alive")]
        public string KeepAlive { get; set; } = "5m";
    }

    public class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }      // "system" | "user" | "assistant" | "tool"

        [JsonProperty("content")]
        public string Content { get; set; }
    }
}
