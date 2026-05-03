using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Implements the "Context Auto-Compaction" pattern from the Knowledge Graph.
    /// Safely prunes chat history by summarizing decisions and keeping code artifacts.
    /// </summary>
    public class HistoryCompactor
    {
        private readonly OllamaService _ollama;

        public HistoryCompactor(OllamaService ollama)
        {
            _ollama = ollama;
        }

        public async Task<List<ChatMessage>> CompactIfNeededAsync(List<ChatMessage> history, string model, int threshold = 20)
        {
            // Only compact if history is getting deep (excluding system prompts)
            var userAssistantMessages = history.Where(m => m.Role != "system").ToList();
            if (userAssistantMessages.Count < threshold) return history;

            LocalPilotLogger.Log($"[Compactor] History threshold reached ({userAssistantMessages.Count}). Initiating auto-compaction...");

            // 1. Preserve the foundational system prompts and the original task
            var result = new List<ChatMessage>();
            result.AddRange(history.Where(m => m.Role == "system").Take(2)); // Identity & Context
            
            // 2. Identify the window to compact (everything except the last 6 messages)
            var toCompact = userAssistantMessages.Take(userAssistantMessages.Count - 6).ToList();
            var keptMessages = userAssistantMessages.Skip(userAssistantMessages.Count - 6).ToList();

            // 3. Extract Architectural Decisions & Code Snippets from the 'toCompact' window
            string architecturalSummary = await SummarizeDecisionsAsync(toCompact, model);
            
            // 4. Build the Compacted State Message
            var compactedState = new ChatMessage
            {
                Role = "system",
                Content = $"## CONTEXT AUTO-COMPACTION (History Restored)\n\n" +
                          $"Summary of previous architectural decisions and state:\n{architecturalSummary}\n\n" +
                          $"Note: Detailed logs for these turns have been pruned to optimize context window space."
            };

            result.Add(compactedState);
            result.AddRange(keptMessages);

            LocalPilotLogger.Log($"[Compactor] History compacted. Block size reduced from {userAssistantMessages.Count} to {keptMessages.Count + 1} turns.");
            
            return result;
        }

        private async Task<string> SummarizeDecisionsAsync(List<ChatMessage> messages, string model)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Summarize the following technical discussion into a bulleted list of key architectural decisions, file changes, and project state updates. Keep code snippets exactly as they are. ignore conversational filler.");
            sb.AppendLine();

            foreach (var m in messages)
            {
                // Prune metadata (thoughts/tool details) before summarizing to save LLM effort
                string content = m.Content;
                content = Regex.Replace(content, @"(?s)<thought>.*?</thought>", "[thought pruned]");
                content = Regex.Replace(content, @"(?s)\[Tool '.*?' result\].*?\n", "[tool log pruned]\n");
                
                sb.AppendLine($"{m.Role.ToUpper()}: {content}");
            }

            try
            {
                var options = new OllamaOptions { Temperature = 0.0, NumPredict = 1024 };
                var summarySb = new System.Text.StringBuilder(512);
                
                await foreach (var token in _ollama.StreamChatAsync(model, new List<ChatMessage> { 
                    new ChatMessage { Role = "user", Content = sb.ToString() } 
                }, options))
                {
                    summarySb.Append(token);
                }

                return summarySb.ToString();
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[Compactor] Summarization failed: {ex.Message}. Falling back to basic pruning.");
                return "Error during summarization. History was pruned to save tokens.";
            }
        }
    }
}
