using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;

namespace LocalPilot.Settings
{
    /// <summary>
    /// All user-configurable settings for LocalPilot.
    /// Values are persisted by Visual Studio's settings storage.
    /// </summary>
    [Serializable]
    public class LocalPilotSettings
    {
        // ── Connection ─────────────────────────────────────────────────────────
        public string OllamaBaseUrl    { get; set; } = "http://localhost:11434";

        // ── Models ─────────────────────────────────────────────────────────────
        public string CompletionModel  { get; set; } = "phi3:mini";
        public string ChatModel        { get; set; } = "phi3:mini";
        public string ExplainModel     { get; set; } = "phi3:mini";
        public string RefactorModel    { get; set; } = "phi3:mini";
        public string DocModel         { get; set; } = "phi3:mini";
        public string ReviewModel      { get; set; } = "phi3:mini";

        // ── Inline completion behaviour ────────────────────────────────────────
        public bool   EnableInlineCompletion { get; set; } = true;
        public int    CompletionDelayMs      { get; set; } = 600;   // debounce
        public int    MaxCompletionTokens    { get; set; } = 128;
        public double Temperature            { get; set; } = 0.2;
        public bool   ShowCompletionGhost    { get; set; } = true;  // ghost-text

        // ── Context window ─────────────────────────────────────────────────────
        public int    ContextLinesBefore { get; set; } = 60;
        public int    ContextLinesAfter  { get; set; } = 10;

        // ── Chat panel ─────────────────────────────────────────────────────────
        public bool   EnableChatPanel    { get; set; } = true;
        public int    ChatHistoryMaxItems { get; set; } = 50;

        // ── Code Actions ──────────────────────────────────────────────────────
        public bool   EnableExplain      { get; set; } = true;
        public bool   EnableRefactor     { get; set; } = true;
        public bool   EnableDocGen       { get; set; } = true;
        public bool   EnableReview       { get; set; } = true;
        public bool   EnableFix          { get; set; } = true;
        public bool   EnableUnitTest     { get; set; } = true;

        // ── UI Preferences ────────────────────────────────────────────────────
        public string AccentColor        { get; set; } = "#7C6AF7";    // purple
        public bool   ShowStatusBar      { get; set; } = true;

        // ── Singleton ─────────────────────────────────────────────────────────
        private static LocalPilotSettings _instance;
        public  static LocalPilotSettings Instance
            => _instance ??= new LocalPilotSettings();

        public static void UpdateInstance(LocalPilotSettings updated)
        {
            _instance = updated;
        }
    }
}
