using Community.VisualStudio.Toolkit;
using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LocalPilot.Editor
{
    /// <summary>
    /// MEF-exported provider for native Visual Studio Lightbulb (Ctrl+.) actions:
    /// "Fix with LocalPilot", "Refactor with LocalPilot", and "Generate Unit Tests".
    /// Designed to be strictly non-blocking on the editor/UI thread.
    /// </summary>
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("LocalPilot Lightbulb Actions")]
    [ContentType("code")]
    internal sealed class LocalPilotSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        [Import] internal IViewTagAggregatorFactoryService TagAggregatorFactory { get; set; }
        [Import] internal ITextDocumentFactoryService TextDocumentFactory { get; set; }

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (textView == null || textBuffer == null) return null;
            return new LocalPilotSuggestedActionsSource(this, textView, textBuffer);
        }
    }

    internal sealed class LocalPilotSuggestedActionsSource : ISuggestedActionsSource
    {
        private readonly LocalPilotSuggestedActionsSourceProvider _provider;
        private readonly ITextView _textView;
        private readonly ITextBuffer _textBuffer;
        private readonly ITagAggregator<IErrorTag> _errorAggregator;
        private bool _disposed;

        public event EventHandler<EventArgs> SuggestedActionsChanged { add { } remove { } }

        public LocalPilotSuggestedActionsSource(
            LocalPilotSuggestedActionsSourceProvider provider,
            ITextView textView,
            ITextBuffer textBuffer)
        {
            _provider = provider;
            _textView = textView;
            _textBuffer = textBuffer;
            try
            {
                _errorAggregator = provider.TagAggregatorFactory?.CreateTagAggregator<IErrorTag>(textView);
            }
            catch { }
        }

        public Task<bool> HasSuggestedActionsAsync(
            ISuggestedActionCategorySet requestedActionCategories,
            SnapshotSpan range,
            CancellationToken cancellationToken)
        {
            if (_disposed || range.Snapshot == null) return Task.FromResult(false);

            try
            {
                var lineSpan = ExpandToLineSpan(range);
                if (HasErrorTag(lineSpan)) return Task.FromResult(true);

                // Thread-safe SnapshotSpan check (avoid UI-affined _textView.Selection on background thread)
                if (range.Length > 0)
                    return Task.FromResult(true);

                string lineText = lineSpan.GetText();
                if (!string.IsNullOrWhiteSpace(lineText) && lineText.Trim().Length > 4)
                    return Task.FromResult(true);
            }
            catch { }

            return Task.FromResult(false);
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(
            ISuggestedActionCategorySet requestedActionCategories,
            SnapshotSpan range,
            CancellationToken cancellationToken)
        {
            if (_disposed || range.Snapshot == null) return Enumerable.Empty<SuggestedActionSet>();

            try
            {
                var actions = new List<ISuggestedAction>();
                var lineSpan = ExpandToLineSpan(range);
                string errorSummary = GetErrorSummary(lineSpan);

                if (!string.IsNullOrEmpty(errorSummary))
                {
                    actions.Add(new LocalPilotLightbulbAction(
                        _provider, _textView, _textBuffer, range,
                        LocalPilotActionKind.Fix, errorSummary));
                }

                bool hasSelection = _textView?.Selection != null && !_textView.Selection.IsEmpty;
                string lineText = lineSpan.GetText();
                if (hasSelection || (!string.IsNullOrWhiteSpace(lineText) && lineText.Trim().Length > 4))
                {
                    actions.Add(new LocalPilotLightbulbAction(
                        _provider, _textView, _textBuffer, range,
                        LocalPilotActionKind.Refactor, null));

                    actions.Add(new LocalPilotLightbulbAction(
                        _provider, _textView, _textBuffer, range,
                        LocalPilotActionKind.GenerateTests, null));
                }

                if (actions.Count == 0) return Enumerable.Empty<SuggestedActionSet>();

                string category = !string.IsNullOrEmpty(errorSummary)
                    ? PredefinedSuggestedActionCategoryNames.CodeFix
                    : PredefinedSuggestedActionCategoryNames.Refactoring;

                return new[]
                {
                    new SuggestedActionSet(
                        category,
                        actions,
                        title: "LocalPilot",
                        priority: SuggestedActionSetPriority.Low,
                        applicableToSpan: lineSpan)
                };
            }
            catch
            {
                return Enumerable.Empty<SuggestedActionSet>();
            }
        }

        private bool HasErrorTag(SnapshotSpan lineSpan)
        {
            if (_errorAggregator == null) return false;
            return _errorAggregator.GetTags(lineSpan).Any();
        }

        private string GetErrorSummary(SnapshotSpan lineSpan)
        {
            if (_errorAggregator == null) return null;
            var messages = new List<string>();
            foreach (var tagSpan in _errorAggregator.GetTags(lineSpan))
            {
                string tip = tagSpan.Tag?.ToolTipContent?.ToString();
                if (!string.IsNullOrWhiteSpace(tip) && !messages.Contains(tip))
                {
                    messages.Add(tip.Trim());
                }
            }
            return messages.Count > 0 ? string.Join("; ", messages) : null;
        }

        private static SnapshotSpan ExpandToLineSpan(SnapshotSpan range)
        {
            var startLine = range.Start.GetContainingLine();
            var endLine = range.End.GetContainingLine();
            return new SnapshotSpan(startLine.Start, endLine.End);
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _errorAggregator?.Dispose(); } catch { }
        }
    }

    internal enum LocalPilotActionKind
    {
        Fix,
        Refactor,
        GenerateTests
    }

    internal sealed class LocalPilotLightbulbAction : ISuggestedAction
    {
        private readonly LocalPilotSuggestedActionsSourceProvider _provider;
        private readonly ITextView _textView;
        private readonly ITextBuffer _textBuffer;
        private readonly SnapshotSpan _range;
        private readonly LocalPilotActionKind _kind;
        private readonly string _errorMessage;

        private static CancellationTokenSource _activeActionCts;
        private static readonly object _actionLock = new object();

        public LocalPilotLightbulbAction(
            LocalPilotSuggestedActionsSourceProvider provider,
            ITextView textView,
            ITextBuffer textBuffer,
            SnapshotSpan range,
            LocalPilotActionKind kind,
            string errorMessage)
        {
            _provider = provider;
            _textView = textView;
            _textBuffer = textBuffer;
            _range = range;
            _kind = kind;
            _errorMessage = errorMessage;
        }

        public string DisplayText => _kind switch
        {
            LocalPilotActionKind.Fix => "Fix with LocalPilot",
            LocalPilotActionKind.Refactor => "Refactor with LocalPilot",
            LocalPilotActionKind.GenerateTests => "Generate Unit Tests with LocalPilot",
            _ => "LocalPilot Action"
        };

        public bool HasActionSets => false;
        public string IconAutomationText => DisplayText;
        public ImageMoniker IconMoniker => KnownMonikers.StatusInformation;
        public string InputGestureText => null;
        public bool HasPreview => false;

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<SuggestedActionSet>>(null);

        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>(null);

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Invoke(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var snapshot = _textBuffer.CurrentSnapshot;
            string originalFullText = snapshot.GetText();
            _provider.TextDocumentFactory.TryGetTextDocument(_textBuffer, out var doc);
            string filePath = doc?.FilePath ?? "untitled.cs";

            // Determine target span: explicit selection > enclosing C# method > current line
            SnapshotSpan targetSpan = ResolveTargetSpan(snapshot, originalFullText, filePath);
            string targetCode = targetSpan.GetText();
            if (string.IsNullOrWhiteSpace(targetCode)) return;

            int spanStart = targetSpan.Start.Position;
            int spanLength = targetSpan.Length;
            int versionNumber = snapshot.Version.VersionNumber;

            CancellationTokenSource cts;
            lock (_actionLock)
            {
                try { _activeActionCts?.Cancel(); _activeActionCts?.Dispose(); } catch { }
                _activeActionCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                cts = _activeActionCts;
            }

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ExecuteInBackgroundAsync(
                    filePath, originalFullText, targetCode, spanStart, spanLength, versionNumber, cts.Token);
            });
        }

        private async Task ExecuteInBackgroundAsync(
            string filePath,
            string originalFullText,
            string targetCode,
            int spanStart,
            int spanLength,
            int versionNumber,
            CancellationToken ct)
        {
            try
            {
                await VS.StatusBar.ShowMessageAsync($"LocalPilot: Running '{DisplayText}'...");

                var settings = LocalPilotSettings.Instance;
                string model = _kind == LocalPilotActionKind.Refactor && !string.IsNullOrWhiteSpace(settings.RefactorModel)
                    ? settings.RefactorModel
                    : settings.ChatModel;

                if (string.IsNullOrWhiteSpace(model))
                {
                    await VS.StatusBar.ShowMessageAsync("LocalPilot: No ChatModel configured in Options.");
                    return;
                }

                var llm = LlmProviderFactory.GetService();
                var messages = BuildPromptMessages(filePath, targetCode);
                var options = new OllamaOptions
                {
                    Temperature = 0.1,
                    NumPredict = 1024,
                    RequestTimeoutSeconds = 120
                };

                string rawResponse = await llm.ChatAsync(model, messages, options, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(rawResponse)) return;

                string replacement = StripCodeFences(rawResponse);
                if (string.IsNullOrWhiteSpace(replacement)) return;

                // Validate in-memory before touching the editor buffer
                if (_kind != LocalPilotActionKind.GenerateTests &&
                    filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string candidateFull = originalFullText.Remove(spanStart, spanLength).Insert(spanStart, replacement);
                    string syntaxError = ToolRegistry.ValidateCSharpSyntax(originalFullText, candidateFull);
                    if (syntaxError != null)
                    {
                        await VS.StatusBar.ShowMessageAsync($"LocalPilot aborted edit (syntax error: {syntaxError})");
                        return;
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

                var currentSnapshot = _textBuffer.CurrentSnapshot;
                if (currentSnapshot.Version.VersionNumber != versionNumber)
                {
                    await VS.StatusBar.ShowMessageAsync("LocalPilot: Buffer changed while generating; edit skipped.");
                    return;
                }

                string updatedFullText;
                if (_kind == LocalPilotActionKind.GenerateTests)
                {
                    // Insert unit test skeleton cleanly right after the target method/selection
                    int insertPos = Math.Min(currentSnapshot.Length, spanStart + spanLength);
                    string insertBlock = Environment.NewLine + Environment.NewLine + replacement;
                    using (var edit = _textBuffer.CreateEdit())
                    {
                        edit.Insert(insertPos, insertBlock);
                        edit.Apply();
                    }
                    updatedFullText = _textBuffer.CurrentSnapshot.GetText();
                }
                else
                {
                    using (var edit = _textBuffer.CreateEdit())
                    {
                        edit.Replace(spanStart, spanLength, replacement);
                        edit.Apply();
                    }
                    updatedFullText = _textBuffer.CurrentSnapshot.GetText();
                }

                var roslyn = SymbolIndexService.Instance.GetRoslynProvider();
                if (roslyn != null) await roslyn.SynchronizeDocumentAsync(filePath);

                // Show inline Keep / Undo diff pill right in the editor
                InlineDiffService.Instance.SetFileDiff(filePath, originalFullText, updatedFullText);
                await VS.StatusBar.ShowMessageAsync($"LocalPilot: '{DisplayText}' applied (use Keep/Undo in editor).");
            }
            catch (OperationCanceledException)
            {
                await VS.StatusBar.ShowMessageAsync("LocalPilot: Lightbulb action cancelled.");
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Lightbulb] {DisplayText} failed", ex);
                await VS.StatusBar.ShowMessageAsync($"LocalPilot error: {ex.Message}");
            }
        }

        private List<ChatMessage> BuildPromptMessages(string filePath, string targetCode)
        {
            string sys = "You are LocalPilot, an elite code editor. Return ONLY the replacement code with no markdown fences, no explanations, and exact indentation.";
            string userPrompt = _kind switch
            {
                LocalPilotActionKind.Fix =>
                    $"Fix the following error in `{Path.GetFileName(filePath)}`:\nError: {_errorMessage}\n\nCode:\n{targetCode}",
                LocalPilotActionKind.Refactor =>
                    $"Refactor this code in `{Path.GetFileName(filePath)}` to be cleaner, safer, and more idiomatic while preserving behavior:\n\n{targetCode}",
                LocalPilotActionKind.GenerateTests =>
                    $"Generate clean unit test methods (xUnit/MSTest style matching C# conventions) for this code in `{Path.GetFileName(filePath)}`. Return ONLY the test method(s) code:\n\n{targetCode}",
                _ => targetCode
            };

            return new List<ChatMessage>
            {
                new ChatMessage { Role = "system", Content = sys },
                new ChatMessage { Role = "user", Content = userPrompt }
            };
        }

        private SnapshotSpan ResolveTargetSpan(ITextSnapshot snapshot, string fullText, string filePath)
        {
            if (_textView?.Selection != null && !_textView.Selection.IsEmpty)
            {
                return _textView.Selection.StreamSelectionSpan.SnapshotSpan;
            }

            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var tree = CSharpSyntaxTree.ParseText(fullText);
                    var root = tree.GetRoot();
                    var token = root.FindToken(_range.Start.Position);
                    var method = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (method != null)
                    {
                        var span = method.Span;
                        if (span.Start >= 0 && span.End <= snapshot.Length)
                        {
                            return new SnapshotSpan(snapshot, span.Start, span.Length);
                        }
                    }
                }
                catch { }
            }

            var startLine = _range.Start.GetContainingLine();
            var endLine = _range.End.GetContainingLine();
            return new SnapshotSpan(startLine.Start, endLine.End);
        }

        private static readonly Regex FenceStartRegex = new Regex(@"^```[a-zA-Z0-9#+-]*\s*\r?\n", RegexOptions.Compiled);
        private static readonly Regex FenceEndRegex = new Regex(@"\r?\n```\s*$", RegexOptions.Compiled);

        private static string StripCodeFences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            string trimmed = text.Trim();
            trimmed = FenceStartRegex.Replace(trimmed, "");
            trimmed = FenceEndRegex.Replace(trimmed, "");
            return trimmed.TrimEnd();
        }

        public void Dispose() { }
    }
}
