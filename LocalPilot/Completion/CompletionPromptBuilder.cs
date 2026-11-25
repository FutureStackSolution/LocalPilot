using LocalPilot.Settings;

namespace LocalPilot.Completion
{
    /// <summary>
    /// Builds the prompt that is sent to Ollama for inline completion.
    /// Mirrors the FIM (Fill-In-the-Middle) technique used by Copilot.
    /// </summary>
    public class CompletionPromptBuilder
    {
        private readonly LocalPilotSettings _settings;

        public CompletionPromptBuilder(LocalPilotSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Build a FIM-style prompt from the text before and after the cursor.
        /// Uses native FIM tokens for coder models (Qwen/DeepSeek/StarCoder) to cut prefill latency.
        /// </summary>
        public string Build(string fileExtension, string prefix, string suffix, string filePath)
        {
            bool isFast = _settings?.Mode == PerformanceMode.Fast;
            int maxBefore = isFast ? 32 : 64;
            int maxAfter = isFast ? 8 : 16;

            prefix = TrimLines(prefix, maxBefore, fromEnd: true);
            suffix = TrimLines(suffix, maxAfter, fromEnd: false);

            string model = _settings?.CompletionModel ?? "";
            if (SupportsQwenFim(model))
            {
                return $"<|fim_prefix|>{prefix}<|fim_suffix|>{suffix}<|fim_middle|>";
            }

            string langHint = GetLanguageHint(fileExtension);
            var vars = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Language", langHint },
                { "FileName", System.IO.Path.GetFileName(filePath) },
                { "Prefix",   prefix },
                { "Suffix",   suffix }
            };

            return Services.PromptLoader.GetPrompt("CompletionPrompt", vars);
        }

        private static bool SupportsQwenFim(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.IndexOf("qwen", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("starcoder", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("coder", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TrimLines(string text, int maxLines, bool fromEnd)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lines = text.Split('\n');
            if (lines.Length <= maxLines) return text;
            return fromEnd
                ? string.Join("\n", lines, lines.Length - maxLines, maxLines)
                : string.Join("\n", lines, 0, maxLines);
        }

        private static string GetLanguageHint(string ext) => ext?.ToLower() switch
        {
            ".cs"    => "C#",
            ".vb"    => "Visual Basic",
            ".cpp"   => "C++",
            ".c"     => "C",
            ".h"     => "C/C++ header",
            ".py"    => "Python",
            ".js"    => "JavaScript",
            ".ts"    => "TypeScript",
            ".json"  => "JSON",
            ".xml"   => "XML",
            ".xaml"  => "XAML",
            ".html"  => "HTML",
            ".css"   => "CSS",
            ".sql"   => "SQL",
            ".fs"    => "F#",
            ".go"    => "Go",
            ".rs"    => "Rust",
            ".java"  => "Java",
            ".kt"    => "Kotlin",
            ".swift" => "Swift",
            ".rb"    => "Ruby",
            ".php"   => "PHP",
            ".md"    => "Markdown",
            ".sh"    => "Shell",
            ".ps1"   => "PowerShell",
            _        => "code"
        };
    }
}
