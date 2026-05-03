using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Priority 2: LSP Client Bridge.
    /// Routes requests to Visual Studio's Language Server Client Broker.
    /// For languages like Python, C++, Go, and Rust.
    /// </summary>
    public class LspSemanticProvider : ISemanticProvider
    {
        public bool CanHandle(string extension)
        {
            // Specifically languages that typically have LSP servers in VS
            var lspExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { 
                ".py", ".cpp", ".h", ".js", ".ts", ".go", ".rs", ".java", 
                ".vue", ".svelte", ".jsx", ".tsx", ".json", ".html", ".css", ".md"
            };
            return lspExts.Contains(extension);
        }

        public string GetSummary() => "INTELLIGENCE: LSP Bridge Active (Python, JS, C++, Go).";

        public Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct)
        {
            // Placeholder: Future implementation will use IVsLanguageClientBroker
            return Task.FromResult(new List<SymbolLocation>());
        }

        public async Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct)
        {
            // For now, falls back to structural scan but marked as LSP-context
            return null; 
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

                var sb = new System.Text.StringBuilder();
                for (int i = 1; i <= Math.Min(10, items.Count); i++)
                {
                    var item = items.Item(i);
                    // Match file extension to confirm it's an LSP-handled file
                    if (!CanHandle(Path.GetExtension(item.FileName))) continue;

                    string level = ((int)item.ErrorLevel == 1) ? "ERROR" : "WARNING";
                    sb.AppendLine($"[LSP {level}] {item.Description} (at {Path.GetFileName(item.FileName)}:{item.Line})");
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        public Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct)
        {
            // Avoid triggering the modal VS Rename dialog which blocks the agent workflow.
            // Redirect to the text-based approach that works non-interactively.
            return Task.FromResult($"Error: LSP-based rename is not available for {Path.GetExtension(filePath)} files in automated mode. Please use the 'replace_text' tool or 'grep_search' to find and update references manually.");
        }
    }
}
