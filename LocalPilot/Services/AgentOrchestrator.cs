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
            string implicitContext = await GetUnifiedContextAsync(solutionPath);
            if (!string.IsNullOrEmpty(implicitContext))
            {
                messages.Add(new ChatMessage { Role = "system", Content = implicitContext });
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
                var locs = await SymbolIndexService.Instance.FindDefinitionsAsync(name);
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
                    string neighborhood = await SymbolIndexService.Instance.GetNeighborhoodContextAsync(activeDoc.FilePath);
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

                string contextModel = LocalPilot.Settings.LocalPilotSettings.Instance.ChatModel;
                var options = ApplyPerformancePresets(LocalPilot.Settings.LocalPilotSettings.Instance.Mode);

                // Stream with native tool calling
                await foreach (var result in _ollama.StreamChatWithToolsAsync(contextModel, messages, toolDefinitions, options, ct))
                {
                    if (result.IsTextToken)
                    {
                        responseBuilder.Append(result.TextToken);
                        OnMessageFragment?.Invoke(result.TextToken);
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

                // Show completed message text (if any)
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    OnMessageCompleted?.Invoke(responseText);
                }

                // Add assistant response to history
                messages.Add(new ChatMessage { Role = "assistant", Content = responseText });

                // 2. Process tool calls (if any)
                if (toolCallsThisTurn.Any())
                {
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

                        var result = await _toolRegistry.ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                        OnToolCallCompleted?.Invoke(toolCall, result);

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

                        // 🚀 AUTO-VERIFICATION: If we edited code, immediately check for errors
                        if (isWrite && !result.IsError)
                        {
                            var diagResult = await GetVisibleDiagnosticsAsync();
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

        private async Task<string> GetUnifiedContextAsync(string solutionPath)
        {
            var sb = new System.Text.StringBuilder();

            // 1. Diagnostics (Errors/Warnings)
            string diags = await GetVisibleDiagnosticsAsync();
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

        private async Task<string> GetVisibleDiagnosticsAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                if (dte == null) return null;

                var items = dte.ToolWindows.ErrorList.ErrorItems;
                if (items.Count == 0) return null;

                var sb = new System.Text.StringBuilder();
                int count = 0;
                for (int i = 1; i <= items.Count; i++)
                {
                    var item = items.Item(i);
                    // Only show Errors (not warnings/info) to save tokens (1 = vsBuildErrorLevelHigh)
                    if ((int)item.ErrorLevel == 1)
                    {
                        sb.AppendLine($"[ERROR] {item.Description} (at {Path.GetFileName(item.FileName)}:{item.Line})");
                        count++;
                        if (count >= 10) break; 
                    }
                }
                return sb.ToString();
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
                if (!File.Exists(originalPath)) return;

                string tempDir = Path.Combine(Path.GetTempPath(), "LocalPilot_Previews");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                
                string tempPath = Path.Combine(tempDir, "Preview_" + Path.GetFileName(originalPath));
                File.WriteAllText(tempPath, newContent);
                
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
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
