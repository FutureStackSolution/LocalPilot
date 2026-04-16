using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

namespace LocalPilot.Services
{
    /// <summary>
    /// Professional LSP-backed symbol service.
    /// Replaces regex indexing with the native Roslyn (Microsoft.CodeAnalysis) engine.
    /// </summary>
    public class SymbolIndexService
    {
        private static readonly SymbolIndexService _instance = new SymbolIndexService();
        public static SymbolIndexService Instance => _instance;

        private VisualStudioWorkspace _workspace;
        private readonly object _lock = new object();

        private SymbolIndexService() 
        {
            InitializeWorkspaceSync();
        }

        private void InitializeWorkspaceSync()
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
                _workspace = componentModel.GetService<VisualStudioWorkspace>();
            });
        }

        public async Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName)
        {
            if (_workspace == null) return new List<SymbolLocation>();

            var solution = _workspace.CurrentSolution;
            var results = new List<SymbolLocation>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                // Find symbols with matching names
                var symbols = compilation.GetSymbolsWithName(s => s.Equals(symbolName, StringComparison.OrdinalIgnoreCase));

                foreach (var sym in symbols)
                {
                    foreach (var loc in sym.Locations)
                    {
                        if (loc.IsInSource)
                        {
                            var lineSpan = loc.GetLineSpan();
                            results.Add(new SymbolLocation
                            {
                                Name = sym.Name,
                                FilePath = lineSpan.Path,
                                Line = lineSpan.StartLinePosition.Line + 1,
                                Column = lineSpan.StartLinePosition.Character + 1,
                                Kind = sym.Kind.ToString()
                            });
                        }
                    }
                }
            }

            return results;
        }

        public string GetSummary()
        {
            // We no longer need to output 150 names; Roslyn handles it on demand.
            return "INTELLIGENCE: LSP (Roslyn) Active. The agent can pinpoint any symbol in the solution.";
        }

        public async Task<string> GetNeighborhoodContextAsync(string filePath)
        {
            if (_workspace == null) return null;

            // 🌐 MULTI-LANGUAGE SEMANTIC ENGINE
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".cs")
            {
                try
                {
                    string content = File.ReadAllText(filePath);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"## SEMANTIC CONTEXT: {Path.GetFileName(filePath)}");

                    // Directory Awareness (First 15 files)
                    var dir = Path.GetDirectoryName(filePath);
                    var files = Directory.GetFiles(dir).Select(Path.GetFileName).Take(15);
                    sb.AppendLine($"Folder [{dir}]: " + string.Join(", ", files));

                    switch (ext)
                    {
                        case ".ts":
                        case ".tsx":
                        case ".js":
                        case ".jsx":
                            // TS/React: Exports, Interfaces, Hooks, Selectors
                            var webMatches = System.Text.RegularExpressions.Regex.Matches(content, @"export\s+(?:class|interface|type|const|function|enum)\s+(?<name>[a-zA-Z_]\w*)");
                            foreach (System.Text.RegularExpressions.Match m in webMatches) sb.AppendLine($" - Symbol: {m.Groups["name"].Value}");
                            if (content.Contains("useEffect") || content.Contains("useState")) sb.AppendLine(" - Tech: React (Functional/Hooks)");
                            if (content.Contains("@Component")) sb.AppendLine(" - Tech: Angular/NestJS Decorator Found");
                            break;

                        case ".py":
                            // Python: Classes, Functions, Imports
                            var pyMatches = System.Text.RegularExpressions.Regex.Matches(content, @"(?:class|def)\s+(?<name>[a-zA-Z_]\w*)");
                            foreach (System.Text.RegularExpressions.Match m in pyMatches) sb.AppendLine($" - Python Symbol: {m.Groups["name"].Value}");
                            if (content.Contains("import ")) sb.AppendLine(" - Note: Contains external imports");
                            break;

                        case ".go":
                            // Go: Package, Func, Type, Struct
                            var goPackage = System.Text.RegularExpressions.Regex.Match(content, @"package\s+(?<name>\w+)");
                            if (goPackage.Success) sb.AppendLine($" - Go Package: {goPackage.Groups["name"].Value}");
                            var goMatches = System.Text.RegularExpressions.Regex.Matches(content, @"type\s+(?<name>\w+)\s+(?:struct|interface)");
                            foreach (System.Text.RegularExpressions.Match m in goMatches) sb.AppendLine($" - Go Type: {m.Groups["name"].Value}");
                            break;

                        case ".vue":
                        case ".svelte":
                            sb.AppendLine($" - Tech: Single File Component ({ext})");
                            if (content.Contains("<script")) sb.AppendLine(" - Contains Logic Block");
                            if (content.Contains("<template")) sb.AppendLine(" - Contains View Block");
                            break;

                        case ".json":
                            if (filePath.EndsWith("package.json")) sb.AppendLine(" - Type: Node.js Manifest (Check for scripts/dependencies)");
                            else if (filePath.EndsWith("tsconfig.json")) sb.AppendLine(" - Type: TypeScript Config");
                            break;
                    }

                    return sb.ToString();
                }
                catch { return null; }
            }

            // 🚀 C# SEMANTIC NEIGHBORHOOD (Roslyn)
            var solution = _workspace.CurrentSolution;
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            
            if (document == null) return null;

            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            
            var csSb = new System.Text.StringBuilder();
            csSb.AppendLine($"## SEMANTIC NEIGHBORHOOD: {Path.GetFileName(filePath)}");

            var classes = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();
            foreach (var cls in classes)
            {
                if (semanticModel.GetDeclaredSymbol(cls) is INamedTypeSymbol sym)
                {
                    csSb.AppendLine($" - Class: {sym.Name}");
                    if (sym.Interfaces.Length > 0)
                        csSb.AppendLine($"   Implements: {string.Join(", ", sym.Interfaces.Select(i => i.Name))}");
                }
            }

            return csSb.ToString();
        }
    }

    public class SymbolLocation
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Kind { get; set; }
    }
}
