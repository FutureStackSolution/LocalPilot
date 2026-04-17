using System;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using LocalPilot.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;

namespace LocalPilot.Services
{
    /// <summary>
    /// Manages the floating 'summons' overlay for inline refactoring.
    /// Calculates coordinates relative to the caret and orchestrates the AI turn.
    /// </summary>
    public class InlineChatOverlayManager
    {
        private static readonly InlineChatOverlayManager _instance = new InlineChatOverlayManager();
        public static InlineChatOverlayManager Instance => _instance;

        private InlineChatOverlayManager() { }

        public void Initialize(AsyncPackage package)
        {
            LocalPilotCommandRouter.Instance.Initialize(package);
        }

        public async Task ShowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextView == null) return;

            var textView = docView.TextView as IWpfTextView;
            if (textView == null) return;

            // Calculate screen coordinates for the caret
            var caretPoint = textView.Caret.Position.BufferPosition;
            var line = textView.GetTextViewLineContainingBufferPosition(caretPoint);
            var charBounds = line.GetCharacterBounds(caretPoint);
            
            // Adjust for DPI/Scaling if necessary, but PointToScreen usually handles it
            var pos = textView.VisualElement.PointToScreen(new Point(charBounds.Left, charBounds.Bottom + 10));

            var overlay = new InlineChatOverlay();
            overlay.Left = pos.X;
            overlay.Top = pos.Y;
            
            // Show as modal to block until input is received
            overlay.ShowDialog();

            if (!overlay.IsCancelled && !string.IsNullOrWhiteSpace(overlay.Result))
            {
                _ = ExecuteInlineActionAsync(overlay.Result);
            }
        }

        private async Task ExecuteInlineActionAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            string normalized = prompt.Trim();
            if (normalized.StartsWith("/"))
            {
                // Phase C consistency: inline entry can use the same slash actions as chat.
                var slashToken = normalized.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries)[0]
                    .Trim()
                    .ToLowerInvariant();

                if (CapabilityCatalog.TryResolveActionFromSlash(slashToken, out var action))
                {
                    await LocalPilotCommandRouter.Instance.ExecuteQuickActionAsync(action);
                    return;
                }
            }

            // Backward-compatible default behavior for free-form inline prompt.
            await LocalPilotCommandRouter.Instance.ExecuteQuickActionAsync("refactor", prompt);
        }
    }
}
