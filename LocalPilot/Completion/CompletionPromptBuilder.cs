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
        /// </summary>
        public string Build(string fileExtension, string prefix, string suffix, string filePath)
        {
            string langHint = GetLanguageHint(fileExtension);

            // Trim to configured context window (Golden Ratio: 64 before, 16 after)
            prefix = TrimLines(prefix, 64, fromEnd: true);
            suffix = TrimLines(suffix, 16, fromEnd: false);

            var vars = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Language", langHint },
                { "FileName", System.IO.Path.GetFileName(filePath) },
                { "Prefix",   prefix },
                { "Suffix",   suffix }
            };

            return Services.PromptLoader.GetPrompt("CompletionPrompt", vars);
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
