using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;

namespace LocalPilot.Completion
{
    /// <summary>
    /// Listens for text changes in the editor and triggers an inline
    /// completion request to Ollama after a configurable debounce delay.
    /// Ghost-text is displayed as an adornment (see GhostTextAdornment).
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class InlineCompletionController : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService TextDocumentFactory { get; set; }
        [Import] internal ICompletionBroker CompletionBroker { get; set; }

        // Shared OllamaService instance across all editor tabs to unify circuit breaker state
        private static readonly OllamaService _sharedOllama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);

        private IWpfTextView _view;
        private ITextDocument _document;
        private CompletionPromptBuilder _promptBuilder;
        private GhostTextAdornment _ghostAdornment;
        private CancellationTokenSource _cts;
        private Timer _debounceTimer;
        private readonly object _lock = new object();

        public void TextViewCreated(IWpfTextView textView)
        {
            if (!LocalPilotSettings.Instance.EnableInlineCompletion) return;

            _view = textView;
            _promptBuilder = new CompletionPromptBuilder(LocalPilotSettings.Instance);
            _ghostAdornment = new GhostTextAdornment(textView);

            TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out _document);

            textView.TextBuffer.Changed += OnTextBufferChanged;
            textView.Caret.PositionChanged += OnCaretPositionChanged;
            textView.Closed += OnViewClosed;
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (!LocalPilotSettings.Instance.EnableInlineCompletion) return;

            // Dismiss existing ghost text on any edit
            _ghostAdornment?.HideGhost();

            var mode = LocalPilotSettings.Instance.Mode;
            var delay = mode switch
            {
                PerformanceMode.Fast => 300,
                PerformanceMode.HighAccuracy => 1000,
                _ => 600
            };

            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    _ => { var t = TriggerCompletionSafeAsync(); },
                    null, delay, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Safely wraps the async completion call from timer callbacks
        /// to prevent unhandled exceptions from crashing the process.
        /// </summary>
        private async Task TriggerCompletionSafeAsync()
        {
            try { await TriggerCompletionAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LocalPilot] Timer callback error: {ex.Message}"); }
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (!LocalPilotSettings.Instance.EnableInlineCompletion) return;

            // Cancel any pending completion when user moves caret
            _cts?.Cancel();
            _ghostAdornment?.HideGhost();
        }

        private async Task TriggerCompletionAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                // 1. Get snapshot and buffer info on the UI thread
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                      .SwitchToMainThreadAsync(token);

                // 🛡️ INTELLI-FIRST: Don't show ghost text if VS IntelliSense is active
                if (CompletionBroker.IsCompletionActive(_view))
                {
                    return;
                }

                // Debug guard: silence completions during debugging sessions
                var shellDebugger = await Community.VisualStudio.Toolkit.VS.GetRequiredServiceAsync<Microsoft.VisualStudio.Shell.Interop.SVsShellDebugger, Microsoft.VisualStudio.Shell.Interop.IVsDebugger>();
                if (shellDebugger != null)
                {
                    Microsoft.VisualStudio.Shell.Interop.DBGMODE[] mode = new Microsoft.VisualStudio.Shell.Interop.DBGMODE[1];
                    shellDebugger.GetMode(mode);
                    if (mode[0] != Microsoft.VisualStudio.Shell.Interop.DBGMODE.DBGMODE_Design)
                    {
                        return;
                    }
                }

                var snapshot = _view.TextBuffer.CurrentSnapshot;
                var caretPos = _view.Caret.Position.BufferPosition.Position;
                var fileExt = System.IO.Path.GetExtension(_document?.FilePath ?? ".cs");
                var filePath = _document?.FilePath ?? "untitled";

                // 2. Offload network request and heavy text processing to background thread
                var completionText = await Task.Run(async () =>
                {
                    // Context window: 64 lines before, 16 after cursor
                    const int beforeLines = 64;
                    const int afterLines = 16;

                    int startPos = Math.Max(0, caretPos);
                    int linesFound = 0;
                    while (startPos > 0 && linesFound < beforeLines)
                    {
                        startPos--;
                        if (snapshot[startPos] == '\n') linesFound++;
                    }

                    int endPos = caretPos;
                    linesFound = 0;
                    while (endPos < snapshot.Length && linesFound < afterLines)
                    {
                        if (snapshot[endPos] == '\n') linesFound++;
                        endPos++;
                    }

                    var prefix = snapshot.GetText(startPos, caretPos - startPos);
                    var suffix = snapshot.GetText(caretPos, endPos - caretPos);

                    var prompt = _promptBuilder.Build(fileExt, prefix, suffix, filePath);
                    var perfMode = LocalPilotSettings.Instance.Mode;
                    var maxTokens = perfMode switch
                    {
                        PerformanceMode.Fast => 128,
                        PerformanceMode.HighAccuracy => 512,
                        _ => 256
                    };

                    var opts = new OllamaOptions
                    {
                        Temperature = perfMode == PerformanceMode.Fast ? 0.4 : (perfMode == PerformanceMode.HighAccuracy ? 0.1 : 0.2),
                        NumPredict = maxTokens,
                        Stop = new System.Collections.Generic.List<string> { "\n\n\n", "</MID>" }
                    };

                    _sharedOllama.UpdateBaseUrl(LocalPilotSettings.Instance.OllamaBaseUrl);

                    var sb = new StringBuilder(256);
                    await foreach (var chunk in _sharedOllama.StreamCompletionAsync(
                        LocalPilotSettings.Instance.CompletionModel, prompt, opts, token).ConfigureAwait(false))
                    {
                        sb.Append(chunk);
                        if (token.IsCancellationRequested) break;
                    }
                    return sb.ToString().Trim();
                }, token);

                if (!LocalPilotSettings.Instance.EnableInlineCompletion || token.IsCancellationRequested) return;

                // 3. Return to UI thread only to render
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                      .SwitchToMainThreadAsync(CancellationToken.None);

                if (!token.IsCancellationRequested && LocalPilotSettings.Instance.ShowCompletionGhost)
                    _ghostAdornment?.ShowGhost(completionText);
            }
            catch (OperationCanceledException) { /* user typed again — expected */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalPilot] Completion error: {ex.Message}");
            }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            _view.TextBuffer.Changed -= OnTextBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.Closed -= OnViewClosed;

            _cts?.Cancel();
            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }
    }
}
