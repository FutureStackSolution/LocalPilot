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
    /// Supports streaming completions, chat, and model listing.
    /// </summary>
    public class OllamaService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private string _baseUrl;

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        }

        public void UpdateBaseUrl(string baseUrl)
        {
            // If HttpClient is static, BaseAddress cannot be updated per instance.
            // Instead, the _baseUrl field is used to construct full URLs for each request.
            // This method now only updates the instance's _baseUrl.
            _baseUrl = baseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        }

        // ── Model listing ──────────────────────────────────────────────────────
        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var names = new List<string>();
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
                LocalPilotLogger.Log($"[Ollama] Fetching models from {_baseUrl}/api/tags. Success: {response.IsSuccessStatusCode}");
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

        // ── Code completion (generate endpoint) ───────────────────────────────
        /// <summary>Yields tokens one by one as they stream from Ollama.</summary>
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
                options = options ?? new OllamaOptions()
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

        // ── Chat completion ────────────────────────────────────────────────────
        /// <summary>Yields chat response tokens one by one as they stream.</summary>
        public async IAsyncEnumerable<string> StreamChatAsync(
            string model,
            List<ChatMessage> messages,
            OllamaOptions options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var payload = new
            {
                model,
                messages,
                stream  = true,
                options = options ?? new OllamaOptions()
            };

            string jsonPayload = JsonConvert.SerializeObject(payload);
            LocalPilotLogger.Log($"[Ollama] StreamChatAsync starting for model: {model}");
            LocalPilotLogger.Log($"[Ollama] Input Payload: {jsonPayload}");

            HttpResponseMessage response = null;
            string errorDetails = null;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                errorDetails = ex.Message;
                LocalPilotLogger.LogError("Ollama Service Connection Error", ex);
            }

            if (errorDetails != null)
            {
                yield return $"\n[LocalPilot Error] Could not reach Ollama: {errorDetails}";
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
                        
                        var token = obj["message"]?["content"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(token))
                        {
                            fullResponse.Append(token);
                            yield return token;
                        }

                        if (obj["done"]?.Value<bool>() == true)
                        {
                            LocalPilotLogger.Log($"[Ollama] Finished streaming from model: {model}");
                            LocalPilotLogger.Log($"[Ollama] Full Output Response: {fullResponse.ToString()}");
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
    public class OllamaOptions
    {
        [JsonProperty("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonProperty("top_p")]
        public double TopP { get; set; } = 0.9;

        [JsonProperty("num_predict")]
        public int NumPredict { get; set; } = 4096;

        [JsonProperty("stop")]
        public List<string> Stop { get; set; } = new List<string>();
    }

    public class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }      // "system" | "user" | "assistant"

        [JsonProperty("content")]
        public string Content { get; set; }
    }
}
