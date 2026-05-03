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
            // Only compact if history is getting deep
            if (history.Count < threshold) return history;

            LocalPilotLogger.Log($"[Compactor] History threshold reached ({history.Count}). Initiating KV-cache aware auto-compaction...");

            // 🚀 WORLD-CLASS PERFORMANCE: Stable Prefix Strategy (KV-Cache Reuse)
            // We keep the first 3 messages (usually System + Initial Context + Initial Task) 
            // completely unchanged. This allows Ollama to skip processing these tokens on every turn.
            var result = new List<ChatMessage>();
            var stablePrefix = history.Take(3).ToList();
            result.AddRange(stablePrefix);
            
            var dynamicHistory = history.Skip(3).ToList();
            if (dynamicHistory.Count < 10) return history; // Not enough to summarize

            // Identify the 'Middle' to compact and the 'Recent' to keep
            var toCompact = dynamicHistory.Take(dynamicHistory.Count - 6).ToList();
            var keptMessages = dynamicHistory.Skip(dynamicHistory.Count - 6).ToList();

            // Extract Architectural Decisions & Code Snippets from the 'Middle' window
            string architecturalSummary = await SummarizeDecisionsAsync(toCompact, model);
            
            // Build the Compacted State Message
            var compactedState = new ChatMessage
            {
                Role = "system",
                Content = $"## CONTEXT AUTO-COMPACTION (History Restored)\n\n" +
                          $"Summary of previous architectural decisions and state:\n{architecturalSummary}\n\n" +
                          $"Note: To optimize KV-cache performance, the middle of the conversation has been summarized."
            };

            result.Add(compactedState);
            result.AddRange(keptMessages);

            LocalPilotLogger.Log($"[Compactor] History compacted. Block size reduced from {history.Count} to {result.Count} turns. Stable prefix preserved.");
            
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
