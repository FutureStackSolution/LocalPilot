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
        private readonly ProjectMapService _projectMap;

        public event Action<AgentStatus, string> OnStatusUpdate;
        public event Action<ToolCallRequest> OnToolCallPending;
        public event Action<ToolCallRequest, ToolResponse> OnToolCallCompleted;
        public event Action<string> OnMessageFragment;
        public event Action<string> OnMessageCompleted;
        public event Action<Dictionary<string, string>> OnTurnModificationsPending; // 🚀 Antigravity Stage UX
        
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

            var systemPrompt = $@"You are LocalPilot, an elite AI developer inside Visual Studio.
WORKSPACE: {solutionPath}

### STRATEGY
1. **Parallelize**: Call multiple 'read_file' or 'grep_search' tools in one turn to gather context faster.
2. **Verify**: Always read a file before editing it.
3. **Accuracy**: Never guess paths. Use 'workspace_map' or 'grep_search' first.
4. **No Hallucination**: If you don't find a symbol, use 'list_directory'.

### TOOL CALL FORMAT
To use tools, return a JSON array of tool calls. You can call MULTIPLE tools at once for parallel execution.
Always include a <thought> block before your tool calls.

Example:
<thought>I need to read the class definition and its implementation.</thought>
```json
[
  {{ ""name"": ""read_file"", ""arguments"": {{ ""path"": ""Helper.cs"" }} }},
  {{ ""name"": ""read_file"", ""arguments"": {{ ""path"": ""Processor.cs"" }} }}
]
```
";

            string projectMapContent = "";
            if (LocalPilot.Settings.LocalPilotSettings.Instance.EnableProjectMap)
            {
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Analyzing project structure...");
                projectMapContent = await _projectMap.GenerateProjectMapAsync(solutionPath, 
                    maxTotalBytes: LocalPilot.Settings.LocalPilotSettings.Instance.MaxMapSizeKB * 1024);
                
            }

            var messages = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPrompt } };
            
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
                    
                    // Fallback to full file if no selection but we need a code block
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
                // Simple command without arguments: auto-inject selection
                var cmd = processedTask.ToLowerInvariant();
                var settings = LocalPilot.Settings.LocalPilotSettings.Instance;

                if (cmd == "/explain")       processedTask = settings.ExplainPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/fix")      processedTask = settings.FixPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/test")     processedTask = settings.TestPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/refactor")  processedTask = settings.RefactorPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/review")    processedTask = settings.ReviewPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/doc" || cmd == "/document") processedTask = settings.DocumentPrompt.Replace("{codeBlock}", activeSelection);
                else if (cmd == "/map")      
                {
                    OnStatusUpdate?.Invoke(AgentStatus.Thinking, "Updating Project Map...");
                    await _projectMap.GenerateProjectMapAsync(solutionPath, maxTotalBytes: settings.MaxMapSizeKB * 1024);
                    processedTask = "Please provide an executive summary of the project structure based on the updated map.";
                }
            }
            else if (processedTask.StartsWith("/"))
            {
                // Command with arguments: use the arguments as the context
                var parts = processedTask.Split(new[] { ' ' }, 2);
                var cmd = parts[0].ToLowerInvariant();
                var args = parts[1].Trim();
                var settings = LocalPilot.Settings.LocalPilotSettings.Instance;

                if (cmd == "/explain")       processedTask = settings.ExplainPrompt.Replace("{codeBlock}", args);
                else if (cmd == "/fix")      processedTask = settings.FixPrompt.Replace("{codeBlock}", args);
                else if (cmd == "/test")     processedTask = settings.TestPrompt.Replace("{codeBlock}", args);
                else if (cmd == "/refactor")  processedTask = settings.RefactorPrompt.Replace("{codeBlock}", args);
                else if (cmd == "/review")    processedTask = settings.ReviewPrompt.Replace("{codeBlock}", args);
                else if (cmd == "/doc" || cmd == "/document") processedTask = settings.DocumentPrompt.Replace("{codeBlock}", args);
            }

            if (!string.IsNullOrEmpty(projectMapContent))
            {
                messages.Add(new ChatMessage { Role = "user", Content = projectMapContent });
            }

            messages.Add(new ChatMessage { Role = "user", Content = processedTask });

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

            // User task is already added as 'processedTask' above for recency after the map.


            bool isDone = false;
            int maxSteps = 20;
            int step = 0;
            _stagedChanges.Clear();

            while (!isDone && step < maxSteps)
            {
                step++;
                OnStatusUpdate?.Invoke(AgentStatus.Thinking, string.Empty);

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
                    OnStatusUpdate?.Invoke(AgentStatus.Executing, $"Running {toolCalls.Count} tools...");

                    var toolTasks = toolCalls.Select(async toolCall =>
                    {
                        if (ct.IsCancellationRequested) return;

                        OnToolCallPending?.Invoke(toolCall);
                        
                        // Track changes instead of immediate commit
                        if (toolCall.Name == "write_to_file" || toolCall.Name == "replace_file_content")
                        {
                            var target = toolCall.Arguments?["TargetFile"]?.ToString();
                            var code = toolCall.Arguments?["CodeContent"]?.ToString() ?? toolCall.Arguments?["ReplacementContent"]?.ToString();
                            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(code))
                            {
                                lock (_stagedChanges) _stagedChanges[target] = code;
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
                                Role = "user",
                                Content = $"[Observation: Tool '{toolCall.Name}' executed]\nResult:\n{output}"
                            });
                        }
                    });

                    await Task.WhenAll(toolTasks);
                }
                else
                {
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
