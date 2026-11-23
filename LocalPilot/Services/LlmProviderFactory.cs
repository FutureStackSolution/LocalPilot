using System;
using LocalPilot.Settings;

namespace LocalPilot.Services
{
    /// <summary>
    /// Factory for creating and retrieving the active ILlmService provider instance
    /// based on current user settings (Ollama vs. OpenAI-compatible endpoints).
    /// </summary>
    public static class LlmProviderFactory
    {
        private static ILlmService _currentService;
        private static LlmProviderType _lastProviderType = (LlmProviderType)(-1);
        private static string _lastUrl;
        private static string _lastApiKey;
        private static readonly object _lock = new object();

        /// <summary>
        /// Retrieves the active LLM service instance configured by LocalPilotSettings.
        /// Reuses the instance if the connection configuration has not changed.
        /// </summary>
        public static ILlmService GetService()
        {
            var settings = LocalPilotSettings.Instance;
            var provider = settings.ProviderType;
            var url = provider == LlmProviderType.Ollama ? settings.OllamaBaseUrl : settings.OpenAiBaseUrl;
            var apiKey = settings.OpenAiApiKey ?? "";

            lock (_lock)
            {
                if (_currentService == null || _lastProviderType != provider || _lastUrl != url || _lastApiKey != apiKey)
                {
                    _lastProviderType = provider;
                    _lastUrl = url;
                    _lastApiKey = apiKey;
                    _currentService = CreateService(provider, url, apiKey);
                }
                return _currentService;
            }
        }

        /// <summary>
        /// Creates a new isolated LLM service instance for a specific provider and endpoint configuration.
        /// </summary>
        public static ILlmService CreateService(LlmProviderType provider, string baseUrl, string apiKey = "")
        {
            if (provider == LlmProviderType.OpenAiCompatible)
            {
                return new OpenAiService(baseUrl, apiKey);
            }
            return new OllamaService(baseUrl);
        }
    }
}
