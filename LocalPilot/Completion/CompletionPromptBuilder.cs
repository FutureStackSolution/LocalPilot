using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalPilot.Services;
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

            // Trim to configured context window
            prefix = TrimLines(prefix, _settings.ContextLinesBefore, fromEnd: true);
            suffix = TrimLines(suffix, _settings.ContextLinesAfter,  fromEnd: false);

            return
$@"You are an expert {langHint} developer. Complete the following code precisely.
Return ONLY the completion text, no explanations, no markdown fences.
File: {System.IO.Path.GetFileName(filePath)}

<PRE>
{prefix}</PRE>
<SUF>
{suffix}</SUF>
<MID>";
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
