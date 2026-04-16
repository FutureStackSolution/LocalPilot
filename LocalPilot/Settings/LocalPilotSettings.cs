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
        HighAccuracy,
        Custom
    }

    [Serializable]
    public class LocalPilotSettings
    {
        private PerformanceMode _mode = PerformanceMode.Standard;
        private double _temperature = 0.2;
        private int _maxChatTokens = 4096;

        public PerformanceMode Mode 
        { 
            get => _mode; 
            set 
            {
                if (_mode == value) return;
                _mode = value;
                if (_mode != PerformanceMode.Custom)
                {
                    ApplyModePresets();
                }
            }
        }

        // ── Connection ─────────────────────────────────────────────────────────
        public string OllamaBaseUrl    { get; set; } = "http://localhost:11434";

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
        
        public int MaxChatTokens
        {
            get => _maxChatTokens;
            set
            {
                if (_maxChatTokens == value) return;
                _maxChatTokens = value;
                _mode = PerformanceMode.Custom;
            }
        }

        public double Temperature
        {
            get => _temperature;
            set
            {
                if (Math.Abs(_temperature - value) < 0.001) return;
                _temperature = value;
                _mode = PerformanceMode.Custom;
            }
        }

        public bool   ShowCompletionGhost    { get; set; } = true;  // ghost-text


        // ── Context window ─────────────────────────────────────────────────────
        public int    ContextLinesBefore { get; set; } = 60;
        public int    ContextLinesAfter  { get; set; } = 10;

        // ── Chat panel ─────────────────────────────────────────────────────────
        public int    ChatHistoryMaxItems { get; set; } = 50;

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
        public int    MaxMapSizeKB       { get; set; } = 600;

        // ── UI Preferences ────────────────────────────────────────────────────

        public string AccentColor        { get; set; } = "#7C6AF7";    // purple
        public bool   ShowStatusBar      { get; set; } = true;
        public bool   EnableLogging      { get; set; } = false;

        // ── Prompt Customization ───────────────────────────────────────────────
        public string SystemPrompt { get; set; } = 
            "You are LocalPilot, a world-class AI pair programmer for Visual Studio. " +
            "Your goal is to help the user write, explain, and refactor production-ready, high-performance code. " +
            "RULES: 1. Be extremely concise and skip pleasantries. 2. Use professional, clean markdown formatting. " +
            "3. Always prioritize the provided <PROJECT_SOURCE_CONTEXT> snippets. " +
            "4. CITATIONS: Always cite your sources using the [source: Filename.cs] format when referencing code from the project. " +
            "5. If you don't know the answer from the context, admit it instead of hallucinating.";

        public string ExplainPrompt { get; set; } = "Explain the following code clearly and concisely. Reference specific files using [source: filename] if applicable:{codeBlock}";
        
        public string RefactorPrompt { get; set; } = 
            "Refactor the following code to improve readability, performance and best practices. " +
            "Reference existing patterns using [source: filename]. " +
            "RETURN ONLY THE REFACTORED CODE BLOCK without extra explanation if possible:{codeBlock}";
        
        public string DocumentPrompt { get; set; } = 
            "Add XML documentation comments (summary, params, returns) for the following code and return only the documented code block:{codeBlock}";
        
        public string ReviewPrompt { get; set; } = 
            "Perform a rigorous security and quality review of the following code. " +
            "Identify potential bugs, performance bottlenecks, and security vulnerabilities. " +
            "Cite relevant project files using [source: filename]. " +
            "Provide specific, actionable suggestions for improvement:{codeBlock}";
        
        public string FixPrompt { get; set; } = 
            "Identify and fix all issues in the following code. " +
            "Reference project context using [source: filename] if necessary. " +
            "RETURN ONLY THE FIXED CODE BLOCK without extra explanation if possible:{codeBlock}";
        
        public string TestPrompt { get; set; } = 
            "Write comprehensive unit tests using xUnit for the following code. " +
            "Ensure tests stay consistent with existing project patterns [source: filename]:{codeBlock}";

        // ── Singleton ─────────────────────────────────────────────────────────
        private static LocalPilotSettings _instance;
        public  static LocalPilotSettings Instance
            => _instance ??= new LocalPilotSettings();

        public static void UpdateInstance(LocalPilotSettings updated)
        {
            _instance = updated;
        }

        private void ApplyModePresets()
        {
            switch (_mode)
            {
                case PerformanceMode.Fast:
                    _temperature = 0.7;
                    _maxChatTokens = 1024;
                    break;
                case PerformanceMode.HighAccuracy:
                    _temperature = 0.0;
                    _maxChatTokens = 8192;
                    break;
                case PerformanceMode.Standard:
                default:
                    _temperature = 0.2;
                    _maxChatTokens = 4096;
                    break;
            }
        }
    }
}
