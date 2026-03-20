using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using LocalPilot.Services;
using LocalPilot.Settings;

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

        private IWpfTextView  _view;
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

            _view          = textView;
            _ollama        = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            _promptBuilder = new CompletionPromptBuilder(LocalPilotSettings.Instance);
            _ghostAdornment = new GhostTextAdornment(textView);

            TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out _document);

            textView.TextBuffer.Changed     += OnTextBufferChanged;
            textView.Caret.PositionChanged  += OnCaretPositionChanged;
            textView.Closed                 += OnViewClosed;
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // Dismiss existing ghost text on any edit
            _ghostAdornment?.HideGhost();

            // Debounce the request
            var delay = LocalPilotSettings.Instance.CompletionDelayMs;
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
                string prefix, suffix;
                int caretPos;

                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                      .SwitchToMainThreadAsync(token);

                var snapshot    = _view.TextBuffer.CurrentSnapshot;
                caretPos        = _view.Caret.Position.BufferPosition.Position;
                prefix          = snapshot.GetText(0, caretPos);
                suffix          = snapshot.GetText(caretPos, snapshot.Length - caretPos);
                var fileExt     = System.IO.Path.GetExtension(_document?.FilePath ?? ".cs");
                var filePath    = _document?.FilePath ?? "untitled";

                // Build prompt
                var prompt = _promptBuilder.Build(fileExt, prefix, suffix, filePath);

                // Call Ollama
                var completionText = string.Empty;
                var opts = new OllamaOptions
                {
                    Temperature = LocalPilotSettings.Instance.Temperature,
                    NumPredict  = LocalPilotSettings.Instance.MaxCompletionTokens,
                    Stop        = new System.Collections.Generic.List<string> { "\n\n\n", "</MID>" }
                };

                await foreach (var chunk in _ollama.StreamCompletionAsync(
                    LocalPilotSettings.Instance.CompletionModel, prompt, opts, token))
                {
                    completionText += chunk;
                    if (token.IsCancellationRequested) return;
                }

                completionText = completionText.Trim();
                if (string.IsNullOrWhiteSpace(completionText)) return;

                // Show ghost text on UI thread
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                      .SwitchToMainThreadAsync(CancellationToken.None);

                if (!token.IsCancellationRequested)
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
            _view.TextBuffer.Changed    -= OnTextBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.Closed                -= OnViewClosed;

            _cts?.Cancel();
            _debounceTimer?.Dispose();
            _ollama?.Dispose();
        }
    }
}
