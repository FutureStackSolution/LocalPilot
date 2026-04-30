using System;

namespace LocalPilot.Settings
{
    /// <summary>
    /// All user-configurable settings for LocalPilot.
    /// Values are persisted by Visual Studio's settings storage.
    /// </summary>
    public enum PerformanceMode
    {
        Fast,
        Standard,
        HighAccuracy
    }

    [Serializable]
    public class LocalPilotSettings
    {
        public PerformanceMode Mode { get; set; } = PerformanceMode.Standard;

        // ── Connection ─────────────────────────────────────────────────────────
        public string OllamaBaseUrl    { get; set; } = "http://localhost:11434";

        // ── Models ─────────────────────────────────────────────────────────────
        public string CompletionModel  { get; set; } = "";
        public string ChatModel        { get; set; } = "";
        public string EmbeddingModel   { get; set; } = "";
        public string ExplainModel     { get; set; } = "";
        public string RefactorModel    { get; set; } = "";
        public string DocModel         { get; set; } = "";
        public string ReviewModel      { get; set; } = "";

        /// <summary>
        /// Returns true if the configured ChatModel looks like a dedicated embedding model.
        /// Embedding models don't support /api/chat and will return HTTP 400 if used for it.
        /// </summary>
        public bool ChatModelIsEmbeddingModel =>
            !string.IsNullOrEmpty(ChatModel) &&
            (ChatModel.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
             ChatModel.Contains("nomic", StringComparison.OrdinalIgnoreCase) ||
             ChatModel.Contains("bge-",  StringComparison.OrdinalIgnoreCase) ||
             ChatModel.Contains("e5-",   StringComparison.OrdinalIgnoreCase));

        // ── Inline completion behaviour ────────────────────────────────────────
        public bool   EnableInlineCompletion { get; set; } = true;
        public int    CompletionDelayMs      { get; set; } = 600;   // debounce
        public int    MaxCompletionTokens    { get; set; } = 256;
        
        public bool   ShowCompletionGhost    { get; set; } = true;  // ghost-text


        // ── Chat panel ─────────────────────────────────────────────────────────
        public int    ChatHistoryMaxItems { get; set; } = 50;
        public float  Temperature         { get; set; } = 0.7f;
        public int    MaxChatTokens       { get; set; } = 4096;

        // ── Agent ─────────────────────────────────────────────────────────────
        public bool   AutonomousModeEnabled    { get; set; } = true;
        public bool   RequireApprovalForWrites { get; set; } = true;
        public bool   EnableExplain      { get; set; } = true;
        public bool   EnableRefactor     { get; set; } = true;
        public bool   EnableDocGen       { get; set; } = true;
        public bool   EnableReview       { get; set; } = true;
        public bool   EnableFix          { get; set; } = true;
        public bool   EnableUnitTest     { get; set; } = true;

        // ── Workspace Snapshot ────────────────────────────────────────────────
        public bool   EnableProjectMap   { get; set; } = true;
        public int    BackgroundIndexingConcurrency { get; set; } = 2;

        // ── UI Preferences ────────────────────────────────────────────────────

        public string AccentColor        { get; set; } = "#7C6AF7";    // purple
        public bool   ShowStatusBar      { get; set; } = true;
        public bool   EnableLogging      { get; set; } = false;

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
