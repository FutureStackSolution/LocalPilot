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
        private readonly OllamaService _ollama;
        private readonly ToolRegistry _toolRegistry;
        private readonly ProjectContextService _projectContext;
        private readonly ProjectMapService _projectMap;

        public event Action<AgentStatus, string> OnStatusUpdate;
        public event Action<ToolCallRequest> OnToolCallPending;
        public event Action<ToolCallRequest, ToolResponse> OnToolCallCompleted;
        public event Action<string> OnMessageFragment;
        public event Action<string> OnMessageCompleted;
        public event Action<Dictionary<string, (string original, string improved)>> OnTurnModificationsPending;
        
        public Func<ToolCallRequest, Task<bool>> RequestPermissionAsync;

        private Dictionary<string, (string original, string improved)> _stagedChanges = new Dictionary<string, (string original, string improved)>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Indicates if an agentic task is currently running. 
        /// Used by background services to yield CPU/GPU resources.
        /// </summary>
        public bool IsActive { get; private set; }

        public AgentOrchestrator(OllamaService ollama, ToolRegistry toolRegistry, ProjectContextService projectContext, ProjectMapService projectMap)

        {
            _ollama = ollama;
            _toolRegistry = toolRegistry;
            _projectContext = projectContext;
            _projectMap = projectMap;
        }

        /// <summary>
        /// Initiates an autonomous task for the agent.
        /// Uses Ollama's native tool calling API for structured, reliable tool invocation.
        /// </summary>
        public async Task RunTaskAsync(string taskDescription, List<ChatMessage> messages, CancellationToken ct, string modelOverride = null)
        {
            if (_ollama.CircuitBreakerTripped)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Failed, "Ollama is unreachable (Circuit Breaker tripped). Please check your connection.");
                return;
            }

            IsActive = true;
            GlobalPriorityGuard.StartAgentTurn();
            try
            {
                string solutionPath = "unknown";
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var sol = await VS.Solutions.GetCurrentSolutionAsync();
                    if (sol != null && !string.IsNullOrEmpty(sol.FullPath))
                    {
                        solutionPath = Path.GetDirectoryName(sol.FullPath);
                    }
                }
                catch { }

                _toolRegistry.WorkspaceRoot = solutionPath;
                
                // 🚀 MODEL SELECTION: Use specific model for tasks (Explain, Refactor, etc) if provided
                string contextModel = modelOverride ?? LocalPilot.Settings.LocalPilotSettings.Instance.ChatModel;

                // 🚀 CONTEXT AUTO-COMPACTION: Prune if history is getting too deep
                var compactor = new HistoryCompactor(_ollama);
                var originalCount = messages.Count;
                var compactedMessages = await compactor.CompactIfNeededAsync(messages, contextModel);
                
                // If compaction happened, we swap the list content (preserving the reference if possible, 
                // but since we return it to the UI, we'll just update the local variable here).
                if (compactedMessages.Count < messages.Count)
                {
                    messages.Clear();
                    messages.AddRange(compactedMessages);
                    LocalPilotLogger.Log($"[Orchestrator] Context Compacted: {originalCount} -> {messages.Count} messages.");
                }

                // 🚀 SISTEM PROMPT DEDUPLICATION: Ensure we don't stack duplicate identity prompts
                // Clear any existing prompts that look like the LocalPilot Identity to prevent bloat
                messages.RemoveAll(m => m.Role == "system" && 
                                        (m.Content.Contains("<identity>") || 
                                         m.Content.Contains("LocalPilot, an elite") ||
                                         m.Content.Contains("{solutionPath}")));

                // 🚀 SMART SYSTEM PROMPT: Tailor the foundation based on the task intent
                var systemPrompt = PromptLoader.GetPrompt("SystemPrompt", new Dictionary<string, string> { { "solutionPath", solutionPath } });

                bool isReadOnlyAction = taskDescription.Contains("<task_type>code_explanation") || 
                                        taskDescription.Contains("<task_type>documentation_generation") ||
                                        taskDescription.Contains("<task_type>code_review") ||
                                        taskDescription.Contains("/explain") ||
                                        taskDescription.Contains("/review") ||
                                        taskDescription.Contains("/doc");

                if (isReadOnlyAction && !string.IsNullOrEmpty(systemPrompt))
                {
                    // Strip the 'Worker' protocol for informational tasks to save context and prevent hallucinated tool calls
                    systemPrompt = System.Text.RegularExpressions.Regex.Replace(systemPrompt, @"(?s)<smart_fix_protocol>.*?</smart_fix_protocol>", string.Empty);
                    systemPrompt = System.Text.RegularExpressions.Regex.Replace(systemPrompt, @"(?s)<tool_usage>.*?</tool_usage>", string.Empty);
                }

                messages.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt ?? "You are LocalPilot." });

                // Inject project-specific rules if they haven't been added yet
                if (!messages.Any(m => m.Content.Contains("PROJECT-SPECIFIC RULES")))
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

                // ═══════════════════════════════════════════════════════════════════
                // NATIVE TOOL DEFINITIONS
                // ═══════════════════════════════════════════════════════════════════
                // 🚀 PERFORMANCE SHIELD: Disable tools for informational Quick Actions to ensure near-instant responses
                // and prevent models from hallucinating tool calls for simple questions.
                var toolDefinitions = isReadOnlyAction ? new List<OllamaToolDefinition>() : _toolRegistry.GetOllamaToolDefinitions();
                LocalPilotLogger.Log($"[Agent] Registered {toolDefinitions.Count} native tools for Ollama (ReadOnly: {isReadOnlyAction})", LogCategory.Agent);

                // Determine if this is a specialized Quick Action (Explain, Document, etc)
                bool isQuickAction = !string.IsNullOrEmpty(modelOverride);

                // 🚀 UNIFIED CONTEXT PIPELINE: Proactively gather implicit context (Errors, Git, Symbols)
                if (!isQuickAction && !messages.Any(m => m.Content.Contains("ACTIVE DIAGNOSTICS")))
                {
                    string unifiedContext = await GetUnifiedContextAsync(solutionPath, ct);
                    if (!string.IsNullOrEmpty(unifiedContext))
                    {
                        messages.Add(new ChatMessage { Role = "system", Content = unifiedContext });
                    }
                }

                if (LocalPilot.Settings.LocalPilotSettings.Instance.EnableProjectMap)
                {
                    // 🚀 SMART CONTEXT BUDGETING: Use a compact map for Quick Actions to avoid context bloat
                    int mapLimit = isQuickAction ? 20 * 1024 : 600 * 1024;

                    if (!messages.Any(m => m.Content.Contains("PROJECT STRUCTURE")))
                    {
                        OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Analyzing project structure...");
                        string projectMapContent = await _projectMap.GenerateProjectMapAsync(solutionPath, maxTotalBytes: mapLimit);
                        if (!string.IsNullOrEmpty(projectMapContent))
                        {
                            messages.Add(new ChatMessage { Role = "system", Content = $"## PROJECT STRUCTURE\n{projectMapContent}" });
                        }
                    }
                }
                
                // Fetch initial grounding context from the project (Skip for simple Quick Actions)
                if (!isQuickAction)
                {
                    string groundingContext = await _projectContext.SearchContextAsync(_ollama, taskDescription, topN: 5);
                    if (!string.IsNullOrEmpty(groundingContext))
                    {
                        messages.Add(new ChatMessage { Role = "system", Content = $"## GROUNDING CONTEXT (VECTORS)\n{groundingContext}" });
                    }
                }

                // Inject Active Document context for better grounding
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                    if (activeDoc?.FilePath != null)
                    {
                        // 🚀 AUTO-RAG: Inject semantic neighborhood awareness
                        string neighborhood = await SymbolIndexService.Instance.GetNeighborhoodContextAsync(activeDoc.FilePath, ct);
                        if (!string.IsNullOrEmpty(neighborhood))
                        {
                            messages.Add(new ChatMessage { Role = "system", Content = neighborhood });
                        }

                        string content = activeDoc.TextBuffer.CurrentSnapshot.GetText();
                        if (content.Length > 3000) content = content.Substring(0, 3000) + "... [truncated]";
                        
                        messages.Add(new ChatMessage { 
                            Role = "system", 
                            Content = $"## ACTIVE EDITOR SNIPPET\nPath: {activeDoc.FilePath}\nCode:\n```\n{content}\n```" 
                        });
                    }
                }
                catch { }

                // 🚀 NEXUS INTELLIGENCE: Inject cross-language dependency awareness
                try
                {
                    var nexus = NexusService.Instance.GetGraph();
                    if (nexus.Nodes.Any())
                    {
                        var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                        if (activeDoc?.FilePath != null)
                        {
                            var node = nexus.Nodes.FirstOrDefault(n => n.FilePath != null && n.FilePath.Equals(activeDoc.FilePath, StringComparison.OrdinalIgnoreCase));
                            if (node != null)
                            {
                                var deps = nexus.Edges.Where(e => e.FromId == node.Id).ToList();
                                if (deps.Any())
                                {
                                    var sb = new System.Text.StringBuilder();
                                    sb.AppendLine("## NEXUS CONTEXT (Stack Dependencies)");
                                    sb.AppendLine($"Active File '{node.Name}' connects to {deps.Count} resources.");
                                    
                                    // Budgeting: Only show top 5 dependencies to avoid context bloat
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
                }
                catch { }

                // Context Injection: Fetch active selection for slash commands
                string activeSelection = "";
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                    if (activeDoc?.TextView != null)
                    {
                        activeSelection = activeDoc.TextView.Selection.SelectedSpans.Count > 0 
                                          ? activeDoc.TextView.Selection.SelectedSpans[0].GetText() 
                                          : "";
                        
                        if (string.IsNullOrWhiteSpace(activeSelection))
                        {
                            activeSelection = activeDoc.TextBuffer.CurrentSnapshot.GetText();
                        }
                    }
                }
                catch { }

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

            // Finally, the user request with a task header
            messages.Add(new ChatMessage { Role = "user", Content = $"## TASK\n{processedTask}" });


            // 🔍 FILE NESTING: Use Roslyn/LSP to find definitions for potential symbols in the task
            // This provides the model with structural awareness of the code it's discussing.
            var words = processedTask.Split(new[] { ' ', '.', '(', ')', '[', ']', '<', '>', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            var PascalNames = words.Where(w => w.Length >= 3 && char.IsUpper(w[0])).Distinct();

            foreach (var name in PascalNames)
            {
                var locs = await SymbolIndexService.Instance.FindDefinitionsAsync(name, ct);
                if (locs != null && locs.Any())
                {
                    var loc = locs.First();
                    messages.Add(new ChatMessage { Role = "system", Content = $"[LSP CONTEXT] Symbol '{name}' ({loc.Kind}) is defined at: {loc.FilePath}:{loc.Line}" });
                }
            }

            // Inject known symbols summary for fuzzy matching
            string symbolSummary = SymbolIndexService.Instance.GetSummary();
            if (!string.IsNullOrEmpty(symbolSummary))
            {
                messages.Add(new ChatMessage { Role = "system", Content = $"## WORKSPACE SYMBOLS\n{symbolSummary}" });
            }

            // ═══════════════════════════════════════════════════════════════════
            // AGENT LOOP: Native tool calling — no parsing, no nudging
            // ═══════════════════════════════════════════════════════════════════
            bool isDone = false;
            int maxSteps = 20;
            int step = 0;
            _stagedChanges.Clear();
            string lastToolCallSignature = null;
            int repeatCount = 0;

            while (!isDone && step < maxSteps)
            {
                step++;
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Analyzing context and planning next step...");

                // 1. Get LLM response with native tool support
                var responseBuilder = new System.Text.StringBuilder();
                var toolCallsThisTurn = new List<ToolCallRequest>();

                // 🚀 STREAM INTERCEPTOR: Buffer and suppress tool-text leaking to UI
                var options = ApplyPerformancePresets(LocalPilot.Settings.LocalPilotSettings.Instance.Mode, messages, isQuickAction);
                
                // 🚀 OODA ORIENTATION: Inject Turn Summary to prevent amnesia
                if (step > 1)
                {
                    string historySummary = GenerateActionHistorySummary(messages);
                    messages.Add(new ChatMessage { Role = "system", Content = $"## OODA ORIENTATION (Turn {step})\n{historySummary}\n\nSTAY FOCUSED: Proceed to the next step of your plan." });
                }

                LocalPilotLogger.Log($"[Agent] Turn {step}: Running model {contextModel} with context={options.NumCtx}", LogCategory.Agent);

                var turnSw = Stopwatch.StartNew();
                int estimatedTokens = 0;

                // Stream with native tool calling
                await foreach (var result in _ollama.StreamChatWithToolsAsync(contextModel, messages, toolDefinitions, options, ct))
                {
                    if (result.IsTextToken)
                    {
                        string token = result.TextToken;
                        responseBuilder.Append(token);
                        estimatedTokens++; 

                        // 🚀 NARRATIVE STREAMING: Pass tokens to UI for a 'live' feel.
                        OnMessageFragment?.Invoke(token);
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
                
                // Record metrics
                PerformanceTracer.Instance.RecordTurn(taskDescription, step, turnSw.ElapsedMilliseconds, estimatedTokens, contextModel);
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

                    // Execute tools in parallel (or sequence for risky ones)
                    foreach (var toolCall in validToolCalls)
                    {
                        if (ct.IsCancellationRequested) break;

                        // 🛡️ USER CHECKPOINT: For mutating actions, wait for permission
                        bool isRisky = toolCall.Name == "write_file" || 
                                       toolCall.Name == "replace_text" || 
                                       toolCall.Name == "delete_file" || 
                                       toolCall.Name == "rename_symbol" || 
                                       toolCall.Name == "run_terminal";
                        if (isRisky && RequestPermissionAsync != null)
                        {
                            OnStatusUpdate?.Invoke(AgentStatus.ActionPending, $"Waiting for permission to {toolCall.Name}...");
                            bool granted = await RequestPermissionAsync(toolCall);
                            if (!granted)
                            {
                                messages.Add(new ChatMessage { Role = "tool", Content = "Error: User denied permission to execute this tool." });
                                continue;
                            }
                        }

                        OnToolCallPending?.Invoke(toolCall);
                        
                        // Track changes for staging
                        bool isWrite = toolCall.Name == "write_file" || 
                                       toolCall.Name == "replace_text" || 
                                       toolCall.Name == "write_to_file" || 
                                       toolCall.Name == "replace_file_content";
                        string target = null;
                        string code = null;

                        if (isWrite)
                        {
                            target = GetSafeToolArg(toolCall, "path") ?? GetSafeToolArg(toolCall, "TargetFile");
                            
                            string rawCode = GetSafeToolArg(toolCall, "content") ?? 
                                             GetSafeToolArg(toolCall, "new_text") ?? 
                                             GetSafeToolArg(toolCall, "CodeContent") ??
                                             GetSafeToolArg(toolCall, "ReplacementContent"); // 🛡️ Support more tool variants

                            // 🚀 INTELLIGENT STAGING: For replacements, calculate the FULL NEW CONTENT for the diff
                            if (toolCall.Name == "replace_text")
                            {
                                string oldText = GetSafeToolArg(toolCall, "old_text");
                                string absPath = _toolRegistry.ResolvePath(target);
                                string currentContent = "";
                                
                                try {
                                    if (File.Exists(absPath)) currentContent = File.ReadAllText(absPath);
                                    
                                    // Capture original for the WHOLE TURN (first touch)
                                    lock (_stagedChanges) {
                                        if (!_stagedChanges.ContainsKey(target)) {
                                            _stagedChanges[target] = (currentContent, "");
                                        }
                                    }

                                    // Apply replacement to show the FULL FILE
                                    if (!string.IsNullOrEmpty(currentContent) && !string.IsNullOrEmpty(oldText))
                                        code = currentContent.Replace(oldText, rawCode ?? "");
                                    else
                                        code = rawCode;
                                } catch { code = rawCode; }
                            }
                            else
                            {
                                code = rawCode;
                            }
                            
                            // 🚀 STAGING: Track changes for UI review or post-process validation
                            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(code))
                            {
                                lock (_stagedChanges) {
                                    bool hasExisting = _stagedChanges.ContainsKey(target);
                                    string originalCode = hasExisting ? _stagedChanges[target].original : null;
                                    
                                    if (!hasExisting) {
                                        // If we didn't capture original yet (e.g. write_file vs replace_text)
                                        try {
                                             string absPath = _toolRegistry.ResolvePath(target);
                                             if (File.Exists(absPath)) originalCode = File.ReadAllText(absPath);
                                             else originalCode = ""; // New file original is empty
                                        } catch { originalCode = ""; }
                                    }
                                    _stagedChanges[target] = (originalCode ?? "", code);
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
                            if (result.Output != "No matches found." && !result.Output.Contains("not found"))
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
                                    LocalPilotLogger.Log("[Agent] Build errors detected after write. Triggering Smart Fix protocol feedback.", LogCategory.Build, LogSeverity.Warning);
                                }
                                else
                                {
                                    LocalPilotLogger.Log("[Agent] Write verified. No build errors detected.", LogCategory.Build, LogSeverity.Info);
                                }
                            }
                        }
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
                    // No tool calls — the model is done (or gave a text-only response)
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
            try
            {
                // Match blocks like: ```json { "name": "...", "arguments": { ... } } ```
                // Or just a raw JSON object { "name": "...", ... }
                var pattern = @"(?:```(?:json)?\s*)?\{\s*""name""\s*:\s*""(?<name>[^""]+)""\s*,\s*""arguments""\s*:\s*(?<args>\{.*?\})\s*\}(?:\s*```)?";
                var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    try
                    {
                        string name = m.Groups["name"].Value;
                        string argsJson = m.Groups["args"].Value;
                        var args = JsonConvert.DeserializeObject<Dictionary<string, object>>(argsJson);
                        
                        results.Add(new ToolCallRequest { Name = name, Arguments = args });
                    }
                    catch { /* Skip malformed JSON */ }
                }

                // Fallback: If no structured "name/arguments" pairs, look for any JSON object that might be a tool call
                if (!results.Any())
                {
                    var rawPattern = @"(?:```(?:json)?\s*)?(?<json>\{\s*""name""\s*:.+?\})(?:\s*```)?";
                    var rawMatches = System.Text.RegularExpressions.Regex.Matches(text, rawPattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                    foreach (System.Text.RegularExpressions.Match m in rawMatches)
                    {
                        try
                        {
                            var tc = JsonConvert.DeserializeObject<ToolCallRequest>(m.Groups["json"].Value);
                            if (tc != null && !string.IsNullOrEmpty(tc.Name)) results.Add(tc);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return results;
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
            
            // 🚀 DYNAMIC CONTEXT SIZER: Automatically calculate required memory
            // Use char/2 (not char/3): source code is denser than prose — keywords,
            // indentation, punctuation all inflate the token count per character.
            long estimatedTokens = 0;
            foreach (var msg in history) estimatedTokens += (msg.Content?.Length ?? 0) / 2;
            
            // Account for the tools-schema JSON that Ollama injects into the prompt
            // but is NOT part of the messages array. 12 tools × ~400 chars ≈ ~2400 chars ≈ 1200 tokens.
            const long ToolSchemaOverhead = 1500;
            estimatedTokens += ToolSchemaOverhead;
            
            // Add a 25% safety buffer for the next generation output
            long requiredCtx = (long)(estimatedTokens * 1.25);
            
            // Tiered minimums to avoid frequent context switching.
            if (requiredCtx < 8192) requiredCtx = 8192;
            else if (requiredCtx < 16384) requiredCtx = 16384;
            else if (requiredCtx < 32768) requiredCtx = 32768;
            
            // 🚀 STRICT CAP: Ensure context never exceeds hardware/Ollama stability limits.
            int maxCtx = isQuickAction ? 8192 : 32768;
            options.NumCtx = (int)Math.Min(requiredCtx, (long)maxCtx);

            LocalPilotLogger.Log($"[Orchestrator] Dynamic Context Allocation: {options.NumCtx} tokens (QuickAction: {isQuickAction}, EstimatedInput: {estimatedTokens})");

            switch (mode)
            {
                case LocalPilot.Settings.PerformanceMode.Fast:
                    options.Temperature = 0.5;
                    options.NumPredict = 1024;
                    options.RepeatPenalty = 1.1;
                    options.TopP = 0.9;
                    break;

                case LocalPilot.Settings.PerformanceMode.HighAccuracy:
                    options.Temperature = 0.0;
                    options.NumPredict = 8192;
                    options.RepeatPenalty = 1.25;
                    options.TopP = 0.1;
                    break;

                case LocalPilot.Settings.PerformanceMode.Standard:
                default:
                    options.Temperature = 0.1;
                    options.NumPredict = 2048;
                    options.RepeatPenalty = 1.15;
                    options.TopP = 0.7;
                    break;
            }

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
                    string output = await proc.StandardOutput.ReadToEndAsync();
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
    }
}
