using Community.VisualStudio.Toolkit;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;

namespace LocalPilot.Services
{
    /// <summary>
    /// The heart of Agent Mode. Orchestrates the Plan-Act-Observe loop
    /// using Ollama's NATIVE tool calling API.
    /// 
    /// KEY ARCHITECTURE CHANGE (v3.0):
    /// Instead of parsing ```json blocks from free-text model output, this orchestrator
    /// sends tool definitions via Ollama's /api/chat 'tools' parameter and receives
    /// structured tool_calls in the API response. This eliminates:
    ///   - Text-based JSON parsing (ParseAllToolCalls, SafeParseJson)
    ///   - Nudge messages ("You MUST call tools...")
    ///   - The "reasoning 3x then completing" bug
    /// </summary>
    public class AgentOrchestrator
    {
        private readonly ILlmService _ollama;
        private readonly ToolRegistry _toolRegistry;
        private readonly ProjectContextService _projectContext;
        private readonly ProjectMapService _projectMap;

        public event Action<AgentStatus, string> OnStatusUpdate;
        public event Action<ToolCallRequest> OnToolCallPending;
        public event Action<ToolCallRequest, ToolResponse> OnToolCallCompleted;
        public event Action<string> OnMessageFragment;
        public event Action<string> OnMessageCompleted;
        public event Action<Dictionary<string, (string original, string improved)>> OnTurnModificationsPending;
        public event Action<string> OnPerformanceWarning;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _lastModelTokPerSec
            = new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        
        public Func<ToolCallRequest, Task<bool>> RequestPermissionAsync;

        private Dictionary<string, (string original, string improved)> _stagedChanges = new Dictionary<string, (string original, string improved)>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Indicates if an agentic task is currently running. 
        /// Used by background services to yield CPU/GPU resources.
        /// </summary>
        public bool IsActive { get; private set; }

        public AgentOrchestrator(ILlmService ollama, ToolRegistry toolRegistry, ProjectContextService projectContext, ProjectMapService projectMap)
        {
            _ollama = ollama;
            _toolRegistry = toolRegistry;
            _projectContext = projectContext;
            _projectMap = projectMap;
            _historyCompactor = new HistoryCompactor(ollama);
        }

        private readonly HistoryCompactor _historyCompactor;

        private bool IsConversationalQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (query.StartsWith("/")) return false; // Slash commands are technical

            string clean = query.Trim().ToLowerInvariant()
                .Replace("!", "").Replace(".", "").Replace("?", "").Replace(",", "");

            string[] commonConversational = { 
                "hi", "hello", "hey", "thanks", "thank you", "thanks!", 
                "awesome", "perfect", "good morning", "good afternoon",
                "good evening", "bye", "goodbye", "ok", "okay", "cool", "greetings"
            };

            if (commonConversational.Contains(clean)) return true;

            // Check if query is very short and doesn't contain code/symbols
            var words = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 3)
            {
                bool hasTechnicalIndicator = clean.Contains("{") || clean.Contains("}") || 
                                             clean.Contains("(") || clean.Contains(")") || 
                                             clean.Contains("[") || clean.Contains("]") || 
                                             clean.Contains("<") || clean.Contains(">") || 
                                             clean.Contains(";") || clean.Contains("=") ||
                                             clean.Contains("+") || clean.Contains("-") ||
                                             clean.Contains("*") || clean.Contains("/");
                if (!hasTechnicalIndicator) return true;
            }

