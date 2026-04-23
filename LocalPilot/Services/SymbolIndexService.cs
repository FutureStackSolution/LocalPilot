using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Senior Architect Grade Dispatcher.
    /// Implements the 3-Tier Semantic Priority Chain:
    /// 1. Roslyn (Native .NET)
    /// 2. LSP (Language Servers)
    /// 3. Local Parser (Structural Heuristics)
    /// </summary>
    public class SymbolIndexService
    {
        private static readonly SymbolIndexService _instance = new SymbolIndexService();
        public static SymbolIndexService Instance => _instance;

        private readonly List<ISemanticProvider> _providers = new List<ISemanticProvider>();

        private SymbolIndexService() 
        {
            // 🛡️ ARCHITECTURE STRATEGY: Priority Registry
            _providers.Add(new RoslynSemanticProvider());    // Tier 1: C# / VB.NET
            _providers.Add(new LspSemanticProvider());       // Tier 2: LSP (TypeScript, Python, etc.)
            _providers.Add(new UniversalSemanticProvider()); // Tier 3: Polyglot Heuristics (Enterprise Stack)
        }

        public RoslynSemanticProvider GetRoslynProvider()
        {
            return _providers.OfType<RoslynSemanticProvider>().FirstOrDefault();
        }

        private IEnumerable<ISemanticProvider> GetOrderedProviders(string filePath)
        {
            string ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
            return _providers.Where(p => p.CanHandle(ext)).Concat(_providers.Where(p => !p.CanHandle(ext))).Distinct();
        }

        public async Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct)
        {
            // Race check: Fast cache first
            var cachedMatches = ProjectMapService.Instance.FindSymbols(symbolName);
            if (cachedMatches.Any()) return cachedMatches.ToList();

            // Chain of Responsibility: Task-based fallback
            foreach (var provider in GetOrderedProviders(null))
            {
                if (ct.IsCancellationRequested) break;
                var results = await provider.FindDefinitionsAsync(symbolName, ct);
                if (results != null && results.Any()) return results;
            }
            return new List<SymbolLocation>();
        }

        public string GetSummary()
        {
            return string.Join(" ", _providers.Select(p => p.GetSummary()));
        }

        public async Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct)
        {
            foreach (var provider in GetOrderedProviders(filePath))
            {
                if (ct.IsCancellationRequested) break;
                string context = await provider.GetNeighborhoodContextAsync(filePath, ct);
                if (!string.IsNullOrEmpty(context)) return context;
            }
            return null;
        }

        public async Task<string> GetDiagnosticsAsync(CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in _providers)
            {
                if (ct.IsCancellationRequested) break;
                string diag = await p.GetDiagnosticsAsync(ct);
                if (!string.IsNullOrEmpty(diag)) sb.Append(diag);
            }
            string result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        public async Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct)
        {
            var reports = new List<string>();
            
            // 🛡️ CIRCUIT BREAKER: Tiered Execution with Diagnostic Aggregation
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutSource.CancelAfter(60000); // 60s timeout for deep semantic analysis
                
                foreach (var provider in GetOrderedProviders(filePath))
                {
                    string tierName = provider.GetType().Name.Replace("SemanticProvider", "");
                    try
                    {
                        string result = await provider.RenameSymbolAsync(filePath, line, column, newName, timeoutSource.Token);
                        if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error")) return result;
                        
                        reports.Add($"{tierName}: {result ?? "No response"}");
                    }
                    catch (OperationCanceledException) 
                    { 
                        reports.Add($"{tierName}: Timed out (>60000ms)");
                        break; 
                    }
                    catch (Exception ex) 
                    { 
                        reports.Add($"{tierName}: Failed ({ex.Message})");
                        continue; 
                    }
                }
            }

            string fullReport = string.Join(" | ", reports);
            LocalPilotLogger.Log($"[SymbolIndex] Rename failed. Tier Reports: {fullReport}");
            
            return $"Error: Refactoring failed. Detailed Diagnostics: [{fullReport}]. Suggestion: Fall back to 'replace_text'.";
        }
    }
}
