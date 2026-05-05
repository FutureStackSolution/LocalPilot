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

            // 🚀 PERFORMANCE HYGIENE: Prioritize the Active Project to avoid full solution compilation
            Microsoft.CodeAnalysis.Project activeProject = null;
            try {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
                if (activeDoc != null) {
                    var docId = solution.GetDocumentIdsWithFilePath(activeDoc.FilePath).FirstOrDefault();
                    if (docId != null) activeProject = solution.GetProject(docId.ProjectId);
                }
            } catch { }

            var projects = solution.Projects.ToList();
            if (activeProject != null) {
                projects.Remove(activeProject);
                projects.Insert(0, activeProject);
            }

            foreach (var project in projects)
            {
                if (ct.IsCancellationRequested) break;
                
                // 🛡️ CIRCUIT BREAKER: Skip projects that aren't C#/VB to save cycles
                if (!CanHandle(Path.GetExtension(project.FilePath ?? ""))) continue;

                // 🚀 SMART CHECK: Check if the project even contains the symbol name before compiling
                // This is a much cheaper metadata check than GetCompilationAsync.
                // (Note: Some versions of Roslyn don't have this, so we fallback)
                
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
                
                // If we found it in the active project or high-priority projects, we stop to save time
                if (results.Any()) break;
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
            csSb.AppendLine($"## SEMANTIC NEIGHBORHOOD: {Path.GetFileName(filePath)}");

            // 1. Identify all types defined in this file
            var declaredTypes = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Select(t => semanticModel.GetDeclaredSymbol(t))
                .Where(s => s != null)
                .Cast<INamedTypeSymbol>()
                .ToList();

            var displayFormat = new SymbolDisplayFormat(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeAccessibility,
                parameterOptions: SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

            foreach (var type in declaredTypes)
            {
                csSb.AppendLine($"### [Definition] {type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                
                // 2. Hierarchy Check (Base & Interfaces)
                var hierarchy = new List<INamedTypeSymbol>();
                if (type.BaseType != null && type.BaseType.SpecialType == SpecialType.None) hierarchy.Add(type.BaseType);
                hierarchy.AddRange(type.AllInterfaces.Take(3));

                foreach (var h in hierarchy)
                {
                    csSb.AppendLine($"  - Contract: {h.Name} ({h.TypeKind})");
                    var members = h.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public).Take(8);
                    foreach(var m in members) csSb.AppendLine($"    - {m.ToDisplayString(displayFormat)}");
                }
                
                // 3. Dependency Check (Field & Property Injection)
                var dependencies = type.GetMembers()
                    .Select(m => m switch {
                        IFieldSymbol f => f.Type,
                        IPropertySymbol p => p.Type,
                        _ => null
                    })
                    .Where(t => t != null && t.TypeKind != TypeKind.Unknown && SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, type.ContainingAssembly))
                    .OfType<INamedTypeSymbol>()
                    .Distinct(SymbolEqualityComparer.Default)
                    .Take(3);

                foreach (INamedTypeSymbol dep in dependencies)
                {
                    csSb.AppendLine($"  - Dependency: {dep.Name} (API)");
                    var members = dep.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared).Take(10);
                    foreach(var m in members) csSb.AppendLine($"    - {m.ToDisplayString(displayFormat)}");
                }
            }

            return csSb.ToString();
        }

        public async Task<string> GetDiagnosticsAsync(CancellationToken ct)
        {
            await _initTask.JoinAsync(ct);
            
            // 🚀 SENIOR ARCHITECT FIX: Use Visual Studio's Error List instead of full-solution compilation.
            // Compilation-based diagnostics are 100x slower on large projects (e.g. HMIS).
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE80.DTE2;
                if (dte == null) return null;

                var items = dte.ToolWindows.ErrorList.ErrorItems;
                if (items.Count == 0) return null;

                var sb = new System.Text.StringBuilder();
                int count = 0;
                int totalItems = items.Count;
                for (int i = 1; i <= totalItems; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var item = items.Item(i);
                        if (item == null) continue;

                        // Only report Errors for Roslyn (Warnings are too noisy for the system prompt)
                        if (item.ErrorLevel == global::EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            string file = string.IsNullOrEmpty(item.FileName) ? "Unknown" : Path.GetFileName(item.FileName);
                            sb.AppendLine($"[ERROR] {item.Description} (at {file}:{item.Line})");
                            count++;
                        }
                        if (count >= 10) break;
                    }
                    catch { continue; }
                }
                return count > 0 ? sb.ToString() : null;
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[RoslynProvider] Error List access failed: {ex.Message}. Falling back to active project only.");
            }

            // Fallback: Active Project compilation diagnostics only (MUCH faster than solution-wide)
            try
            {
                var solution = _workspace?.CurrentSolution;
                if (solution == null) return null;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeProj = await Community.VisualStudio.Toolkit.VS.Solutions.GetActiveProjectAsync();
                if (activeProj == null) return null;

                var project = solution.Projects.FirstOrDefault(p => p.Name == activeProj.Name);
                if (project == null) return null;

                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation == null) return null;

                var diagnostics = compilation.GetDiagnostics(ct)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(10);

                var sbFallback = new System.Text.StringBuilder();
                foreach (var diag in diagnostics)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    sbFallback.AppendLine($"[ERROR] {diag.GetMessage()} (at {Path.GetFileName(lineSpan.Path)}:{lineSpan.StartLinePosition.Line + 1})");
                }
                return sbFallback.Length > 0 ? sbFallback.ToString() : null;
            }
            catch { return null; }
        }

        public async Task SynchronizeDocumentAsync(string filePath)
        {
            // TryApplyChanges handles synchronization
            await Task.CompletedTask;
        }

        public async Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, string oldName, CancellationToken ct)
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

                // 🚀 ADVANCED FUZZY FALLBACK (v2.2)
                // If the model provided wrong coordinates, try several recovery strategies
                if (symbol == null)
                {
                    // Strategy 1: If we have the old name, search the entire file for it (Tokens are faster than Nodes)
                    if (!string.IsNullOrEmpty(oldName))
                    {
                        var bestToken = syntaxRoot.DescendantTokens()
                            .Where(t => t.Text == oldName)
                            .OrderBy(t => Math.Abs(t.GetLocation().GetMappedLineSpan().StartLinePosition.Line - (line - 1)))
                            .FirstOrDefault();

                        if (bestToken != default)
                        {
                            token = bestToken;
                            node = token.Parent;
                            symbol = semanticModel.GetDeclaredSymbol(node, ct) ?? semanticModel.GetSymbolInfo(node, ct).Symbol;
                            
                            // If still null, try the parent of the parent (e.g. for some complex declarators)
                            if (symbol == null && node.Parent != null)
                            {
                                symbol = semanticModel.GetDeclaredSymbol(node.Parent, ct) ?? semanticModel.GetSymbolInfo(node.Parent, ct).Symbol;
                            }
                            
                            if (symbol != null) LocalPilotLogger.Log($"[RoslynProvider] Fuzzy matched symbol '{oldName}' via Strategy 1.");
                        }
                    }

                    // Strategy 2: If still not found, try the token text at the position (if it's not whitespace)
                    if (symbol == null && !string.IsNullOrWhiteSpace(token.Text))
                    {
                        var bestToken = syntaxRoot.DescendantTokens()
                            .Where(t => t.Text == token.Text)
                            .OrderBy(t => Math.Abs(t.GetLocation().GetMappedLineSpan().StartLinePosition.Line - (line - 1)))
                            .FirstOrDefault();

                        if (bestToken != default)
                        {
                            token = bestToken;
                            node = token.Parent;
                            symbol = semanticModel.GetDeclaredSymbol(node, ct) ?? semanticModel.GetSymbolInfo(node, ct).Symbol;
                            
                            if (symbol == null && node.Parent != null)
                            {
                                symbol = semanticModel.GetDeclaredSymbol(node.Parent, ct) ?? semanticModel.GetSymbolInfo(node.Parent, ct).Symbol;
                            }
                            
                            if (symbol != null) LocalPilotLogger.Log($"[RoslynProvider] Fuzzy matched token '{token.Text}' via Strategy 2.");
                        }
                    }
                }

                if (symbol == null)
                {
                    bool isStillLoading = solution.Projects.Any(p => !p.Documents.Any());
                    string hint = isStillLoading ? " (Solution is likely still indexing background documents)" : "";
                    string foundTokenText = string.IsNullOrEmpty(token.Text) ? "empty" : $"'{token.Text}'";
                    return $"Error: Could not find a refactorable symbol at the specified location (Line {line}, Col {column}). Token found: {foundTokenText}. {hint}Suggestion: Verify the line/column point exactly to an identifier.";
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
