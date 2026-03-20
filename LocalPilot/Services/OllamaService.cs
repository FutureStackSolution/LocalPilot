using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalPilot.Services
{
    /// <summary>
    /// Communicates with a local Ollama instance over HTTP.
    /// Supports streaming completions, chat, and model listing.
    /// </summary>
    public class OllamaService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public void UpdateBaseUrl(string baseUrl)
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        // ── Model listing ──────────────────────────────────────────────────────
        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var names = new List<string>();
            try
            {
                var response = await _httpClient.GetAsync("/api/tags", ct);
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
                var resp = await _httpClient.GetAsync("/api/tags", ct);
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
                response = await _httpClient.PostAsync("/api/generate", content, ct);
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
            {
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

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

            var body    = JsonConvert.SerializeObject(payload);
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response = null;
            string errorMessage = null;
            try
            {
                response = await _httpClient.PostAsync("/api/chat", content, ct);
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
            {
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    var token = obj["message"]?["content"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(token))
                        yield return token;

                    if (obj["done"]?.Value<bool>() == true)
                        break;
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

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient?.Dispose();
            _disposed = true;
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
        public int NumPredict { get; set; } = 256;

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
