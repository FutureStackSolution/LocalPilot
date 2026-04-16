using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// High-performance symbol indexer for LocalPilot.
    /// Scans source files for class, method, and property definitions using regex.
    /// This provides the LLM with a global map of the API without reading every file.
    /// </summary>
    public class SymbolIndexService
    {
        private static readonly Regex SymbolRegex = new Regex(
            @"(?:public|private|internal|protected|static|async|virtual|override|abstract|sealed)\s+" +
            @"(?:(?:class|interface|struct|enum)\s+(?<name>[A-Z]\w*))|" +
            @"(?:(?<type>[A-Z]\w*(?:<[^>]+>)?(?:\[\])?)\s+(?<name>[A-Z]\w*)\s*(?:\(|{))",
            RegexOptions.Compiled);

        private Dictionary<string, List<SymbolInfo>> _index = new Dictionary<string, List<SymbolInfo>>();

        public async Task BuildIndexAsync(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return;

            var newIndex = new Dictionary<string, List<SymbolInfo>>();
            var excludes = new[] { "bin", "obj", ".git", "node_modules" };
            
            var files = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !excludes.Any(e => f.Contains(Path.DirectorySeparatorChar + e + Path.DirectorySeparatorChar)))
                .Take(200); // Limit indexing speed for first pass

            var tasks = files.Select(async file =>
            {
                try
                {
                    string content = await File.ReadAllTextAsync(file);
                    var matches = SymbolRegex.Matches(content);
                    var symbols = new List<SymbolInfo>();
                    
                    foreach (Match m in matches)
                    {
                        symbols.Add(new SymbolInfo 
                        { 
                            Name = m.Groups["name"].Value, 
                            FilePath = file,
                            Type = m.Value.Contains("class") ? "class" : "method"
                        });
                    }
                    return (Path: file, Symbols: symbols);
                }
                catch { return (Path: null, Symbols: null); }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var res in results)
            {
                if (res.Path != null) newIndex[res.Path] = res.Symbols;
            }

            _index = newIndex;
        }

        public string GetSummary()
        {
            if (!_index.Any()) return "Symbol index empty.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### SYMBOL SUMMARY");
            
            // Group by class to keep it readable
            var topSymbols = _index.SelectMany(kvp => kvp.Value)
                                   .OrderBy(s => s.Name)
                                   .Take(100);

            foreach (var s in topSymbols)
            {
                sb.AppendLine($"- {s.Name} ({s.Type}) in {Path.GetFileName(s.FilePath)}");
            }
            
            if (_index.SelectMany(kvp => kvp.Value).Count() > 100)
                sb.AppendLine("... [Truncated]");

            return sb.ToString();
        }

        public class SymbolInfo
        {
            public string Name { get; set; }
            public string FilePath { get; set; }
            public string Type { get; set; }
        }
    }
}
