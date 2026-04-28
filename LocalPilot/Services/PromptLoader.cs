using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

namespace LocalPilot.Services
{
    /// <summary>
    /// Loads Markdown prompt templates from the extension's 'Prompts' directory.
    /// This allows editing prompts without recompiling and keeps the logic clean.
    /// </summary>
    public static class PromptLoader
    {
        private static readonly string _assemblyDir;
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static readonly Dictionary<string, DateTime> _cacheTimestamps = new Dictionary<string, DateTime>();

        static PromptLoader()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                _assemblyDir = Path.GetDirectoryName(Uri.UnescapeDataString(new UriBuilder(assembly.CodeBase).Path));
            }
            catch
            {
                _assemblyDir = AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>
        /// Reads a prompt template by name (e.g., "SystemPrompt").
        /// Automatically handles placeholders like {solutionPath}.
        /// </summary>
        public static string GetPrompt(string templateName, Dictionary<string, string> variables = null)
        {
            string content = LoadTemplate(templateName);
            if (string.IsNullOrEmpty(content)) return string.Empty;

            if (variables != null)
            {
                foreach (var kvp in variables)
                {
                    content = content.Replace("{" + kvp.Key + "}", kvp.Value);
                }
            }

            return content;
        }

        private static string LoadTemplate(string name)
        {
            try
            {
                string path = Path.Combine(_assemblyDir, "Prompts", $"{name}.md");
                
                if (File.Exists(path))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    
                    // Invalidate cache if the file on disk is newer (e.g. after a VSIX update)
                    if (_cache.TryGetValue(name, out var cached) &&
                        _cacheTimestamps.TryGetValue(name, out var cachedTime) &&
                        lastWrite <= cachedTime)
                    {
                        return cached;
                    }
                    
                    string content = File.ReadAllText(path);
                    _cache[name] = content;
                    _cacheTimestamps[name] = lastWrite;
                    return content;
                }
                
                LocalPilotLogger.Log($"[PromptLoader] Template not found: {path}");
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[PromptLoader] Failed to load template {name}", ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Clears the cache — useful if we implement a 'Reload Prompts' feature later.
        /// </summary>
        public static void ClearCache() => _cache.Clear();

        public static string GetPromptsDirectoryPath() => Path.Combine(_assemblyDir, "Prompts");
    }
}
