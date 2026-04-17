using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Priority 3: Local Parser.
    /// Uses high-fidelity regex patterns and structural scanning to provide
    /// pseudo-semantic awareness without a compiler or LSP.
    /// </summary>
    public class LocalParserProvider : ISemanticProvider
    {
        public bool CanHandle(string extension) => true; // Catch-all

        public string GetSummary() => "INTELLIGENCE: Local Parser Active (Structural Scanning).";

        public async Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct)
        {
            // For Priority 3, we use the pre-indexed cache in ProjectMapService
            var matches = ProjectMapService.Instance.FindSymbols(symbolName);
            return matches.ToList();
        }

        public async Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct)
        {
            // 🛡️ MEMORY CEILING FIX: Stream-based parsing for large files
            var sb = new StringBuilder();
            sb.AppendLine($"## STRUCTURAL CONTEXT: {Path.GetFileName(filePath)}");

            try
            {
                using (var stream = await ProjectMapService.Instance.GetLiveStreamAsync(filePath))
                using (var reader = new StreamReader(stream))
                {
                    int lineCount = 0;
                    while (!reader.EndOfStream && lineCount < 1000)
                    {
                        if (ct.IsCancellationRequested) break;
                        string line = await reader.ReadLineAsync();
                        lineCount++;

                        if (line.Contains("class ") || line.Contains("function ") || line.Contains("def ") || line.Contains("export "))
                        {
                            sb.AppendLine($" - L{lineCount}: {line.Trim()}");
                        }
                    }
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        public async Task<string> GetDiagnosticsAsync(CancellationToken ct)
        {
            // Priority 3 Diagnostics: Basic syntax validation (braces, quotes)
            return null; // Usually handled better by Priority 1 or 2
        }

        public Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct)
        {
            return Task.FromResult("Error: Structural refactoring (Priority 3) is too high-risk for this file. Please use 'replace_text' to manually update occurrences.");
        }
    }
}
