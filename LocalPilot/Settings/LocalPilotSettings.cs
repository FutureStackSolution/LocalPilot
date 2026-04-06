using System;

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
        // ── Models ─────────────────────────────────────────────────────────────
        public string CompletionModel  { get; set; } = "llama3:8b";
        public string ChatModel        { get; set; } = "llama3:8b";
        public string ExplainModel     { get; set; } = "llama3:8b";
        public string RefactorModel    { get; set; } = "llama3:8b";
        public string DocModel         { get; set; } = "llama3:8b";
        public string ReviewModel      { get; set; } = "llama3:8b";

        // ── Inline completion behaviour ────────────────────────────────────────
        public bool   EnableInlineCompletion { get; set; } = true;
        public int    CompletionDelayMs      { get; set; } = 600;   // debounce
        public int    MaxCompletionTokens    { get; set; } = 256;
        public int    MaxChatTokens          { get; set; } = 4096;
        public double Temperature            { get; set; } = 0.2;
        public bool   ShowCompletionGhost    { get; set; } = true;  // ghost-text

        // ── Context window ─────────────────────────────────────────────────────
        public int    ContextLinesBefore { get; set; } = 60;
        public int    ContextLinesAfter  { get; set; } = 10;

        // ── Chat panel ─────────────────────────────────────────────────────────
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
        public bool   EnableLogging      { get; set; } = false;

        // ── Prompt Customization ───────────────────────────────────────────────
        public string SystemPrompt { get; set; } = 
            "You are LocalPilot, a world-class AI pair programmer for Visual Studio. " +
            "Your goal is to help the user write, explain, and refactor production-ready, high-performance code. " +
            "RULES: 1. Be extremely concise and skip pleasantries. 2. Use professional, clean markdown formatting. " +
            "3. Always prioritize the provided <PROJECT_SOURCE_CONTEXT> if available. " +
            "4. If you don't know the answer from the context, admit it instead of hallucinating.";

        public string ExplainPrompt { get; set; } = "Explain the following code clearly and concisely:{codeBlock}";
        
        public string RefactorPrompt { get; set; } = 
            "Refactor the following code to improve readability, performance and best practices. " +
            "RETURN ONLY THE REFACTORED CODE BLOCK without extra explanation if possible:{codeBlock}";
        
        public string DocumentPrompt { get; set; } = 
            "Add XML documentation comments (summary, params, returns) for the following code and return only the documented code block:{codeBlock}";
        
        public string ReviewPrompt { get; set; } = 
            "Perform a rigorous security and quality review of the following code. " +
            "Identify potential bugs, performance bottlenecks, and security vulnerabilities. " +
            "Provide specific, actionable suggestions for improvement:{codeBlock}";
        
        public string FixPrompt { get; set; } = 
            "Identify and fix all issues in the following code. " +
            "RETURN ONLY THE FIXED CODE BLOCK without extra explanation if possible:{codeBlock}";
        
        public string TestPrompt { get; set; } = 
            "Write comprehensive unit tests using xUnit for the following code:{codeBlock}";

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
