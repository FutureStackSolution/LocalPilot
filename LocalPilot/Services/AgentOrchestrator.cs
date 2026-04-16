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
    /// by combining the LLM's reasoning with local tools.
    /// </summary>
    public class AgentOrchestrator
    {
        private readonly OllamaService _ollama;
        private readonly ToolRegistry _toolRegistry;
        private readonly ProjectContextService _projectContext;

        public event Action<AgentStatus, string> OnStatusUpdate;
        public event Action<ToolCallRequest> OnToolCallPending;
        public event Action<ToolCallRequest, ToolResponse> OnToolCallCompleted;
        public event Action<string> OnMessageFragment;
        public event Action<string> OnMessageCompleted;
        public event Action<AgentPlan> OnPlanReady;
        public Func<ToolCallRequest, Task<bool>> RequestPermissionAsync;

        public AgentOrchestrator(OllamaService ollama, ToolRegistry toolRegistry, ProjectContextService projectContext)
        {
            _ollama = ollama;
            _toolRegistry = toolRegistry;
            _projectContext = projectContext;
        }

        /// <summary>
        /// Initiates an autonomous task for the agent.
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

            _toolRegistry.WorkspaceRoot = solutionPath;
            string toolList = string.Join("\n", _toolRegistry.GetAllTools().Select(t => $"- {t.Name}: {t.Description}\n  Parameters: {t.ParameterSchema}"));

            var systemPrompt = $@"You are LocalPilot Agent Mode, a highly capable autonomous coding assistant inside Visual Studio.
WORKSPACE ROOT: {solutionPath}

### STEP 1 — PLAN (REQUIRED ON FIRST TURN ONLY)
Before doing any work, output a short, human-friendly plan using EXACTLY this format:

Steps:
1. <Bold label>: brief description of first action
2. <Bold label>: brief description of second action
...

Then add a blank line and begin executing.
Do NOT repeat the plan on subsequent turns.

### OPERATIONAL RULES
1. **THINK BEFORE ACTING**: Use a <thought> block to deliberate on the context, potential pitfalls, and your next move.
2. **FOCUS ON THE TASK**: Your primary goal is to perform the requested edit correctly.
3. **READ BEFORE WRITE**: Always 'read_file' to get the latest content before performing a 'replace_text'.
4. **VERIFY BEFORE MODIFYING**: If you are unsure where a symbol is defined, use 'grep_search'.
5. **INTELLIGENT RECOVERY**: If 'read_file' fails (path not found), use 'list_directory' to explore and find the correct path.
6. **NO HALLUCINATIONS**: Never assume a file exists or an API is available without evidence.
7. **CITATIONS**: Always cite your sources using the [source: Filename.cs] format when referencing code from the project snippets.
8. **AMBIGUITY**: If the task is unclear, ask the user for clarification instead of guessing.

### AVAILABLE TOOLS
{toolList}

### RESPONSE FORMAT
For the FIRST turn: output the Steps plan, then a <thought> block, then tool call(s).
For ALL turns: Provide your tool call(s) in a single ```json block.
The content MUST be a JSON array of objects with ""name"" and ""arguments"".

Example:
Steps:
1. **Read file**: Read Calculator.cs to see the current Add method.
2. **Update method**: Rename Add to Sum.

<thought>
I need to examine Calculator.cs first to ensure I have the exact content for replacement.
</thought>
```json
[
  {{
    ""name"": ""read_file"",
    ""arguments"": {{ ""path"": ""Calculator.cs"" }}
  }}
]
```";

            var messages = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPrompt } };

            // Fetch initial grounding context from the project
            string groundingContext = await _projectContext.SearchContextAsync(_ollama, taskDescription, topN: 5);
            if (!string.IsNullOrEmpty(groundingContext))
            {
                messages.Add(new ChatMessage { Role = "system", Content = groundingContext });
            }

            // Enterprise Intelligence: Inject Active Document context for better grounding
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                if (activeDoc?.FilePath != null)
                {
                    string content = activeDoc.TextBuffer.CurrentSnapshot.GetText();
                    if (content.Length > 5000) content = content.Substring(0, 5000) + "... [truncated]";
                    
                    messages.Add(new ChatMessage { 
                        Role = "system", 
                        Content = $"[REFERENCE: ACTIVE EDITOR SNIPPET]\nPath: {activeDoc.FilePath}\nContent:\n{content}\n\nNOTE: This is just a snippet from the editor. Use read_file for the full, latest content." 
                    });
                }
            }
            catch { }

            // User task MUST be the final message for recency bias
            messages.Add(new ChatMessage { Role = "user", Content = $"Task: {taskDescription}" });

            bool isDone = false;
            int maxSteps = 20;
            int step = 0;
            bool planEmitted = false;

            while (!isDone && step < maxSteps)
            {
                step++;
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Reasoning step {step}...");

                // 1. Get LLM reasoning + optional tool call
                var responseBuilder = new System.Text.StringBuilder();
                bool isUserOutputSuppressed = false;

                string contextModel = LocalPilot.Settings.LocalPilotSettings.Instance.ChatModel;
                var options = ApplyPerformancePresets(LocalPilot.Settings.LocalPilotSettings.Instance.Mode);

                await foreach (var token in _ollama.StreamChatAsync(contextModel, messages, options, ct))

                {
                    responseBuilder.Append(token);

                    // Stop showing fragments to user once we hit a JSON block
                    string currentText = responseBuilder.ToString();
                    if (!isUserOutputSuppressed && (currentText.Contains("```json") || currentText.Contains("<|") || currentText.Contains("< |")))
                    {
                        isUserOutputSuppressed = true;
                    }

                    if (!isUserOutputSuppressed)
                    {
                        // Heuristic: If we detect reasoning patterns without tags, wrap them visually for segments
                        string frag = token;
                        if (currentText.Length < 200 && (frag.ToLower().Contains("let me") || frag.ToLower().Contains("first, i") || frag.ToLower().Contains("analyzing")))
                        {
                            // Wrap the fragment in a pseudo-thought block if it looks like the start of reasoning
                            frag = "[Reasoning] " + frag;
                        }
                        OnMessageFragment?.Invoke(frag);
                    }
                }

                var response = responseBuilder.ToString();

                // ── PLAN EXTRACTION (first turn only) ──────────────────────
                if (!planEmitted && step == 1)
                {
                    planEmitted = true;
                    var plan = TryParsePlan(response);
                    if (plan != null && plan.Steps.Count > 0)
                    {
                        OnPlanReady?.Invoke(plan);
                        OnStatusUpdate?.Invoke(AgentStatus.Planning, $"{plan.Steps.Count} steps planned");
                    }
                }

                // Show final clean message if it was a direct reply, 
                // OR just end the fragment stream if we was performing tools.
                if (!isUserOutputSuppressed)
                {
                    OnMessageCompleted?.Invoke(response);
                }
                else
                {
                    // If we suppressed, just close the visible message box with the pre-JSON/pre-tag text
                    var cleanText = response.Split(new[] { "```json", "<|", "< |" }, StringSplitOptions.None)[0].Trim();
                    OnMessageCompleted?.Invoke(cleanText);
                }

                // Add assistant response to history
                messages.Add(new ChatMessage { Role = "assistant", Content = response });

                // 2. Parse for ALL tool calls in response
                var toolCalls = ParseAllToolCalls(response);
                if (toolCalls.Any())
                {
                    var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var toolCall in toolCalls)
                    {
                        if (ct.IsCancellationRequested) break;

                        string signature = BuildToolSignature(toolCall);
                        if (!seenToolCalls.Add(signature))
                        {
                            LocalPilotLogger.Log($"[Agent] Skipping duplicate tool call in same turn: {toolCall.Name}");
                            continue;
                        }

                        OnStatusUpdate?.Invoke(AgentStatus.Executing, $"Agent executing tool: {toolCall.Name}");

                        // PAUSE FOR APPROVAL if it's a 'Write' tool and setting is enabled
                        if (LocalPilot.Settings.LocalPilotSettings.Instance.RequireApprovalForWrites && IsWriteTool(toolCall.Name))
                        {
                            OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Waiting for approval to run {toolCall.Name}...");
                            if (RequestPermissionAsync != null)
                            {
                                bool approved = await RequestPermissionAsync(toolCall);
                                if (!approved)
                                {
                                    OnStatusUpdate?.Invoke(AgentStatus.Idle, "User rejected tool execution. Task cancelled.");
                                    return; // Stop the whole agent loop
                                }
                            }
                        }

                        OnToolCallPending?.Invoke(toolCall);

                        ToolResponse toolResult = null;
                        int attempts = 0;
                        const int maxAttempts = 3;
                        bool shouldRetry;
                        do
                        {
                            attempts++;
                            toolResult = await _toolRegistry.ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                            if (!toolResult.IsError || ct.IsCancellationRequested) break;

                            shouldRetry = attempts < maxAttempts && IsTransientToolError(toolResult.Output);
                            if (shouldRetry)
                            {
                                OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Tool failed (attempt {attempts}/{maxAttempts}). Retrying...");
                                await Task.Delay(500, ct);
                            }
                        }
                        while (attempts < maxAttempts && toolResult != null && toolResult.IsError && !ct.IsCancellationRequested && shouldRetry);

                        if (toolResult != null && toolResult.IsError)
                        {
                            var retryInfo = attempts > 1 ? $" after {attempts} attempt(s)" : string.Empty;
                            OnStatusUpdate?.Invoke(AgentStatus.Thinking, $"Tool '{toolCall.Name}' failed{retryInfo}.");
                        }

                        OnToolCallCompleted?.Invoke(toolCall, toolResult);

                        // Add tool result back as a system observation
                        string output = toolResult.Output ?? string.Empty;
                        
                        // Intelligent Truncation: Save context window for local models
                        if (output.Length > 8000) 
                        {
                            output = output.Substring(0, 8000) + "... [Output heavily truncated]";
                        }

                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = $"[Observation: Tool '{toolCall.Name}' executed]\nResult:\n{output}"
                        });
                    }
                }
                else
                {
                    // No tool call means the agent has provided a final answer or is just talking
                    isDone = true;

                    if (response.ToLower().Contains("failed") || response.ToLower().Contains("unable to"))
                    {
                        OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task stopped (check response).");
                    }
                    else
                    {
                        OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task finished.");
                    }
                }

            }

            if (!isDone && !ct.IsCancellationRequested && step >= maxSteps)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Completed, "Task stopped at maximum reasoning steps.");
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



        private AgentPlan TryParsePlan(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;

            // Plan should appear before the first tool-call JSON block.
            string preJson = response.Split(new[] { "```json" }, StringSplitOptions.None)[0];
            int stepsHeader = preJson.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
            if (stepsHeader < 0) return null;

            string planBlock = preJson.Substring(stepsHeader);
            string[] lines = planBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            var plan = new AgentPlan();
            string preamble = preJson.Substring(0, stepsHeader).Trim();
            if (!string.IsNullOrWhiteSpace(preamble))
            {
                plan.Preamble = preamble;
            }

            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Equals("Steps:", StringComparison.OrdinalIgnoreCase)) continue;

                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(\d+[\.\)]|-)\s+"))
                {
                    string stepText = System.Text.RegularExpressions.Regex
                        .Replace(line, @"^(\d+[\.\)]|-)\s+", string.Empty)
                        .Trim();

                    if (!string.IsNullOrWhiteSpace(stepText))
                    {
                        plan.Steps.Add(stepText);
                    }
                }
                else if (plan.Steps.Count > 0)
                {
                    // Continuation line for the previous step only when explicitly indented.
                    // If not indented, this is likely normal narrative text after the plan.
                    if (raw.StartsWith(" ") || raw.StartsWith("\t"))
                    {
                        plan.Steps[plan.Steps.Count - 1] += " " + line;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return plan.Steps.Count > 0 ? plan : null;
        }

        private List<ToolCallRequest> ParseAllToolCalls(string content)
        {
            var results = new List<ToolCallRequest>();
            try
            {
                int pos = 0;
                while ((pos = content.IndexOf("```json", pos, StringComparison.OrdinalIgnoreCase)) != -1)
                {
                    int start = pos + 7;
                    int end = content.IndexOf("```", start);
                    if (end == -1) { pos = start; continue; }

                    string json = content.Substring(start, end - start).Trim();
                    pos = end + 3;

                    // Skip empty blocks
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    try
                    {
                        var tokenObj = SafeParseJson(json);
                        
                        // Handle both array [...] and single object {...}
                        IEnumerable<JToken> items = tokenObj is JArray arr ? (IEnumerable<JToken>)arr : new[] { tokenObj };

                        foreach (var item in items)
                        {
                            if (item is not JObject obj) continue;

                            // Support BOTH formats:
                            // { "name": "tool", "arguments": {...} }          <- preferred, clean format  
                            // { "action": "tool_call", "name": ..., ... }     <- legacy format
                            string name = obj["name"]?.ToString();
                            var arguments = obj["arguments"]?.ToObject<Dictionary<string, object>>()
                                         ?? obj["parameters"]?.ToObject<Dictionary<string, object>>()
                                         ?? new Dictionary<string, object>();

                            // Only skip if no name found at all (completely malformed)
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            // Validate it's a known tool (prevents hallucinated tool calls)
                            if (!_toolRegistry.HasTool(name)) 
                            {
                                LocalPilotLogger.Log($"[Agent] Unknown tool '{name}' in response - skipping.");
                                continue;
                            }

                            results.Add(new ToolCallRequest { Name = name, Arguments = arguments });
                            LocalPilotLogger.Log($"[Agent] Parsed tool call: {name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.Log($"[Agent] JSON parse error in block: {ex.Message}\nBlock: {json.Substring(0, Math.Min(200, json.Length))}");
                    }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[Agent] ParseAllToolCalls outer error: {ex.Message}");
            }
            return results;
        }

        private JToken SafeParseJson(string json)
        {
            string repaired = json.Trim();

            // 1. Try standard parse first
            try { return JToken.Parse(repaired); } catch { }

            // 2. Intelligent Repair: LLMs often forget to close the nested 'arguments' object 
            // especially when the content contains C# braces.
            
            // Strip trailing brackets/braces to find the actual 'end' of content
            string baseJson = repaired.TrimEnd(']', '}', ' ', '\n', '\r', '\t');
            
            // Try wrapping combinations to close unclosed objects/arrays properly
            string[] suffixes = { 
                "}",         // Just close one object
                "}}",        // Close arguments AND tool object
                "}]",        // Close tool object AND array
                "}}] ",      // Close arguments, tool object, AND array
                "}]}"        // Close tool object, array, AND accidentally opened outer brace
            };

            foreach (var s in suffixes)
            {
                try { return JToken.Parse(baseJson + s); } catch { }
            }

            // 3. Last resort: If we can't repair it, maybe it's missing quotes? 
            // Actually, just re-throw to trigger the standard error logging.
            return JToken.Parse(json);
        }

        private ToolCallRequest ParseSingleToolCall(JToken obj)
        {
            return new ToolCallRequest
            {
                Name = obj["name"]?.ToString(),
                Arguments = obj["arguments"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
            };
        }
        private bool IsWriteTool(string name)
        {
            return name == "write_file" || name == "replace_text" || name == "delete_file" || name == "run_terminal";
        }

        private string BuildToolSignature(ToolCallRequest toolCall)
        {
            string argsJson = JsonConvert.SerializeObject(toolCall?.Arguments ?? new Dictionary<string, object>());
            return $"{toolCall?.Name}|{argsJson}";
        }

        private bool IsTransientToolError(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return false;
            string t = output.ToLowerInvariant();

            return t.Contains("timeout")
                || t.Contains("temporar")
                || t.Contains("rate limit")
                || t.Contains("busy")
                || t.Contains("network")
                || t.Contains("connection")
                || t.Contains("io exception")
                || t.Contains("access is denied")
                || t.Contains("locked");
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
                    options.RepeatPenalty = 1.25; // Stronger penalty for greedy sampling
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
    }
}
