using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Community.VisualStudio.Toolkit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        public event Action<Dictionary<string, string>> OnTurnModificationsPending;
        
        public Func<ToolCallRequest, Task<bool>> RequestPermissionAsync;

        private Dictionary<string, string> _stagedChanges = new Dictionary<string, string>();

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
        public async Task RunTaskAsync(string taskDescription, CancellationToken ct)
        {
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
                else
                {
                    var prj = await VS.Solutions.GetActiveProjectAsync();
                    if (prj != null && !string.IsNullOrEmpty(prj.FullPath))
                    {
                        solutionPath = Path.GetDirectoryName(prj.FullPath);
                    }
                }
            }
            catch { }

            var messages = new List<ChatMessage>();
            _toolRegistry.WorkspaceRoot = solutionPath;

            // 🚀 UNIFIED CONTEXT PIPELINE: Proactively gather implicit context (Errors, Git, Symbols)
            string unifiedContext = await GetUnifiedContextAsync(solutionPath, ct);
            if (!string.IsNullOrEmpty(unifiedContext))
            {
                messages.Add(new ChatMessage { Role = "system", Content = unifiedContext });
            }

            // ═══════════════════════════════════════════════════════════════════
            // NATIVE TOOL DEFINITIONS
            // ═══════════════════════════════════════════════════════════════════
            var toolDefinitions = _toolRegistry.GetOllamaToolDefinitions();
            LocalPilotLogger.Log($"[Agent] Registered {toolDefinitions.Count} native tools for Ollama");

            // 🚀 SECTION-HEADER FORMAT: Structured system prompt loaded from template
            var systemPrompt = PromptLoader.GetPrompt("SystemPrompt", new Dictionary<string, string> 
            {
                { "solutionPath", solutionPath }
            });

            if (string.IsNullOrEmpty(systemPrompt))
            {
                // Fallback if file load fails
                systemPrompt = $"## IDENTITY\nYou are LocalPilot.\n## WORKSPACE\nPath: {solutionPath}";
            }

            messages.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });

            if (LocalPilot.Settings.LocalPilotSettings.Instance.EnableProjectMap)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Analyzing project structure...");
                string projectMapContent = await _projectMap.GenerateProjectMapAsync(solutionPath, 
                    maxTotalBytes: LocalPilot.Settings.LocalPilotSettings.Instance.MaxMapSizeKB * 1024);
                
                if (!string.IsNullOrEmpty(projectMapContent))
                {
                    messages.Add(new ChatMessage { Role = "system", Content = $"## PROJECT STRUCTURE\n{projectMapContent}" });
                }
            }
            
            // Context Injection: Fetch active selection for slash commands
            string activeSelection = "";
            string activeFilePath = "";
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                if (activeDoc?.TextView != null)
                {
                    activeFilePath = activeDoc.FilePath;
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
                    await _projectMap.GenerateProjectMapAsync(solutionPath, maxTotalBytes: settings.MaxMapSizeKB * 1024);
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

            // 🔍 FILE NESTING: Use Roslyn to find definitions for potential symbols in the task
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

            // Fetch initial grounding context from the project
            string groundingContext = await _projectContext.SearchContextAsync(_ollama, taskDescription, topN: 5);
            if (!string.IsNullOrEmpty(groundingContext))
            {
                messages.Add(new ChatMessage { Role = "system", Content = $"## GROUNDING CONTEXT (VECTORS)\n{groundingContext}" });
            }

            // Inject known symbols for fuzzy matching
            string symbolSummary = SymbolIndexService.Instance.GetSummary();
            if (!string.IsNullOrEmpty(symbolSummary))
            {
                messages.Add(new ChatMessage { Role = "system", Content = $"## WORKSPACE SYMBOLS\n{symbolSummary}" });
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
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, string.Empty);

                // 1. Get LLM response with native tool support
                var responseBuilder = new System.Text.StringBuilder();
                var toolCallsThisTurn = new List<ToolCallRequest>();

                // 🚀 STREAM INTERCEPTOR: Buffer and suppress tool-text leaking to UI
                var tokenBuffer = new System.Text.StringBuilder();
                bool isBuffering = false;

                string contextModel = LocalPilot.Settings.LocalPilotSettings.Instance.ChatModel;
                var options = ApplyPerformancePresets(LocalPilot.Settings.LocalPilotSettings.Instance.Mode);

                // Stream with native tool calling
                await foreach (var result in _ollama.StreamChatWithToolsAsync(contextModel, messages, toolDefinitions, options, ct))
                {
                    if (result.IsTextToken)
                    {
                        string token = result.TextToken;
                        responseBuilder.Append(token);
                        tokenBuffer.Append(token);

                        // 🚀 NARRATIVE STREAMING: Pass tokens to UI for a 'live' feel.
                        if (!isBuffering)
                        {
                            OnMessageFragment?.Invoke(token);
                        }

                        // Code block detection for adaptive buffering
                        if (!isBuffering && tokenBuffer.ToString().Contains("```"))
                        {
                            isBuffering = true;
                        }

                        if (isBuffering && tokenBuffer.ToString().TrimEnd().EndsWith("```") && tokenBuffer.Length > 5)
                        {
                            tokenBuffer.Clear();
                            isBuffering = false;
                        }
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

                var responseText = responseBuilder.ToString();
                var cleanText = StripToolCalls(responseText);

                // Show completed message text (if any)
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    OnMessageCompleted?.Invoke(cleanText);
                }

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

                        // 🛡️ USER CHECKPOINT: For risky actions, wait for permission
                        bool isRisky = toolCall.Name == "write_to_file" || toolCall.Name == "replace_file_content" || toolCall.Name == "delete_file";
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
                        bool isWrite = toolCall.Name == "write_to_file" || toolCall.Name == "replace_file_content";
                        string target = null;
                        string code = null;

                        if (isWrite)
                        {
                            target = toolCall.Arguments?.ContainsKey("TargetFile") == true
                                ? toolCall.Arguments["TargetFile"]?.ToString()
                                : toolCall.Arguments?.ContainsKey("path") == true
                                    ? toolCall.Arguments["path"]?.ToString()
                                    : null;
                            code = toolCall.Arguments?.ContainsKey("CodeContent") == true
                                ? toolCall.Arguments["CodeContent"]?.ToString()
                                : toolCall.Arguments?.ContainsKey("content") == true
                                    ? toolCall.Arguments["content"]?.ToString()
                                    : null;
                            
                            // 🚀 DIFF PREVIEW: Show native VS comparison before writing
                            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(code))
                            {
                                lock (_stagedChanges) _stagedChanges[target] = code;
                                _ = ShowDiffAsync(_toolRegistry.ResolvePath(target), code);
                            }
                        }

                        // 🚀 DYNAMIC STATUS UPDATES: Let the user know exactly what the engine is doing
                        string status = $"Executing {toolCall.Name}...";
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

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await toolTask;
                        sw.Stop();
                        
                        OnToolCallCompleted?.Invoke(toolCall, result);
                        LocalPilotLogger.Log($"[Agent] Tool '{toolCall.Name}' executed in {sw.ElapsedMilliseconds}ms. Success: {!result.IsError}");

                        string output = result.Output ?? string.Empty;
                        if (output.Length > 8000) output = output.Substring(0, 8000) + "... [Truncated]";
                        
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
                            OnMessageFragment?.Invoke($"✅ Successfully executed: **{toolCall.Name}**\n");
                        }

                        // 🛡️ ERROR FEEDBACK: Ensure model doesn't hallucinate success
                        if (result.IsError)
                        {
                            lock (messages)
                            {
                                messages.Add(new ChatMessage 
                                { 
                                    Role = "system", 
                                    Content = $"CRITICAL: Tool '{toolCall.Name}' failed. You MUST NOT claim success. " +
                                              "You must acknowledge this failure in your next Plan and try an alternative approach (e.g., if rename failed, use replace_text)." 
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
                            if (isWrite)
                            {
                                var diagResult = await GetVisibleDiagnosticsAsync(ct);
                                string verificationMsg = !string.IsNullOrEmpty(diagResult)
                                    ? $"[AUTO-VERIFY] Code changes detected new or existing errors:\n{diagResult}\n\nPlease fix these errors before proceeding."
                                    : "[AUTO-VERIFY] No compilation errors detected in the Error List.";
                                
                                lock (messages)
                                {
                                    messages.Add(new ChatMessage { Role = "system", Content = verificationMsg });
                                }
                            }
                        }
                    }

                    if (!stepHasMeaningfulResult && !ct.IsCancellationRequested)
                    {
                        LocalPilotLogger.Log($"[Agent] Loop protection: Turn {step} produced no meaningful actions or results. Nudging model...");
                        messages.Add(new ChatMessage 
                        { 
                            Role = "system", 
                            Content = "Your last turn did not perform any successful file edits or find relevant information. " +
                                      "If you were trying to rename or edit, verify the file path and line numbers. " +
                                      "DO NOT repeat the same failed tool call. If 'rename_symbol' failed, use 'replace_text' instead." 
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
                OnTurnModificationsPending?.Invoke(new Dictionary<string, string>(_stagedChanges));
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

            // 🚀 ABOSLUTE SILENCE ENGINE: More aggressive regex to catch all JSON variations
            // 1. Remove markdown-wrapped JSON tool calls (including partials)
            var mdPattern = @"(?s)```(?:json)?\s*\{.*?\}.*?```";
            var cleaned = System.Text.RegularExpressions.Regex.Replace(text, mdPattern, string.Empty);
            
            // 2. Remove raw JSON objects that look like tool calls (name/arguments/path)
            var rawPattern = @"(?s)\{\s*""(?:name|arguments|path|pattern)""\s*:\s*.*?\}.*?\}?";
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, rawPattern, string.Empty);
            
            // 3. Remove thinking process / thought tags ONLY if they are not meant to be shown
            // (We preserve them now for Antigravity-style verbosity)
            // cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?s)<thought>.*?</thought>", string.Empty);
            
            // 4. (v3.0) We NO LONGER strip plans, as users want to see the AI's intent.
            // cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?s)## PLAN\s*.*?(?=\n\n|\n#|$)", string.Empty);

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

        private OllamaOptions ApplyPerformancePresets(LocalPilot.Settings.PerformanceMode mode)
        {
            var options = new OllamaOptions();
            var settings = LocalPilot.Settings.LocalPilotSettings.Instance;

            // Start with synchronized global settings
            options.Temperature = settings.Temperature;
            options.NumPredict = settings.MaxChatTokens;

            switch (mode)
            {
                case LocalPilot.Settings.PerformanceMode.Fast:
                    options.NumCtx = 4096;
                    options.RepeatPenalty = 1.1;
                    options.TopP = 0.9;
                    break;
                case LocalPilot.Settings.PerformanceMode.HighAccuracy:
                    options.NumCtx = 16384; 
                    options.RepeatPenalty = 1.25;
                    options.TopP = 0.8;
                    options.TopK = 20;
                    break;
                case LocalPilot.Settings.PerformanceMode.Custom:
                case LocalPilot.Settings.PerformanceMode.Standard:
                default:
                    options.NumCtx = 8192;
                    options.RepeatPenalty = 1.1;
                    options.TopP = 0.9;
                    break;
            }
            return options;
        }

        // ── IMPLICIT CONTEXT PIPELINE ──────────────────────────────────────────

        private async Task<string> GetUnifiedContextAsync(string solutionPath, CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder();

            // 1. Diagnostics (Errors/Warnings)
            string diags = await GetVisibleDiagnosticsAsync(ct);
            if (!string.IsNullOrEmpty(diags))
            {
                sb.AppendLine("## ACTIVE DIAGNOSTICS (ERROR LIST)");
                sb.AppendLine(diags);
                sb.AppendLine();
            }

            // 2. Git State
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

        private async Task ShowDiffAsync(string originalPath, string newContent)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                string tempDir = Path.Combine(Path.GetTempPath(), "LocalPilot_Previews");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                
                string tempPath = Path.Combine(tempDir, "Preview_" + Path.GetFileName(originalPath));
                File.WriteAllText(tempPath, newContent);
                
                // Use native DTE command to ensure compatibility across VS versions
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                if (dte != null)
                {
                    // Tools.DiffFiles "file1" "file2"
                    dte.ExecuteCommand("Tools.DiffFiles", $"\"{originalPath}\" \"{tempPath}\"");
                }
            }
            catch { }
        }
    }
}
