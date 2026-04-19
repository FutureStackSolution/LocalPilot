using LocalPilot.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            var sb = new StringBuilder();
            sb.AppendLine($"## PROACTIVE STRUCTURAL CONTEXT: {Path.GetFileName(filePath)}");

            try
            {
                // 1. Scan current file for imports/dependencies
                var imports = new List<string>();
                string[] lines;
                using (var stream = await ProjectMapService.Instance.GetLiveStreamAsync(filePath))
                using (var reader = new StreamReader(stream))
                {
                    string content = await reader.ReadToEndAsync();
                    lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                }

                foreach (var line in lines.Take(100)) // Only scan top of file for performance
                {
                    // Catch: import { x } from 'y', require('y'), include 'y', using y;
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"(?:import|require|include|using)\s+['""]?([^'""\s;]+)['""]?");
                    if (match.Success)
                    {
                        string dep = match.Groups[1].Value.Split('/').Last().Split('.').First(); // Get filename only
                        if (!string.IsNullOrEmpty(dep) && dep.Length > 2) imports.Add(dep);
                    }
                }

                // 2. For the top 3 unique imports, find their probable files and list public members
                var uniqueImports = imports.Distinct().Take(3);
                foreach (var imp in uniqueImports)
                {
                    var results = ProjectMapService.Instance.FindSymbols(imp);
                    var bestMatch = results.FirstOrDefault();
                    if (bestMatch != null && bestMatch.FilePath != filePath)
                    {
                        sb.AppendLine($"  - Detected Dependency: {imp} (Probable: {Path.GetFileName(bestMatch.FilePath)})");
                        
                        // Proactively scan the discovered dependency for "Exports"
                        try {
                            var depContent = File.ReadLines(bestMatch.FilePath).Take(500);
                            var exports = depContent
                                .Where(l => l.Contains("export ") || l.Contains("public ") || l.Contains("class ") || l.Contains("func"))
                                .Select(l => l.Trim())
                                .Take(5);
                            
                            if (exports.Any())
                            {
                                sb.AppendLine($"    Signatures Found: {string.Join(", ", exports)}");
                            }
                        } catch { }
                    }
                }

                // 3. List local structure
                sb.AppendLine("  - Local Members Found:");
                foreach (var line in lines)
                {
                    if (line.Contains("class ") || line.Contains("function ") || line.Contains("def ") || (line.Contains("export ") && !line.Contains("from")))
                    {
                        sb.AppendLine($"    {line.Trim()}");
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex) { 
                LocalPilotLogger.Log($"[LocalParser] Neighborhood scanning failed: {ex.Message}");
                return null; 
            }
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
