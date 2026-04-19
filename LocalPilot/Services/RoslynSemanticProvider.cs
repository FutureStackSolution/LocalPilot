using LocalPilot.Models;
using Community.VisualStudio.Toolkit;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// Specialized semantic provider for C# and VB.NET using the native Roslyn engine.
    /// Priority: High Performance, Compiler-Grade Accuracy.
    /// </summary>
    public class RoslynSemanticProvider : ISemanticProvider
    {
        private VisualStudioWorkspace _workspace;
        private readonly Microsoft.VisualStudio.Threading.JoinableTask _initTask;

        public RoslynSemanticProvider()
        {
            // 🛡️ SENIOR ARCHITECT PATTERN: Fire-and-forget async init via JoinableTask.
            _initTask = ThreadHelper.JoinableTaskFactory.RunAsync(InitializeAsync);
        }

        private async Task InitializeAsync()
        {
            try
            {
                await   ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
                _workspace = componentModel.GetService<VisualStudioWorkspace>();
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[RoslynProvider] Async Init failed", ex);
            }
        }

        public bool CanHandle(string extension)
        {
            return extension == ".cs" || extension == ".vb";
        }

        public string GetSummary()
        {
            return "INTELLIGENCE: LSP (Roslyn) Active. Specialized for C# solution-wide refactoring and instant diagnostics.";
        }

        public async Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct)
        {
            await _initTask.JoinAsync(ct);
            if (_workspace == null) return new List<SymbolLocation>();

            var solution = _workspace.CurrentSolution;
            var results = new List<SymbolLocation>();

            foreach (var project in solution.Projects)
            {
                if (ct.IsCancellationRequested) break;
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation == null) continue;

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

        public async Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct)
        {
            await _initTask.JoinAsync(ct);
            if (_workspace == null) return null;

            var solution = _workspace.CurrentSolution;
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null) return null;

            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            
            var csSb = new System.Text.StringBuilder();
            csSb.AppendLine($"## PROACTIVE SEMANTIC CONTEXT: {Path.GetFileName(filePath)}");

            // 1. Identify all types defined in this file
            var declaredTypes = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Select(t => semanticModel.GetDeclaredSymbol(t))
                .Where(s => s != null)
                .Cast<INamedTypeSymbol>()
                .ToList();

            foreach (var type in declaredTypes)
            {
                csSb.AppendLine($"### Type: {type.Name} ({type.TypeKind})");
                if (type.BaseType != null && type.BaseType.SpecialType == SpecialType.None)
                    csSb.AppendLine($"  Inherits: {type.BaseType.Name}");
                
                // 2. Identify external types referenced (Fields, Properties, Parameters)
                var relatedTypes = type.GetMembers()
                    .Select(m => m switch {
                        IFieldSymbol f => f.Type,
                        IPropertySymbol p => p.Type,
                        IMethodSymbol meth => meth.ReturnType,
                        _ => null
                    })
                    .Where(t => t != null && t.TypeKind == TypeKind.Class && SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, type.ContainingAssembly))
                    .Cast<INamedTypeSymbol>()
                    .GroupBy(t => t.Name)
                    .Select(g => g.First())
                    .Take(5); // Limit to top 5 to save tokens

                foreach (var rel in relatedTypes)
                {
                    csSb.AppendLine($"  - Related: {rel.Name} (found in project)");
                    // Proactively grab signatures of the related type
                    var publicMembers = rel.GetMembers()
                        .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared)
                        .Select(m => m.Name)
                        .Take(10);
                    csSb.AppendLine($"    API: {string.Join(", ", publicMembers)}");
                }
            }

            return csSb.ToString();
        }

        public async Task<string> GetDiagnosticsAsync(CancellationToken ct)
        {
            await _initTask.JoinAsync(ct);
            if (_workspace == null) return null;

            var solution = _workspace.CurrentSolution;
            var sb = new System.Text.StringBuilder();
            int count = 0;

            foreach (var project in solution.Projects)
            {
                if (ct.IsCancellationRequested) break;
                if (!CanHandle(Path.GetExtension(project.FilePath ?? ""))) continue;

                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation == null) continue;

                var diagnostics = compilation.GetDiagnostics(ct)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(15); 

                foreach (var diag in diagnostics)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    string file = Path.GetFileName(lineSpan.Path);
                    sb.AppendLine($"[ERROR] {diag.GetMessage()} (at {file}:{lineSpan.StartLinePosition.Line + 1})");
                    count++;
                }
            }
            return count > 0 ? sb.ToString() : null;
        }

        public async Task SynchronizeDocumentAsync(string filePath)
        {
            // TryApplyChanges handles synchronization
            await Task.CompletedTask;
        }

        public async Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct)
        {
            await _initTask.JoinAsync(ct);
            if (_workspace == null) return "Error: Roslyn Workspace not initialized.";

            // 🛡️ UNDO SAFETY NET: Use IVsCompoundAction for atomic multi-file edits
            var textManager = (Microsoft.VisualStudio.TextManager.Interop.IVsTextManager)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager));
            textManager.GetActiveView(1, null, out var activeView);
            Microsoft.VisualStudio.TextManager.Interop.IVsCompoundAction compoundAction = activeView as Microsoft.VisualStudio.TextManager.Interop.IVsCompoundAction;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                compoundAction?.OpenCompoundAction("LocalPilot Refactor");
                await Community.VisualStudio.Toolkit.VS.StatusBar.ShowMessageAsync($"LocalPilot: Refactoring '{newName}'...");

                var solution = _workspace.CurrentSolution;
                var documentId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
                if (documentId == null) return $"Error: Document not found in workspace: {filePath}";

                var document = solution.GetDocument(documentId);
                var syntaxRoot = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                
                var position = text.Lines[Math.Max(0, line - 1)].Start + Math.Max(0, column - 1);
                var token = syntaxRoot.FindToken(position);
                var node = token.Parent;

                var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                var symbol = semanticModel.GetDeclaredSymbol(node, ct) ?? semanticModel.GetSymbolInfo(node, ct).Symbol;

                // 🚀 FUZZY FALLBACK: If the model provided a wrong line number, try to find the symbol by name in this file
                if (symbol == null)
                {
                    var root = await document.GetSyntaxRootAsync(ct);
                    // Search for the symbol name (we'd need the name, but usually we can infer it or try the token text)
                    var possibleNode = root.DescendantNodes().FirstOrDefault(n => n.GetText().ToString().Trim() == token.Text);
                    if (possibleNode != null)
                    {
                         symbol = semanticModel.GetDeclaredSymbol(possibleNode, ct) ?? semanticModel.GetSymbolInfo(possibleNode, ct).Symbol;
                    }
                }

                if (symbol == null)
                {
                    bool isStillLoading = solution.Projects.Any(p => !p.Documents.Any());
                    string hint = isStillLoading ? " (Solution is likely still indexing background documents)" : "";
                    return $"Error: Could not find a refactorable symbol at the specified location.{hint}";
                }

                // 🚀 MODERN RENAMER API: Using non-obsolete SymbolRenameOptions for deep semantic coverage
                var options = new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(
                    RenameOverloads: true, 
                    RenameInStrings: true, 
                    RenameInComments: true);

                var newSolution = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
                    solution, 
                    symbol, 
                    options, 
                    newName, 
                    ct).ConfigureAwait(false);
                
                bool success = false;
                int retries = 3;
                while (retries > 0)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_workspace.TryApplyChanges(newSolution))
                    {
                        success = true;
                        break;
                    }
                    retries--;
                    if (retries > 0) await Task.Delay(200 + new Random().Next(100)).ConfigureAwait(false);
                }

                if (success)
                {
                    await VS.StatusBar.ShowMessageAsync("LocalPilot: Refactoring success.");
                    return $"Successfully refactored '{symbol.Name}' to '{newName}' across solution.";
                }
                return "Error: Workspace is currently busy. Please try again.";
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.StatusBar.ShowMessageAsync("LocalPilot: Refactoring failed.");
                return $"Refactoring failed: {ex.Message}";
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                compoundAction?.CloseCompoundAction();
            }
        }
    }
}