            return false;
        }

        /// <summary>
        /// Initiates an autonomous task for the agent.
        /// Uses Ollama's native tool calling API for structured, reliable tool invocation.
        /// </summary>
        public async Task RunTaskAsync(string taskDescription, List<ChatMessage> messages, CancellationToken ct, string modelOverride = null, string contextOverride = null)
        {
            taskDescription = taskDescription ?? string.Empty;
            if (messages == null) messages = new List<ChatMessage>();

            if (_ollama.CircuitBreakerTripped)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Failed, "Ollama is unreachable (Circuit Breaker tripped). Please check your connection.");
                return;
            }

            bool isConversational = IsConversationalQuery(taskDescription);

            IsActive = true;
            GlobalPriorityGuard.StartAgentTurn();
            try
            {
                // 🛡️ GPU VRAM WATCHDOG: Check for resource pressure before starting
                var vram = await _ollama.GetVramStatusAsync(ct);
                if (!vram.IsHealthy)
                {
                    LocalPilotLogger.Log($"[Agent] High GPU VRAM usage detected ({vram.TotalVramUsedMb}MB). Model loading may be slow.", LogCategory.Ollama, LogSeverity.Warning);
                    OnStatusUpdate?.Invoke(AgentStatus.Thinking, "GPU VRAM pressure detected. Models may load slowly...");
                }

                string solutionPath = "unknown";
                string activeSelection = "";
                string activeDocPath = null;
                string activeDocContent = null;
                Community.VisualStudio.Toolkit.DocumentView activeDoc = null;

                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var sol = await VS.Solutions.GetCurrentSolutionAsync();
                    if (sol != null && !string.IsNullOrEmpty(sol.FullPath))
                    {
                        solutionPath = Path.GetDirectoryName(sol.FullPath);
                    }

                    if (!isConversational)
                    {
                        activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                        if (activeDoc?.FilePath != null)
                        {
                            activeDocPath = activeDoc.FilePath;
                            activeDocContent = activeDoc.TextBuffer?.CurrentSnapshot?.GetText();

                            // Get selection if available
                            if (activeDoc.TextView?.Selection != null && activeDoc.TextView.Selection.SelectedSpans.Count > 0)
                            {
                                activeSelection = activeDoc.TextView.Selection.SelectedSpans[0].GetText();
                            }
                            if (string.IsNullOrWhiteSpace(activeSelection) && activeDocContent != null)
                            {
                                activeSelection = activeDocContent;
                            }
                        }
                    }
                }
                catch { }

                // Fallback workspace root if no solution is loaded
                if (string.IsNullOrEmpty(solutionPath) || solutionPath == "unknown")
                {
                    if (!string.IsNullOrEmpty(activeDocPath))
                    {
                        try
                        {
                            solutionPath = Path.GetDirectoryName(activeDocPath);
                        }
                        catch { }
                    }
                }

                _toolRegistry.WorkspaceRoot = solutionPath;
                _toolRegistry.ActiveDocumentPath = activeDocPath;
                
                // 🚀 MODEL SELECTION: Use specific model for tasks (Explain, Refactor, etc) if provided
                string contextModel = modelOverride ?? LocalPilot.Settings.LocalPilotSettings.Instance.ChatModel;

                // Context auto-compaction: prune if history is getting too deep
                var originalCount = messages.Count;
                var compactedMessages = await _historyCompactor.CompactIfNeededAsync(messages, contextModel);
                
                // If compaction happened, we swap the list content (preserving the reference if possible, 
                // but since we return it to the UI, we'll just update the local variable here).
                if (compactedMessages.Count < messages.Count)
                {
                    messages.Clear();
                    messages.AddRange(compactedMessages);
                    LocalPilotLogger.Log($"[Orchestrator] Context Compacted: {originalCount} -> {messages.Count} messages.");
                }

                // 🚀 CONTEXT HYGIENE: Prune transient system prompts and stale metadata from previous turns
                // This prevents massive multi-turn context accumulation that chokes local and CPU-offloaded models.
                messages.RemoveAll(m => m.Role == "system" && (
                    m.Content.Contains("<identity>") || 
                    m.Content.Contains("LocalPilot, an elite") ||
                    m.Content.Contains("{solutionPath}") ||
                    m.Content.StartsWith("## ACTIVE DIAGNOSTICS") ||
                    m.Content.StartsWith("## GIT STATE") ||
                    m.Content.StartsWith("## ACTIVE TAB") ||
                    m.Content.StartsWith("## ENV") ||
                    m.Content.StartsWith("## GROUNDING CONTEXT") ||
                    m.Content.StartsWith("## ACTIVE EDITOR SNIPPET") ||
                    m.Content.StartsWith("## ACTIVE FILE CONTENT") ||
                    m.Content.StartsWith("## TARGET FILE CONTENT") ||
                    m.Content.StartsWith("## NEXUS CONTEXT") ||
                    m.Content.StartsWith("## WORKSPACE SYMBOLS") ||
                    m.Content.StartsWith("## OODA ORIENTATION") ||
                    m.Content.StartsWith("[LSP CONTEXT]") ||
                    m.Content.StartsWith("## SEMANTIC NEIGHBORHOOD") ||
                    m.Content.StartsWith("INTELLIGENCE: LSP Bridge") ||
                    m.Content.StartsWith("INTELLIGENCE: Polyglot Heuristics") ||
                    m.Content.Contains("[TRUNCATED OLD CONTEXT]")
                ));

                // 🚀 COMPACT PAST TOOL RESULTS: Large tool outputs (e.g. read_file of entire files)
                // from previous turns must be compacted so they do not cause prefill paralysis.
                foreach (var m in messages.Where(msg => msg.Role == "tool"))
                {
                    if (m.Content != null && m.Content.Length > 800)
                    {
                        m.Content = m.Content.Substring(0, 300) + 
                                    $"\n... [Tool output truncated to save context ({m.Content.Length} chars total)] ...\n" + 
                                    m.Content.Substring(m.Content.Length - 150);
                    }
                }

                var turnFileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 🚀 FAST MODE 2-ZONE KV-CACHE HISTORY:
                // Strip stale system prompts, old file dumps, and raw tool outputs from prior turns,
                // while preserving the last 2 user/assistant exchanges (compact) so follow-ups still work!
                bool fastMode = LocalPilot.Settings.LocalPilotSettings.Instance.Mode == LocalPilot.Settings.PerformanceMode.Fast;
                if (fastMode && messages.Count > 0)
                {
                    var recentTurns = messages
                        .Where(m => (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrWhiteSpace(m.Content))
                        .Reverse()
                        .Take(4) // Last 2 user+assistant pairs
                        .Reverse()
                        .Select(m => new ChatMessage
                        {
                            Role = m.Role,
                            Content = m.Content.Length > 350 ? m.Content.Substring(0, 350) + "..." : m.Content
                        })
                        .ToList();

                    messages.Clear();
                    messages.AddRange(recentTurns);
                    LocalPilotLogger.Log($"[Orchestrator] FastMode 2-zone history: retained {recentTurns.Count} recent messages, purged stale context.", LogCategory.Agent);
                }

                // 🚀 SMART SYSTEM PROMPT: Tailor the foundation based on the task intent
                string systemPrompt;
                bool isReadOnlyAction = taskDescription.Contains("<task_type>code_explanation") || 
                                        taskDescription.Contains("<task_type>documentation_generation") ||
                                        taskDescription.Contains("<task_type>code_review") ||
                                        taskDescription.Contains("/explain") ||
                                        taskDescription.Contains("/review") ||
                                        taskDescription.Contains("/doc");

                if (isConversational)
                {
                    systemPrompt = "You are LocalPilot, an AI coding assistant. Give a short, direct, friendly, and conversational response.";
                }
                else if (fastMode)
                {
                    // 🚀 ULTRA-LEAN PROMPT FOR FAST/OFFLOADED LOCAL INFERENCE:
                    // Massive multi-section prompts blow up prefill on local GPUs (Quadro, 4GB-8GB VRAM)
                    // and force disk swapping. A focused 3-line prompt saves ~600 tokens of prefill latency.
                    systemPrompt = "You are LocalPilot, an elite local AI engineer. Execute ONLY the user's requested code change. " +
                                   "Use 'edit_lines' (preferred, by line numbers) or 'replace_text' directly on Turn 1 with complete code. " +
                                   "Leave all unrequested methods 100% untouched. Never generate dummy stubs or throw null.";
                }
                else
                {
                    systemPrompt = PromptLoader.GetPrompt("SystemPrompt", new Dictionary<string, string> { { "solutionPath", solutionPath } });
                    if (isReadOnlyAction && !string.IsNullOrEmpty(systemPrompt))
                    {
                        // Strip the 'Worker' protocol for informational tasks to save context and prevent hallucinated tool calls
                        systemPrompt = System.Text.RegularExpressions.Regex.Replace(systemPrompt, @"(?s)<smart_fix_protocol>.*?</smart_fix_protocol>", string.Empty);
                        systemPrompt = System.Text.RegularExpressions.Regex.Replace(systemPrompt, @"(?s)<tool_usage>.*?</tool_usage>", string.Empty);
                    }
                }

                messages.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt ?? "You are LocalPilot." });

                // Inject project-specific rules if they haven't been added yet
                if (!isConversational && !messages.Any(m => m.Content.Contains("PROJECT-SPECIFIC RULES")))
                {
                    try
                    {
                        string rulesPath = Path.Combine(solutionPath, "LOCALPILOT.md");
                        if (File.Exists(rulesPath))
                        {
                            string localRules = File.ReadAllText(rulesPath);
                            messages.Add(new ChatMessage { Role = "system", Content = $"## PROJECT-SPECIFIC RULES\n{localRules}" });
                        }
                    }
                    catch { }
                }

                // Determine if this is a specialized Quick Action (Explain, Document, etc)
                bool isQuickAction = !string.IsNullOrEmpty(modelOverride);

                // Fetch initial grounding context from the project (Skip for simple Quick Actions/Conversational)
                bool activeFileMentionedInPrompt = !string.IsNullOrEmpty(activeDocPath) && !string.IsNullOrEmpty(taskDescription) &&
                    (taskDescription.IndexOf(Path.GetFileName(activeDocPath), StringComparison.OrdinalIgnoreCase) >= 0 ||
                     taskDescription.IndexOf(Path.GetFileNameWithoutExtension(activeDocPath), StringComparison.OrdinalIgnoreCase) >= 0 ||
                     taskDescription.Replace(" ", "").IndexOf(Path.GetFileNameWithoutExtension(activeDocPath), StringComparison.OrdinalIgnoreCase) >= 0);

                bool isDirectFileAction = activeDocContent != null || activeFileMentionedInPrompt;

                // ═══════════════════════════════════════════════════════════════════
                // NATIVE TOOL DEFINITIONS
                // ═══════════════════════════════════════════════════════════════════
                // 🚀 ULTRA-LEAN TOOL SHIELD:
                // When modifying an active file in Fast mode, the file is ALREADY in context.
                // Register ONLY 'edit_lines' and 'replace_text' (saving ~400 tokens of tool schemas and preventing prefill bloat).
                bool essentialOnly = LocalPilot.Settings.LocalPilotSettings.Instance.Mode == LocalPilot.Settings.PerformanceMode.Fast;
                var allowedTools = (isDirectFileAction && fastMode) ? new[] { "edit_lines", "replace_text" } : null;
                var toolDefinitions = (isReadOnlyAction || isConversational) 
                    ? new List<OllamaToolDefinition>() 
                    : _toolRegistry.GetOllamaToolDefinitions(essentialOnly, allowedTools);
                LocalPilotLogger.Log($"[Agent] Registered {toolDefinitions.Count} native tools for Ollama (ReadOnly: {isReadOnlyAction}, FastMode: {essentialOnly}, DirectFile: {isDirectFileAction})", LogCategory.Agent);

                // 🚀 SURGICAL CONTEXT GATING:
                // When modifying an active or mentioned file (or in Fast mode), DO NOT inject extraneous
                // Git status, environment details, or noisy error lists that blow up the prompt to 4,000+ tokens!
                bool shouldInjectUnifiedContext = !isConversational && !isQuickAction && !isDirectFileAction && !fastMode;
                if (shouldInjectUnifiedContext && !messages.Any(m => m.Content.Contains("ACTIVE DIAGNOSTICS")))
                {
                    string unifiedContext = await GetUnifiedContextAsync(solutionPath, ct);
                    if (!string.IsNullOrEmpty(unifiedContext))
                    {
                        messages.Add(new ChatMessage { Role = "system", Content = unifiedContext });
                    }
                }
                
                // Fetch initial grounding context from the project (Skip for Direct File Actions / Fast mode)
                bool shouldSearchVectors = !isQuickAction && !isConversational && !activeFileMentionedInPrompt && !isDirectFileAction && !fastMode;
                if (shouldSearchVectors)
                {
                    string groundingContext = contextOverride;
                    if (string.IsNullOrEmpty(groundingContext)) {
                        // Limit to top 2 chunks to prevent prefill bloat
                        groundingContext = await _projectContext.SearchContextAsync(_ollama, taskDescription, topN: 2);
                    }
                    
                    if (!string.IsNullOrEmpty(groundingContext))
                    {
                        if (groundingContext.Length > 2000)
                        {
                            groundingContext = groundingContext.Substring(0, 2000) + "\n... </grounding_context>";
                        }
                        messages.Add(new ChatMessage { Role = "system", Content = $"## GROUNDING CONTEXT (VECTORS)\n{groundingContext}" });
                    }
                }

                // Fetch active document info ONCE to avoid redundant UI thread switches
                if (!isConversational && activeDoc != null && activeDocPath != null)
                {
                    try
                    {
                        // In Fast mode or direct file action, skip neighborhood dump to keep prompt lean (< 800 tokens)
                        if (!fastMode && !isDirectFileAction)
                        {
                            string neighborhood = await SymbolIndexService.Instance.GetNeighborhoodContextAsync(activeDocPath, ct);
                            if (!string.IsNullOrEmpty(neighborhood))
                            {
                                if (neighborhood.Length > 1500) neighborhood = neighborhood.Substring(0, 1500) + "\n... [neighborhood truncated]";
                                messages.Add(new ChatMessage { Role = "system", Content = neighborhood });
                            }
                        }

                        // 🎯 UNIFIED TARGET FILE RESOLVER:
                        // Prioritize the file explicitly mentioned in the user prompt (e.g. SampleDataProvider.cs).
                        // If none mentioned, fall back to the active editor document.
                        // NEVER inject both files at the same time, which was bloating input to 2,100+ tokens!
                        string targetDocPath = null;
                        string targetDocContent = null;

                        if (!string.IsNullOrEmpty(solutionPath) && !string.IsNullOrEmpty(taskDescription))
                        {
                            try
                            {
                                var nexus = NexusService.Instance.GetGraph();
                                if (nexus?.Nodes == null || nexus.Nodes.Count == 0)
                                {
                                    await NexusService.Instance.InitializeAsync(solutionPath);
                                    await NexusService.Instance.RebuildGraphAsync(ct);
                                    nexus = NexusService.Instance.GetGraph();
                                }
                                if (nexus?.Nodes != null)
                                {
                                    var targetNode = nexus.Nodes.FirstOrDefault(n => 
                                        !string.IsNullOrEmpty(n.FilePath) && 
                                        (taskDescription.IndexOf(Path.GetFileName(n.FilePath), StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         taskDescription.Replace(" ", "").IndexOf(Path.GetFileNameWithoutExtension(n.FilePath), StringComparison.OrdinalIgnoreCase) >= 0));
                                    
                                    if (targetNode != null && File.Exists(targetNode.FilePath))
                                    {
                                        targetDocPath = targetNode.FilePath;
                                        targetDocContent = File.ReadAllText(targetDocPath);
                                        turnFileCache[targetDocPath] = targetDocContent;
                                    }
                                }
                            }
                            catch { }
                        }

                        // If no specific file was mentioned in the prompt, use the active document
                        if (string.IsNullOrEmpty(targetDocPath) && !string.IsNullOrEmpty(activeDocContent))
                        {
                            targetDocPath = activeDocPath;
                            targetDocContent = activeDocContent;
                            turnFileCache[targetDocPath] = targetDocContent;
                        }

                        if (!string.IsNullOrEmpty(targetDocContent) && !string.IsNullOrEmpty(targetDocPath))
                        {
                            // 🚀 ROSLYN AST SKELETONIZATION: Compress non-target method bodies for files >1,500 chars
                            // Keeps the target method (GetNames) 100% intact while dropping thousands of mock data tokens.
                            string deliveredContent = targetDocContent;
                            int cursorLine = 0;
                            if (targetDocPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && deliveredContent.Length > 1500)
                            {
                                if (targetDocPath.Equals(activeDocPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    try {
                                        var sel = activeDoc.TextView?.Selection;
                                        if (sel?.ActivePoint != null) cursorLine = sel.ActivePoint.Position.GetContainingLine().LineNumber;
                                    } catch { }
                                }
                                deliveredContent = CodeSkeletonizer.SkeletonizeWithLineNumbers(deliveredContent, taskDescription, cursorLine);
                            }
                            else
                            {
                                deliveredContent = AddLineNumbers(deliveredContent);
                            }

                            if (deliveredContent.Length <= 25000)
                            {
                                messages.Add(new ChatMessage { 
                                    Role = "system", 
                                    Content = $"## TARGET FILE CONTENT ({Path.GetFileName(targetDocPath)})\nPath: {targetDocPath}\nCode (with 1-based line numbers):\n```\n{deliveredContent}\n```\nIMPORTANT: The target file is provided above. You ALREADY have the complete context. DO NOT call 'read_file'. Execute 'edit_lines' or 'replace_text' directly on Turn 1!" 
                                });
                            }
                        }
                    }
                    catch { }
                }

                // Nexus intelligence: inject cross-language dependency awareness (Skip for direct file actions / Fast mode)
                if (!isConversational && !isDirectFileAction && !fastMode)
                {
                    try
                    {
                        var nexus = NexusService.Instance.GetGraph();
                        if (nexus.Nodes.Any() && activeDocPath != null)
                        {
                            var node = nexus.Nodes.FirstOrDefault(n => n.FilePath != null && n.FilePath.Equals(activeDocPath, StringComparison.OrdinalIgnoreCase));
                            if (node != null)
                            {
                                var deps = nexus.Edges.Where(e => e.FromId == node.Id).ToList();
                                if (deps.Any())
                                {
                                    var sb = new System.Text.StringBuilder();
                                    sb.AppendLine("## NEXUS CONTEXT (Stack Dependencies)");
                                    sb.AppendLine($"Active File '{node.Name}' connects to {deps.Count} resources.");
                                    
                                    foreach (var edge in deps.Take(5))
                                    {
                                        var target = nexus.Nodes.FirstOrDefault(n => n.Id == edge.ToId);
                                        sb.AppendLine($" - {target?.Name ?? edge.ToId} ({target?.Type})");
                                    }
                                    if (deps.Count > 5) sb.AppendLine($" ... and {deps.Count - 5} other architectural links.");
                                    
                                    messages.Add(new ChatMessage { Role = "system", Content = sb.ToString() });
                                }
                            }
                        }
                    }
                    catch { }
                }

            // Handle Slash Commands
            string processedTask = taskDescription.Trim();
            if (processedTask.StartsWith("/") && !processedTask.Contains(" "))
            {
                var cmd = processedTask.ToLowerInvariant();
                var settings = LocalPilot.Settings.LocalPilotSettings.Instance;

                if (cmd == "/explain")       processedTask = PromptLoader.GetPrompt("ExplainPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/fix")      processedTask = PromptLoader.GetPrompt("FixPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/test")     processedTask = PromptLoader.GetPrompt("TestPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/refactor")  processedTask = PromptLoader.GetPrompt("RefactorPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/rename")    processedTask = PromptLoader.GetPrompt("RenamePrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/review")    processedTask = PromptLoader.GetPrompt("ReviewPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/doc" || cmd == "/document") processedTask = PromptLoader.GetPrompt("DocumentPrompt", new Dictionary<string, string> { { "codeBlock", activeSelection } });
                else if (cmd == "/map")      
                {
                    OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Updating Project Map...");
                    await _projectMap.GenerateProjectMapAsync(solutionPath, maxTotalBytes: 2048 * 1024);
                    processedTask = "Please provide an executive summary of the project structure based on the updated map.";
                }
            }
            else if (processedTask.StartsWith("/"))
            {
                var parts = processedTask.Split(new[] { ' ' }, 2);
                var cmd = parts[0].ToLowerInvariant();
                var args = parts[1].Trim();

                if (cmd == "/explain")       processedTask = PromptLoader.GetPrompt("ExplainPrompt", new Dictionary<string, string> { { "codeBlock", args } });
                else if (cmd == "/fix")      processedTask = PromptLoader.GetPrompt("FixPrompt", new Dictionary<string, string> { { "codeBlock", args } });
                else if (cmd == "/test")     processedTask = PromptLoader.GetPrompt("TestPrompt", new Dictionary<string, string> { { "codeBlock", args } });
                else if (cmd == "/refactor")  processedTask = PromptLoader.GetPrompt("RefactorPrompt", new Dictionary<string, string> { { "codeBlock", args } });
                else if (cmd == "/review")    processedTask = PromptLoader.GetPrompt("ReviewPrompt", new Dictionary<string, string> { { "codeBlock", args } });
                else if (cmd == "/doc" || cmd == "/document") processedTask = PromptLoader.GetPrompt("DocumentPrompt", new Dictionary<string, string> { { "codeBlock", args } });
            }

            // 🔍 FILE NESTING: Use Roslyn/LSP to find definitions for potential symbols in the task
            // This provides the model with structural awareness of the code it's discussing.
            // In Fast mode, the target file is already in context; skip LSP symbol lookups to keep context ultra-lean.
            if (!isConversational && !fastMode)
            {
                var words = processedTask.Split(new[] { ' ', '.', '(', ')', '[', ']', '<', '>', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
                
                // 🚀 PERFORMANCE SHIELD: Filter and limit the number of symbol lookups to prevent pre-turn hangs.
                var PascalNames = words
                    .Where(w => w.Length >= 4 && char.IsUpper(w[0]) && !w.All(char.IsUpper)) 
                    .Distinct()
                    .Take(5); 

                using (var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    discoveryCts.CancelAfter(5000);
                    try
                    {
                        foreach (var name in PascalNames)
                        {
                            if (discoveryCts.Token.IsCancellationRequested) break;
                            
                            var locs = await SymbolIndexService.Instance.FindDefinitionsAsync(name, discoveryCts.Token);
                            if (locs != null && locs.Any())
                            {
                                var loc = locs.First();
                                messages.Add(new ChatMessage { Role = "system", Content = $"[LSP CONTEXT] Symbol '{name}' ({loc.Kind}) is defined at: {loc.FilePath}:{loc.Line}" });
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }

                // Inject known symbols summary for fuzzy matching
                string symbolSummary = SymbolIndexService.Instance.GetSummary();
                if (!string.IsNullOrEmpty(symbolSummary))
                {
                    messages.Add(new ChatMessage { Role = "system", Content = $"## WORKSPACE SYMBOLS\n{symbolSummary}" });
                }
            }

            // Finally, the user request with a task header
            // 🚀 KV-CACHE STABILITY: By adding the user task AFTER the semantic discovery, 
            // we ensure that the discovery results (which are derived from the task) are 
            // part of the stable prefix that never changes across turns.
            messages.Add(new ChatMessage { Role = "user", Content = $"## TASK\n{processedTask}" });

            // ═══════════════════════════════════════════════════════════════════
            // AGENT LOOP: Native tool calling — no parsing, no nudging
            // ═══════════════════════════════════════════════════════════════════
            bool isDone = false;
            int maxSteps = 20;
            int step = 0;
            _stagedChanges.Clear();
            string lastToolCallSignature = null;
            int repeatCount = 0;

            // 🚀 HARDWARE-AWARE PRE-FLIGHT CHECK: Warn if model previously spilled to SSD (< 5 tok/s) or VRAM is under pressure
            if (!string.IsNullOrEmpty(contextModel) && _lastModelTokPerSec.TryGetValue(contextModel, out double prevTps) && prevTps < 5.0)
            {
                OnPerformanceWarning?.Invoke($"This model is offloading to disk (~{prevTps:F1} tok/s). Switch to a 3B–7B model for 30x speed.");
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var vram = await _ollama.GetVramStatusAsync(ct).ConfigureAwait(false);
                        if (vram != null && !vram.IsHealthy)
                        {
                            OnPerformanceWarning?.Invoke("GPU VRAM is under pressure — model may offload to disk. Switch to a 3B–7B model for faster responses.");
                        }
                    }
                    catch { }
                }, ct);
            }

            while (!isDone && step < maxSteps)
            {
                step++;
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Analyzing context and planning next step...");

                // 1. Get LLM response with native tool support
                var responseBuilder = new System.Text.StringBuilder();
                var toolCallsThisTurn = new List<ToolCallRequest>();

                // 🚀 STREAM INTERCEPTOR: Buffer and suppress tool-text leaking to UI
                var options = ApplyPerformancePresets(LocalPilot.Settings.LocalPilotSettings.Instance.Mode, messages, isQuickAction);
                
                // OODA ORIENTATION: Inject Turn Summary to prevent amnesia
                if (step > 1)
                {
                    // 🚀 KV-CACHE HYGIENE: Only remove the previous OODA orientation.
                    // We keep the LSP context and other system messages in their original positions 
                    // to maintain the stable prefix for Ollama's prompt caching.
                    messages.RemoveAll(m => m.Role == "system" && m.Content.StartsWith("## OODA ORIENTATION"));
                    
                    string historySummary = GenerateActionHistorySummary(messages);
                    messages.Add(new ChatMessage { Role = "system", Content = $"## OODA ORIENTATION (Turn {step})\n{historySummary}\n\nSTAY FOCUSED: Proceed to the next step of your plan." });
                }

                LocalPilotLogger.Log($"[Agent] Turn {step}: Running model {contextModel} with context={options.NumCtx}", LogCategory.Agent);

                var turnSw = Stopwatch.StartNew();
                int estimatedTokens = 0;

                // 🚀 SMART CONTEXT TRUNCATION: Prevent 'Prefill Bloat' for 90k+ token projects.
                // We prune the context IN Visual Studio so Ollama doesn't have to discard it slowly.
                var prunedMessages = PruneContextToLimit(messages, options.NumCtx);

                // Stream with native tool calling
                await foreach (var result in _ollama.StreamChatWithToolsAsync(contextModel, prunedMessages, toolDefinitions, options, ct))
                {
                    if (result.IsTextToken)
                    {
                        string token = result.TextToken;
                        responseBuilder.Append(token);
                        estimatedTokens++; 

                        // 🚀 NARRATIVE STREAMING: Pass tokens to UI for a 'live' feel.
                        OnMessageFragment?.Invoke(token);
                    }
                    else if (result.IsToolProgress)
                    {
                        int approxTokens = Math.Max(1, result.ToolArgChars / 4);
                        double elapsedSec = Math.Max(0.1, turnSw.Elapsed.TotalSeconds);
                        double tokPerSec = approxTokens / elapsedSec;
                        OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Generating {result.ToolName ?? "tool"} ({approxTokens} tokens, {tokPerSec:F1} tok/s)...");
                    }
                    else if (result.IsToolCall)
                    {
                        // Structured tool call from Ollama — no parsing needed!
                        toolCallsThisTurn.Add(new ToolCallRequest
                        {
                            Name = result.ToolName,
                            Arguments = result.ToolArguments
                        });
                    }
                }

                turnSw.Stop();
                var responseText = responseBuilder.ToString();
                
                // 📊 ACCURATE TOKEN TRACKING:
                // Include tool call arguments (code replacements, file edits) in token calculation
                // so tool-calling turns never falsely report 0 tokens.
                int toolCallChars = 0;
                foreach (var tc in toolCallsThisTurn)
                {
                    if (tc.Arguments != null)
                    {
                        toolCallChars += Newtonsoft.Json.JsonConvert.SerializeObject(tc.Arguments).Length;
                    }
                }

                if (estimatedTokens == 0)
                {
                    int totalChars = (responseText?.Length ?? 0) + toolCallChars;
                    estimatedTokens = totalChars > 0 ? Math.Max(1, (int)(totalChars / 3.5)) : 0;
                }
                else if (toolCallChars > 0)
                {
                    estimatedTokens += Math.Max(1, (int)(toolCallChars / 3.5));
                }
                
                // Record metrics
                PerformanceTracer.Instance.RecordTurn(taskDescription, step, turnSw.ElapsedMilliseconds, estimatedTokens, contextModel);
                if (!string.IsNullOrEmpty(contextModel) && estimatedTokens >= 10 && turnSw.Elapsed.TotalSeconds > 2.0)
                {
                    double tokPerSec = estimatedTokens / turnSw.Elapsed.TotalSeconds;
                    _lastModelTokPerSec[contextModel] = tokPerSec;
                    if (tokPerSec < 5.0)
                    {
                        OnPerformanceWarning?.Invoke($"This model is offloading to disk (~{tokPerSec:F1} tok/s). Switch to a 3B–7B model for 30x speed.");
                    }
                }
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Finished turn {step} in {turnSw.ElapsedMilliseconds}ms ({estimatedTokens} tokens)");

                var cleanText = StripToolCalls(responseText);

                // 🚀 UI CLEANUP: Always invoke message completion to ensure the 'rendered' 
                // version (which has tool calls stripped) overwrites the 'streaming' noise.
                OnMessageCompleted?.Invoke(cleanText);

                // Add assistant response to history
                messages.Add(new ChatMessage { Role = "assistant", Content = responseText });

                // 2. [FALLBACK] If no native tool calls, check for JSON blocks in text
                if (!toolCallsThisTurn.Any() && !string.IsNullOrWhiteSpace(responseText))
                {
                    var fallbackCalls = ParseJsonToolCalls(responseText);
                    if (fallbackCalls.Any())
                    {
                        LocalPilotLogger.Log($"[Agent] Found {fallbackCalls.Count} fallback tool calls in text output.");
                        toolCallsThisTurn.AddRange(fallbackCalls);
                    }
                }

                // 3. Process tool calls (if any)
                if (toolCallsThisTurn.Any())
                {
                    bool stepHasMeaningfulResult = false;
                    
                    // Filter out unknown tools
                    var validToolCalls = toolCallsThisTurn
                        .Where(tc => _toolRegistry.HasTool(tc.Name))
                        .ToList();

                    if (!validToolCalls.Any())
                    {
                        LocalPilotLogger.Log("[Agent] All tool calls were for unknown tools — skipping.");
                        foreach (var unknownTc in toolCallsThisTurn)
                        {
                            LocalPilotLogger.Log($"[Agent] Unknown tool: {unknownTc.Name}");
                        }
                        // Add error feedback so model can correct itself
                        messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = $"Error: Tool(s) not found: {string.Join(", ", toolCallsThisTurn.Select(t => t.Name))}. Available tools: {string.Join(", ", _toolRegistry.GetAllTools().Select(t => t.Name))}"
                        });
                        continue;
                    }

                    OnStatusUpdate?.Invoke(AgentStatus.Executing, $"Running {validToolCalls.Count} tools...");

                    // Loop detection
                    string currentSignature = string.Join(";", validToolCalls.OrderBy(t => t.Name).Select(t => BuildToolSignature(t)));
                    if (currentSignature == lastToolCallSignature)
                    {
                        repeatCount++;
                        if (repeatCount >= 2)
                        {
                            LocalPilotLogger.Log("[Agent] Loop detected — forcing completion.");
                            isDone = true;
                            OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task stopped (agent was repeating actions).");
                            break;
                        }
                    }
                    else
                    {
                        repeatCount = 0;
                    }
                    lastToolCallSignature = currentSignature;

                    // 🚀 WORLD-CLASS PERFORMANCE: Parallel Execution Engine
                    // Split tools into 'Safe' (Read-only) and 'Risky' (Mutating)
                    var safeTools = new List<ToolCallRequest>();
                    var riskyTools = new List<ToolCallRequest>();

                    foreach (ToolCallRequest tc in validToolCalls)
                    {
                        bool isRisky = tc.Name == "write_file" || 
                                       tc.Name == "replace_text" || 
                                       tc.Name == "edit_lines" ||
                                       tc.Name == "delete_file" || 
                                       tc.Name == "rename_symbol" || 
                                       tc.Name == "run_terminal" ||
                                       tc.Name == "write_to_file" ||
                                       tc.Name == "replace_file_content";
                        
                        if (isRisky) riskyTools.Add(tc);
                        else safeTools.Add(tc);
                    }

                    // 1. Execute Safe Tools in Parallel (Speed boost for multi-file analysis)
                    if (safeTools.Any())
                    {
                        OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Executing {safeTools.Count} analysis tools in parallel...");
                        var parallelTasks = safeTools.Select(async (ToolCallRequest toolCall) => 
                        {
                            if (ct.IsCancellationRequested) return;
                            
                            OnToolCallPending?.Invoke(toolCall);
                            var toolSw = System.Diagnostics.Stopwatch.StartNew();
                            var result = await _toolRegistry.ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                            toolSw.Stop();
                            
                            OnToolCallCompleted?.Invoke(toolCall, result);
                            
                            string output = result.Output ?? string.Empty;
                            if (output.Length > 3000) 
                            {
                                output = output.Substring(0, 1500) + "\n\n... [TRUNCATED] ...\n\n" + output.Substring(output.Length - 1500);
                            }

                            lock (messages)
                            {
                                messages.Add(new ChatMessage { Role = "tool", Content = $"[Tool '{toolCall.Name}' result]\n{output}" });
                                if (!result.IsError && result.Output != "No matches found." && !result.Output.Contains("not found"))
                                {
                                    stepHasMeaningfulResult = true;
                                }
                            }
                        });
                        await Task.WhenAll(parallelTasks);
                    }

                    // 2. Execute Risky Tools Sequentially (Safety first)
                    bool shouldFastComplete = false;
                    foreach (ToolCallRequest toolCall in riskyTools)
                    {
                        if (ct.IsCancellationRequested) break;

                        // 🛡️ USER CHECKPOINT: For mutating actions, wait for permission
                        bool isRisky = toolCall.Name == "write_file" || 
                                       toolCall.Name == "replace_text" || 
                                       toolCall.Name == "edit_lines" ||
                                       toolCall.Name == "delete_file" || 
                                       toolCall.Name == "rename_symbol" || 
                                       toolCall.Name == "run_terminal" ||
                                       toolCall.Name == "write_to_file" ||
                                       toolCall.Name == "replace_file_content";
                        bool requiresApproval = Settings.LocalPilotSettings.Instance?.RequireApprovalForWrites ?? true;
                        if (isRisky && requiresApproval && RequestPermissionAsync != null)
                        {
                            OnStatusUpdate?.Invoke(AgentStatus.ActionPending, $"Waiting for permission to {toolCall.Name}...");
                            bool granted = await RequestPermissionAsync(toolCall);
                            if (!granted)
                            {
                                messages.Add(new ChatMessage { Role = "tool", Content = "Error: User denied permission to execute this tool." });
                                continue;
                            }
                            if (toolCall.Name == "write_file" || toolCall.Name == "write_to_file")
                            {
                                if (toolCall.Arguments == null) toolCall.Arguments = new Dictionary<string, object>();
                                toolCall.Arguments["overwrite"] = true;
                            }
                        }

                        OnToolCallPending?.Invoke(toolCall);
                        
                        // Track changes for staging
                        bool isWrite = toolCall.Name == "write_file" || 
                                       toolCall.Name == "replace_text" || 
                                       toolCall.Name == "edit_lines" ||
                                       toolCall.Name == "write_to_file" || 
                                       toolCall.Name == "replace_file_content";
                        string target = null;
                        string code = null;

                        if (isWrite)
                        {
                            target = GetSafeToolArg(toolCall, "path") ?? GetSafeToolArg(toolCall, "TargetFile") ?? GetSafeToolArg(toolCall, "file");
                            
                            string rawCode = GetSafeToolArg(toolCall, "content") ?? 
                                             GetSafeToolArg(toolCall, "new_text") ?? 
                                             GetSafeToolArg(toolCall, "CodeContent") ??
                                             GetSafeToolArg(toolCall, "ReplacementContent"); // 🛡️ Support more tool variants

                            // 🚀 INTELLIGENT STAGING: For replacements, calculate the FULL NEW CONTENT for the diff
                            if (toolCall.Name == "replace_text" || toolCall.Name == "replace_file_content")
                            {
                                string oldText = GetSafeToolArg(toolCall, "old_text") ?? GetSafeToolArg(toolCall, "target_text");
                                string absPath = _toolRegistry.ResolvePath(target);
                                string currentContent = "";
                                
                                try {
                                    if (!turnFileCache.TryGetValue(absPath, out currentContent))
                                    {
                                        currentContent = File.Exists(absPath) ? File.ReadAllText(absPath) : "";
                                        turnFileCache[absPath] = currentContent;
                                    }
                                    
                                    // Capture original for the WHOLE TURN (first touch)
                                    lock (_stagedChanges) {
                                        if (!_stagedChanges.ContainsKey(absPath)) {
                                            _stagedChanges[absPath] = (currentContent, "");
                                        }
                                    }

                                    // Apply replacement to show the FULL FILE
                                    if (!string.IsNullOrEmpty(currentContent) && !string.IsNullOrEmpty(oldText))
                                        code = currentContent.Replace(oldText, rawCode ?? "");
                                    else
                                        code = rawCode;
                                    if (!string.IsNullOrEmpty(code)) turnFileCache[absPath] = code;
                                } catch { code = rawCode; }
                            }
                            else if (toolCall.Name == "edit_lines")
                            {
                                string absPath = _toolRegistry.ResolvePath(target);
                                string currentContent = "";
                                try
                                {
                                    if (!turnFileCache.TryGetValue(absPath, out currentContent))
                                    {
                                        currentContent = File.Exists(absPath) ? File.ReadAllText(absPath) : "";
                                        turnFileCache[absPath] = currentContent;
                                    }
                                    lock (_stagedChanges)
                                    {
                                        if (!_stagedChanges.ContainsKey(absPath))
                                            _stagedChanges[absPath] = (currentContent, "");
                                    }

                                    if (int.TryParse(GetSafeToolArg(toolCall, "start_line"), out int sLine) &&
                                        int.TryParse(GetSafeToolArg(toolCall, "end_line"), out int eLine) &&
                                        !string.IsNullOrEmpty(currentContent))
                                    {
                                        string sep = currentContent.Contains("\r\n") ? "\r\n" : "\n";
                                        var lines = currentContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                                        if (sLine >= 1 && sLine <= lines.Count + 1 && eLine >= sLine - 1 && eLine <= lines.Count)
                                        {
                                            int removeCount = eLine - sLine + 1;
                                            if (removeCount > 0) lines.RemoveRange(sLine - 1, removeCount);
                                            if (!string.IsNullOrEmpty(rawCode))
                                                lines.InsertRange(sLine - 1, rawCode.Replace("\r\n", "\n").Split('\n'));
                                            code = string.Join(sep, lines);
                                            turnFileCache[absPath] = code;
                                        }
                                        else code = rawCode;
                                    }
                                    else code = rawCode;
                                }
                                catch { code = rawCode; }
                            }
                            else
                            {
                                code = rawCode;
                            }
                            
                            // 🚀 STAGING: Track changes for UI review or post-process validation
                            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(code))
                            {
                                string absPath = _toolRegistry.ResolvePath(target);
                                lock (_stagedChanges) {
                                    bool hasExisting = _stagedChanges.ContainsKey(absPath);
                                    string originalCode = hasExisting ? _stagedChanges[absPath].original : null;
                                    
                                    if (!hasExisting) {
                                        // If we didn't capture original yet (e.g. write_file vs replace_text)
                                        try {
                                             if (File.Exists(absPath)) originalCode = File.ReadAllText(absPath);
                                             else originalCode = null; // New file did not exist prior to turn
                                        } catch { originalCode = null; }
                                    }
                                    _stagedChanges[absPath] = (originalCode, code);
                                }
                            }
                        }

                        // 🚀 DYNAMIC STATUS UPDATES: Let the user know exactly what the engine is doing
                        string status = $"Executing {toolCall.Name.Replace("_", " ")}...";
                        if (toolCall.Name == "rename_symbol") status = "Analyzing symbol with Roslyn (Tier 1)...";
                        else if (toolCall.Name == "grep_search") status = "Searching project files...";
                        else if (toolCall.Name == "list_errors") status = "Checking for build errors...";
                        
                        OnStatusUpdate?.Invoke(AgentStatus.Thinking, status);

                        // 🛡️ REFACTORING FALLBACK STATUS: If a tool takes more than 5s, we give a 'Deep Analysis' update
                        var toolTask = _toolRegistry.ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                        var delayTask = Task.Delay(5000); // 5s pulse
                        
                        var completedTask = await Task.WhenAny(toolTask, delayTask);
                        if (completedTask == delayTask && toolCall.Name == "rename_symbol")
                        {
                            OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Roslyn performing deep project-wide analysis...");
                        }

                        var toolSw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await toolTask;
                        toolSw.Stop();
                        
                        OnToolCallCompleted?.Invoke(toolCall, result);
                        LocalPilotLogger.Log($"[Agent] Tool '{toolCall.Name}' executed in {toolSw.ElapsedMilliseconds}ms. Success: {!result.IsError}");

                        string output = result.Output ?? string.Empty;
                        
                        // 🚀 CONTEXT BUDGETING: Prune massive tool outputs
                        if (output.Length > 3000) 
                        {
                            LocalPilotLogger.Log($"[Agent] Pruning tool output ({output.Length} chars -> 3000 chars)");
                            output = output.Substring(0, 1500) + "\n\n... [TRUNCATED FOR CONTEXT BUDGET] ...\n\n" + output.Substring(output.Length - 1500);
                        }
                        
                        lock (messages)
                        {
                            messages.Add(new ChatMessage
                            {
                                Role = "tool",
                                Content = $"[Tool '{toolCall.Name}' result]\n{output}"
                            });
                        }

                        if (!result.IsError)
                        {
                            // Status is already visually tracked via badges; no need for text chatter.
                        }

                        // 🛡️ ERROR FEEDBACK: Ensure model doesn't hallucinate success
                        if (result.IsError)
                        {
                            // Roll back speculative staging if tool execution failed
                            if (isWrite && !string.IsNullOrEmpty(target))
                            {
                                try
                                {
                                    string failedAbsPath = _toolRegistry.ResolvePath(target);
                                    lock (_stagedChanges) { _stagedChanges.Remove(failedAbsPath); }
                                    turnFileCache.Remove(failedAbsPath);
                                }
                                catch { }
                            }

                            lock (messages)
                            {
                                messages.Add(new ChatMessage 
                                { 
                                    Role = "system", 
                                    Content = $"ERROR: Tool '{toolCall.Name}' failed. Acknowledge and try an alternative approach." 
                                });
                            }
                        }
                        else 
                        {
                            if (!string.IsNullOrEmpty(result.Output) && result.Output != "No matches found." && !result.Output.Contains("not found"))
                            {
                                stepHasMeaningfulResult = true;
                            }

                            // 🚀 AUTO-VERIFICATION: If we edited code, immediately check for errors
                            // 🚀 SELF-HEAL ENGINE (Smart Fix Protocol): If we edited code, immediately check for new errors
                            if (isWrite)
                            {
                                var diagResult = await GetVisibleDiagnosticsAsync(ct);
                                bool hasErrors = !string.IsNullOrEmpty(diagResult);
                                
                                string verificationMsg = hasErrors
                                    ? $"[SMART FIX] Build errors detected:\n{diagResult}\n\nFix or revert now."
                                    : "[SMART FIX] Verified: No compilation errors.";
                                
                                lock (messages)
                                {
                                    messages.Add(new ChatMessage { Role = "system", Content = verificationMsg });
                                }
                                
                                if (hasErrors) 
                                {
                                    shouldFastComplete = false;
                                    LocalPilotLogger.Log("[Agent] Build errors detected after write. Triggering Smart Fix protocol feedback.", LogCategory.Build, LogSeverity.Warning);
                                }
                                else
                                {
                                    LocalPilotLogger.Log("[Agent] Write verified. No build errors detected.", LogCategory.Build, LogSeverity.Info);

                                    // 🚀 FAST-PATH COMPLETION: Mark ready to finish once all risky tools in this turn complete
                                    if (_stagedChanges.Count > 0 && (fastMode || isDirectFileAction || step >= 1))
                                    {
                                        shouldFastComplete = true;
                                    }
                                }
                            }
                        }
                    }

                    if (shouldFastComplete && _stagedChanges.Count > 0)
                    {
                        isDone = true;
                        OnTurnModificationsPending?.Invoke(new Dictionary<string, (string original, string improved)>(_stagedChanges));
                        _stagedChanges.Clear();
                        string doneMsg = "Done. Code updated and verified with 0 errors.";
                        OnMessageFragment?.Invoke(doneMsg);
                        OnMessageCompleted?.Invoke(doneMsg);
                        OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task completed successfully.");
                    }

                    if (!stepHasMeaningfulResult && !ct.IsCancellationRequested)
                    {
                        LocalPilotLogger.Log($"[Agent] Loop protection: Turn {step} idle. Nudging...");
                        messages.Add(new ChatMessage 
                        { 
                            Role = "system", 
                            Content = "Turn idle. If 'rename_symbol' failed, try 'replace_text' after reading the file."
                        });
                    }
                }
                else
                {
                    // No tool calls — check if the model actually provided a text response or only thought
                    string textWithoutThoughts = System.Text.RegularExpressions.Regex.Replace(cleanText ?? "", @"(?s)<thought>.*?</thought>", "").Trim();

                    bool isWriteIntent = !isReadOnlyAction && !isConversational && (
                        taskDescription.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("implement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("fix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("function", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("method", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        taskDescription.IndexOf("class", StringComparison.OrdinalIgnoreCase) >= 0
                    );

                    bool isModelPreambleWithoutAction = isWriteIntent && _stagedChanges.Count == 0 && (
                        string.IsNullOrWhiteSpace(textWithoutThoughts) ||
                        textWithoutThoughts.StartsWith("I need to", StringComparison.OrdinalIgnoreCase) ||
                        textWithoutThoughts.StartsWith("I will", StringComparison.OrdinalIgnoreCase) ||
                        textWithoutThoughts.StartsWith("I'll", StringComparison.OrdinalIgnoreCase) ||
                        textWithoutThoughts.StartsWith("Let me", StringComparison.OrdinalIgnoreCase) ||
                        textWithoutThoughts.Contains("I'll use replace_text") ||
                        textWithoutThoughts.Contains("I need to add") ||
                        textWithoutThoughts.Contains("I will add") ||
                        textWithoutThoughts.Contains("can see the")
                    );

                    // If the response is purely thinking or conversational preamble with no tool calls, nudge it to execute the tool!
                    if (isModelPreambleWithoutAction && step < 4)
                    {
                        LocalPilotLogger.Log($"[Agent] Model emitted preamble instead of executing write tool in Turn {step}. Nudging to execute tool...");
                        messages.Add(new ChatMessage 
                        { 
                            Role = "system", 
                            Content = $"Focus strictly on the user's request: \"{taskDescription}\". Do NOT modify unrelated methods, perceived typos, or existing code. Call 'replace_text' now to apply ONLY the requested change!" 
                        });
                        continue;
                    }

                    // Model gave an actual text response to the user
                    isDone = true;
                    OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task finished.");
                }
            }

            // Signal completion with pending file modifications for UI Review
            if (_stagedChanges.Count > 0)
            {
                OnTurnModificationsPending?.Invoke(new Dictionary<string, (string original, string improved)>(_stagedChanges));
            }

            if (!isDone && !ct.IsCancellationRequested && step >= maxSteps)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task stopped at maximum step limit.");
            }
            }
            catch (OperationCanceledException)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Idle, "Task cancelled by user.");
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Agent] RunTaskAsync failed", ex);
                OnStatusUpdate?.Invoke(AgentStatus.Failed, $"Task failed: {ex.Message}");
            }
            finally
            {
                IsActive = false;
                GlobalPriorityGuard.EndAgentTurn();
            }
        }

        /// <summary>
        /// Robust regex-based parser to catch tool calls embedded in markdown code blocks.
        /// Primarily used as a fallback for models that struggle with native tool-calling APIs.
        /// </summary>
        /// <summary>
        /// Removes JSON tool call blocks from the text to keep the UI clean 
        /// and avoid showing technical details to the user.
        /// </summary>
        private string StripToolCalls(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 🚀 PRECISION SILENCE ENGINE
            // Improved logic: Identify markdown blocks that contain a tool "name" and strip them entirely.
            // This is safer than trying to match balanced braces which is hard in regex.
            
            // 1. Remove markdown-wrapped tool calls (look for blocks containing "name" and "arguments")
            var mdPattern = @"(?s)```(?:json)?\s*\{.*?""name""\s*:.*?\}.*?```";
            var cleaned = System.Text.RegularExpressions.Regex.Replace(text, mdPattern, string.Empty);
            
            // 2. Remove raw JSON tool call structures (if not fenced)
            // Pattern looks for { "name": ... "arguments": ... } and stops at a reasonable end or newline
            var rawPattern = @"(?s)\{\s*""name""\s*:\s*""[^""]+""\s*,\s*""arguments""\s*:\s*\{.*?\}\s*\}";
            // We use a cautious approach for raw JSON to avoid over-stripping actual code samples if they're not tool calls.
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, rawPattern, string.Empty);
            
            // 3. Remove thinking process / thought tags if they are empty or short (noise reduction)
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?s)<thought>\s*</thought>", string.Empty);

            // 4. Remove common model artifacts (stray characters at end of output)
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*\(\$$", string.Empty);
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*\$$", string.Empty);

            return cleaned.Trim();
        }

        private List<ToolCallRequest> ParseJsonToolCalls(string text)
        {
            var results = new List<ToolCallRequest>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            try
            {
                // 🚀 ADVANCED PARSER (v4.1): Hand-crafted to survive LLM formatting chaos
                // 1. First, look for the standard "name/arguments" pattern
                var blocks = ExtractJsonBlocks(text);
                foreach (var block in blocks)
                {
                    try
                    {
                        var tc = JsonConvert.DeserializeObject<ToolCallRequest>(block);
                        if (tc != null && !string.IsNullOrEmpty(tc.Name))
                        {
                            results.Add(tc);
                        }
                        else
                        {
                            // Try parsing as a raw object if it might be an older format
                            var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(block);
                            if (dict != null && dict.TryGetValue("name", out var n) && n != null)
                            {
                                var request = new ToolCallRequest { Name = n.ToString() };
                                if (dict.TryGetValue("arguments", out var a) && a is Newtonsoft.Json.Linq.JObject jo)
                                    request.Arguments = jo.ToObject<Dictionary<string, object>>();
                                else
                                    request.Arguments = dict; // Treat top-level as args if no "arguments" key
                                
                                results.Add(request);
                            }
                        }
                    }
                    catch { /* Block was not valid JSON tool call */ }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[Agent] ParseJsonToolCalls critical failure: {ex.Message}");
            }

            return results;
        }

        private List<string> ExtractJsonBlocks(string text)
        {
            var blocks = new List<string>();
            int pos = 0;
            while ((pos = text.IndexOf('{', pos)) != -1)
            {
                int end = FindClosingBrace(text, pos);
                if (end != -1)
                {
                    blocks.Add(text.Substring(pos, end - pos + 1));
                    pos = end + 1;
                }
                else pos++;
            }
            return blocks;
        }

        private int FindClosingBrace(string text, int start)
        {
            int level = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
                if (!inString)
                {
                    if (c == '{') level++;
                    else if (c == '}')
                    {
                        level--;
                        if (level == 0) return i;
                    }
                }
            }
            return -1;
        }

        private string BuildToolSignature(ToolCallRequest toolCall)
        {
            string argsJson = JsonConvert.SerializeObject(toolCall?.Arguments ?? new Dictionary<string, object>());
            return $"{toolCall?.Name}|{argsJson}";
        }

        private string GetSafeToolArg(ToolCallRequest toolCall, string key)
        {
            if (toolCall?.Arguments == null) return null;
            if (toolCall.Arguments.TryGetValue(key, out var val) && val != null) return val.ToString();
            return null;
        }

        private OllamaOptions ApplyPerformancePresets(LocalPilot.Settings.PerformanceMode mode, List<ChatMessage> history, bool isQuickAction)
        {
            var options = new OllamaOptions();
            
            // 🚀 DYNAMIC CONTEXT SIZER: Accurately estimate tokens
            long estimatedTokens = 0;
            foreach (var msg in history) estimatedTokens += (long)((msg.Content?.Length ?? 0) / 3.2);
            
            // In Fast mode with minified schemas, overhead is ~200 tokens; standard mode ~500 tokens
            long toolSchemaOverhead = (mode == LocalPilot.Settings.PerformanceMode.Fast) ? 200 : 500;
            estimatedTokens += toolSchemaOverhead;
            
            // 🚀 USER-CONFIGURED CONTEXT CAP: Respect ContextWindowSize from Options
            int configuredCap = LocalPilot.Settings.LocalPilotSettings.Instance.ContextWindowSize;
            if (configuredCap <= 0) configuredCap = 4096;

            int maxCtx = configuredCap;
            if (isQuickAction)
            {
                maxCtx = Math.Min(2048, maxCtx);
            }

            // Tiered sizing bounded strictly by maxCtx
            long requiredCtx = (long)(estimatedTokens * 1.20);
            if (requiredCtx < 2048) requiredCtx = Math.Min(2048, maxCtx);
            else if (requiredCtx < 4096) requiredCtx = Math.Min(4096, maxCtx);
            else requiredCtx = maxCtx;
            
            options.NumCtx = (int)Math.Min(requiredCtx, (long)maxCtx);

            LocalPilotLogger.Log($"[Orchestrator] Dynamic Context Allocation: {options.NumCtx} tokens (QuickAction: {isQuickAction}, EstimatedInput: {estimatedTokens})");
            LocalPilotLogger.Log($"[Orchestrator] Prompt breakdown ({history.Count} parts): " + string.Join(", ", history.Select(m => $"{m.Role}[{m.Content?.Length ?? 0} chars]")), LogCategory.Agent);

            // 🚀 AGGRESSIVE DYNAMIC TIMEOUT: Scale based on prefill complexity
            // Heuristic: Base 120s + 10s per 1k tokens. 
            // This accommodates systems with slower GPU prefill or CPU offloading.
            int dynamicTimeout = (int)(120 + (estimatedTokens / 1000) * 10);
            
            switch (mode)
            {
                case LocalPilot.Settings.PerformanceMode.Fast:
                    options.Temperature = 0.5;
                    options.NumPredict = 1024;
                    options.RepeatPenalty = 1.1;
                    options.TopP = 0.9;
                    options.RequestTimeoutSeconds = Math.Max(180, Math.Min(1200, dynamicTimeout));
                    break;

                case LocalPilot.Settings.PerformanceMode.HighAccuracy:
                    options.Temperature = 0.0;
                    options.NumPredict = 8192;
                    options.RepeatPenalty = 1.25;
                    options.TopP = 0.1;
                    options.RequestTimeoutSeconds = Math.Max(dynamicTimeout * 2, 1800); // Up to 30 mins for deep logic
                    break;

                case LocalPilot.Settings.PerformanceMode.Standard:
                default:
                    options.Temperature = 0.1;
                    options.NumPredict = 2048;
                    options.RepeatPenalty = 1.15;
                    options.TopP = 0.7;
                    options.RequestTimeoutSeconds = Math.Max(180, Math.Min(1200, dynamicTimeout));
                    break;
            }

            if (isQuickAction) options.RequestTimeoutSeconds = Math.Max(options.RequestTimeoutSeconds, 180);

            if (LocalPilot.Settings.LocalPilotSettings.Instance.RequestTimeoutSeconds > 0)
            {
                options.RequestTimeoutSeconds = LocalPilot.Settings.LocalPilotSettings.Instance.RequestTimeoutSeconds;
            }

            LocalPilotLogger.Log($"[Orchestrator] Dynamic Timeout: {options.RequestTimeoutSeconds}s (Base: {dynamicTimeout}s, EstimatedTokens: {estimatedTokens})");

            return options;
        }

        // ── IMPLICIT CONTEXT PIPELINE ──────────────────────────────────────────

        private async Task<string> GetUnifiedContextAsync(string solutionPath, CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder();

            // 1. 🛡️ ENVIRONMENT SMART FIX: Host & Framework Details
            try {
                 await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                 var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                 if (dte != null) {
                     sb.AppendLine("## ENV");
                     sb.AppendLine($"- IDE: Visual Studio {dte.Version} ({dte.Edition})");
                     var activeProject = await Community.VisualStudio.Toolkit.VS.Solutions.GetActiveProjectAsync();
                     if (activeProject != null) {
                         sb.AppendLine($"- Active Project: {activeProject.Name}");
                         // Target Framework logic can be deep, but let's grab a simple property if possible
                     }
                     sb.AppendLine();
                 }
            } catch { }

            // 2. 🕒 TEMPORAL CONTEXT: Recently Touched Files
            try {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeDoc = await Community.VisualStudio.Toolkit.VS.Documents.GetActiveDocumentViewAsync();
                if (activeDoc != null) {
                    sb.AppendLine("## ACTIVE TAB");
                    sb.AppendLine($"- {Path.GetFileName(activeDoc.FilePath)} ({activeDoc.FilePath})");
                    sb.AppendLine();
                }
            } catch { }

            // 3. Diagnostics (Errors/Warnings)
            string diags = await GetVisibleDiagnosticsAsync(ct);
            if (!string.IsNullOrEmpty(diags))
            {
                sb.AppendLine("## ACTIVE DIAGNOSTICS (ERROR LIST)");
                sb.AppendLine(diags);
                sb.AppendLine();
            }

            // 4. Git State
            string gitState = await GetGitStateAsync(solutionPath);
            if (!string.IsNullOrEmpty(gitState))
            {
                sb.AppendLine("## GIT STATE");
                sb.AppendLine(gitState);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private async Task<string> GetVisibleDiagnosticsAsync(CancellationToken ct)
        {
            try
            {
                // Delegates to the appropriate provider (Roslyn vs Polyglot) via dispatcher
                return await SymbolIndexService.Instance.GetDiagnosticsAsync(ct);
            }
            catch { return null; }
        }

        private async Task<string> GetGitStateAsync(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(Path.Combine(root, ".git"))) return null;

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --short",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = System.Diagnostics.Process.Start(startInfo))
                {
                    // 10-second timeout to prevent hangs on slow/network drives
                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    if (await Task.WhenAny(outputTask, Task.Delay(10000)) != outputTask)
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }

                    string output = await outputTask;
                    if (!string.IsNullOrWhiteSpace(output))
                        return $"Modified files:\n{output.Trim()}";
                }
            }
            catch { }
            return null;
        }


        private string GenerateActionHistorySummary(List<ChatMessage> history)
        {
            var summaryParts = new List<string>();
            var filtered = history.Where(m => m.Role == "assistant" || m.Role == "tool").ToList();
            var lastTurns = filtered.Skip(Math.Max(0, filtered.Count - 10)).ToList();

            foreach (var msg in lastTurns)
            {
                if (msg.Role == "assistant")
                {
                    var clean = StripToolCalls(msg.Content);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        var preview = clean.Length > 100 ? clean.Substring(0, 100) + "..." : clean;
                        summaryParts.Add($"[Agent]: {preview}");
                    }
                }
                else if (msg.Role == "tool")
                {
                    string status = msg.Content.Contains("Error") ? "FAILED" : "SUCCESS";
                    summaryParts.Add($"[Action]: {msg.Content.Split('\n').FirstOrDefault() ?? "Unknown"} ({status})");
                }
            }

            if (!summaryParts.Any()) return "No prior actions in this session.";
            return "Summary of recent steps:\n" + string.Join("\n", summaryParts);
        }
        private List<ChatMessage> PruneContextToLimit(List<ChatMessage> original, int tokenLimit)
        {
            if (original == null || original.Count == 0) return original;

            // Reserve tokens for output generation (at least 512, or up to 1024)
            int maxPromptTokens = Math.Max(1024, tokenLimit - 512);

            long totalChars = 0;
            foreach (var m in original) totalChars += m.Content?.Length ?? 0;
            long estimatedTokens = (long)(totalChars / 3.2);
            if (estimatedTokens <= maxPromptTokens) return original;

            LocalPilotLogger.Log($"[Orchestrator] PRUNING: Input ({estimatedTokens} tokens) exceeds budget ({maxPromptTokens} of {tokenLimit} max). Truncating...", LogCategory.Agent, LogSeverity.Warning);

            var pruned = new List<ChatMessage>();
            foreach (var msg in original)
            {
                pruned.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
            }

            // Find the latest tool result (e.g. read_file in the current turn) - DO NOT truncate this!
            int latestToolIndex = -1;
            for (int i = pruned.Count - 1; i >= 0; i--)
            {
                if (pruned[i].Role == "tool")
                {
                    latestToolIndex = i;
                    break;
                }
            }

            // 1. Truncate OLDER tool outputs (older than the latest one)
            for (int i = 0; i < pruned.Count; i++)
            {
                if (i != latestToolIndex && pruned[i].Role == "tool" && (pruned[i].Content?.Length ?? 0) > 300)
                {
                    string c = pruned[i].Content;
                    pruned[i].Content = c.Substring(0, 200) + "\n... [TRUNCATED OLD TOOL RESULT] ...\n" + c.Substring(c.Length - 100);
                }
            }

            // Recalculate
            totalChars = 0;
            foreach (var m in pruned) totalChars += m.Content?.Length ?? 0;
            estimatedTokens = (long)(totalChars / 3.2);
            if (estimatedTokens <= maxPromptTokens) return pruned;

            // 2. Drop older conversation turns from previous interactions before touching active file content
            int middleStart = 1;
            while (estimatedTokens > maxPromptTokens && middleStart < pruned.Count - 1)
            {
                if (pruned.Count <= 3) break;
                if (middleStart == latestToolIndex)
                {
                    middleStart++;
                    continue;
                }
                var removed = pruned[middleStart];
                // Never remove active or target file content
                if (removed.Content != null && (removed.Content.StartsWith("## ACTIVE FILE CONTENT") || removed.Content.StartsWith("## TARGET FILE CONTENT")))
                {
                    middleStart++;
                    continue;
                }
                pruned.RemoveAt(middleStart);
                if (latestToolIndex > middleStart) latestToolIndex--;
                estimatedTokens -= (long)((removed.Content?.Length ?? 0) / 3.2);
            }

            if (estimatedTokens <= maxPromptTokens) return pruned;

            // 3. Truncate other non-critical system snippets (NEVER truncate active/target file content!)
            bool hasToolResult = latestToolIndex >= 0;
            for (int i = 1; i < pruned.Count - 1; i++)
            {
                if (pruned[i].Role == "system")
                {
                    string content = pruned[i].Content ?? "";
                    if (content.StartsWith("## ACTIVE FILE CONTENT") || 
                        content.StartsWith("## TARGET FILE CONTENT") || 
                        content.Contains("PROJECT-SPECIFIC RULES"))
                    {
                        // 🛡️ CRITICAL: NEVER truncate active/target file content!
                        continue;
                    }
                    else if (hasToolResult && (content.StartsWith("## ACTIVE EDITOR SNIPPET") || content.StartsWith("## GROUNDING CONTEXT")))
                    {
                        pruned[i].Content = "// [Redundant context pruned: actual file content is in tool result]";
                    }
                    else if (content.Length > 400)
                    {
                        pruned[i].Content = content.Substring(0, 250) + "\n... [truncated for context budget]";
                    }
                }
            }

            // Recalculate
            totalChars = 0;
            foreach (var m in pruned) totalChars += m.Content?.Length ?? 0;
            estimatedTokens = (long)(totalChars / 3.2);
            if (estimatedTokens <= maxPromptTokens) return pruned;

            // 4. As a last resort, if latest tool result exceeds 4000 chars, cap it
            if (estimatedTokens > maxPromptTokens && latestToolIndex >= 0 && latestToolIndex < pruned.Count)
            {
                var toolMsg = pruned[latestToolIndex];
                if ((toolMsg.Content?.Length ?? 0) > 4000)
                {
                    toolMsg.Content = toolMsg.Content.Substring(0, 2500) + 
                        $"\n\n... [File truncated to 3,500 chars. Total size: {toolMsg.Content.Length} chars] ...\n\n" + 
                        toolMsg.Content.Substring(toolMsg.Content.Length - 1000);
                }
            }

            return pruned;
        }

        private static string AddLineNumbers(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            var sb = new System.Text.StringBuilder(code.Length + lines.Length * 5);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(i + 1).Append(": ").Append(lines[i]);
            }
            return sb.ToString();
        }
    }
}
