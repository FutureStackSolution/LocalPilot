using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Universal semantic provider for non-Roslyn languages (Python, Go, Web, Rust, etc.).
    /// Uses a combination of Regex heuristics and Visual Studio's legacy DTE bridge.
    /// </summary>
    public class UniversalSemanticProvider : ISemanticProvider
    {
        public bool CanHandle(string extension)
        {
            // Catch-all for everything that isn't handled by a specialized provider (like Roslyn)
            return extension != ".cs" && extension != ".vb";
        }

        public string GetSummary()
        {
            return "INTELLIGENCE: Polyglot Heuristics Active. Supporting JS/TS, Python, Go, and more via semantic scanning.";
        }

        public Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct)
        {
            // Fallback for non-Roslyn: empty for now as grep_search is the primary discovery tool for polyglot
            return Task.FromResult(new List<SymbolLocation>());
        }

        public async Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                
                // 🚀 SENIOR ARCHITECT FIX: Use Live Buffer for context heuristics
                string content = await ProjectMapService.Instance.GetLiveContentAsync(filePath);
                
                var sb = new StringBuilder();
                sb.AppendLine($"## SEMANTIC CONTEXT: {Path.GetFileName(filePath)} (Heuristics)");
                sb.AppendLine("[WARNING] Preprocessor-Blind Heuristic active. Context may include inactive #if blocks.");

                // Directory Awareness (First 10 files)
                var dir = Path.GetDirectoryName(filePath);
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir).Select(Path.GetFileName).Take(10);
                    sb.AppendLine($"Folder [{dir}]: " + string.Join(", ", files));
                }

                switch (ext)
                {
                    case ".ts":
                    case ".tsx":
                    case ".js":
                    case ".jsx":
                        // 🚀 WEB HEURISTICS: Deep scanning for React/Angular/Vue
                        var webMatches = Regex.Matches(content, @"(?:export\s+)?(?:class|interface|type|const|function|enum)\s+(?<name>[a-zA-Z_]\w*)");
                        foreach (Match m in webMatches) sb.AppendLine($" - Symbol: {m.Groups["name"].Value}");
                        
                        // React Specifics
                        if (content.Contains("useEffect") || content.Contains("useState") || content.Contains("useContext") || content.Contains("useMemo") || content.Contains("useCallback"))
                            sb.AppendLine(" - Tech: React (Functional/Hooks)");
                        
                        // Angular Specifics
                        if (content.Contains("@Component") || content.Contains("@Injectable") || content.Contains("@Directive") || content.Contains("@NgModule"))
                            sb.AppendLine(" - Tech: Angular (Decorator-driven)");
                        
                        // Import Context (Top 5 libraries)
                        var imports = Regex.Matches(content, @"from\s+['""](?<lib>[\w@\/\-]+)['""]").Cast<Match>().Select(m => m.Groups["lib"].Value).Distinct().Take(5);
                        if (imports.Any()) sb.AppendLine(" - Imports: " + string.Join(", ", imports));
                        break;

                    case ".py":
                        var pyMatches = Regex.Matches(content, @"(?:class|def)\s+(?<name>[a-zA-Z_]\w*)");
                        foreach (Match m in pyMatches) sb.AppendLine($" - Python Symbol: {m.Groups["name"].Value}");
                        break;

                    case ".go":
                        var goPackage = Regex.Match(content, @"package\s+(?<name>\w+)");
                        if (goPackage.Success) sb.AppendLine($" - Go Package: {goPackage.Groups["name"].Value}");
                        var goMatches = Regex.Matches(content, @"type\s+(?<name>\w+)\s+(?:struct|interface)");
                        foreach (Match m in goMatches) sb.AppendLine($" - Go Type: {m.Groups["name"].Value}");
                        break;
                }

                return sb.ToString();
            }
            catch { return null; }
        }

        public async Task<string> GetDiagnosticsAsync(CancellationToken ct)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                if (dte == null) return null;

                var items = dte.ToolWindows.ErrorList.ErrorItems;
                if (items.Count == 0) return null;

                var sb = new StringBuilder();
                int count = 0;
                for (int i = 1; i <= items.Count; i++)
                {
                    var item = items.Item(i);
                    // For polyglot, we show both errors and warnings if available
                    string level = ((int)item.ErrorLevel == 1) ? "ERROR" : "WARNING";
                    sb.AppendLine($"[{level}] {item.Description} (at {Path.GetFileName(item.FileName)}:{item.Line})");
                    count++;
                    if (count >= 10) break;
                }
                return count > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        public Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct)
        {
            return Task.FromResult("Error: Semantic refactoring is currently only optimized for Roslyn-compatible languages (C#, VB). For this file type, please use the 'replace_text' tool or 'grep_search' to update references manually.");
        }
    }
}
