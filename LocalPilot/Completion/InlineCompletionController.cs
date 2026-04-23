using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

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

        private IWpfTextView _view;
        private ITextDocument _document;
        private OllamaService _ollama;
        private CompletionPromptBuilder _promptBuilder;
        private GhostTextAdornment _ghostAdornment;
        private CancellationTokenSource _cts;
        private Timer _debounceTimer;
        private readonly object _lock = new object();

        public void TextViewCreated(IWpfTextView textView)
        {
            if (!LocalPilotSettings.Instance.EnableInlineCompletion) return;

            _view = textView;
            _ollama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            _promptBuilder = new CompletionPromptBuilder(LocalPilotSettings.Instance);
            _ghostAdornment = new GhostTextAdornment(textView);

            TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out _document);

            textView.TextBuffer.Changed += OnTextBufferChanged;
            textView.Caret.PositionChanged += OnCaretPositionChanged;
            textView.Closed += OnViewClosed;
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // Dismiss existing ghost text on any edit
            _ghostAdornment?.HideGhost();

            // 🚀 AUTONOMOUS DEBOUNCE: Derived from Intelligence Mode
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
                    _ => _ = TriggerCompletionAsync(),
                    null, delay, Timeout.Infinite);
            }
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
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

                // 🚀 DEBUG GUARD: Silence completions during debugging sessions to prevent clashing with Watchdog/Copilot
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
                
                // 🚀 AUTONOMOUS CONTEXT: Using 'Golden Ratio' for optimized FIM completion
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
                var fileExt = System.IO.Path.GetExtension(_document?.FilePath ?? ".cs");
                var filePath = _document?.FilePath ?? "untitled";

                // 2. Offload network request and processing to a background thread
                var completionText = await Task.Run(async () =>
                {
                    var prompt = _promptBuilder.Build(fileExt, prefix, suffix, filePath);
                    var mode = LocalPilotSettings.Instance.Mode;
                    var maxTokens = mode switch
                    {
                        PerformanceMode.Fast => 128,
                        PerformanceMode.HighAccuracy => 512,
                        _ => 256
                    };

                    var opts = new OllamaOptions
                    {
                        Temperature = mode == PerformanceMode.Fast ? 0.4 : (mode == PerformanceMode.HighAccuracy ? 0.1 : 0.2),
                        NumPredict = maxTokens,
                        Stop = new System.Collections.Generic.List<string> { "\n\n\n", "</MID>" }
                    };

                    _ollama.UpdateBaseUrl(LocalPilotSettings.Instance.OllamaBaseUrl);
                    string result = string.Empty;
                    await foreach (var chunk in _ollama.StreamCompletionAsync(
                        LocalPilotSettings.Instance.CompletionModel, prompt, opts, token).ConfigureAwait(false))
                    {
                        result += chunk;
                        if (token.IsCancellationRequested) break;
                    }
                    return result.Trim();
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
            _debounceTimer?.Dispose();
        }


    }
}
