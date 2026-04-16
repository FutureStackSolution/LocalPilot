using System;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using LocalPilot.UI;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using System.IO;

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

        private AsyncPackage _package;

        private InlineChatOverlayManager() { }

        public void Initialize(AsyncPackage package)
        {
            _package = package;
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
                _ = ExecuteInlineActionAsync(overlay.Result, docView.FilePath);
            }
        }

        private async Task ExecuteInlineActionAsync(string prompt, string filePath)
        {
            // For Inline Chat, we want a high-accuracy model if available
            // This will use the same AgentOrchestrator but with a focused "Refactor" context.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            
            // We can't use the standard side-panel control easily here, 
            // so we'll trigger the Orchestrator directly.
            // Requirement: "This overlay should only suggest code changes, not broad chat."
            
            // In a future version, we'll implement a 'Staged Change' ghost view.
            // For now, let's trigger the orchestrator with an 'Inline' flag.
            
            // Dispatch to the main chat window so the user sees progress
            if (_package == null) return;

            var win = await _package.ShowToolWindowAsync(
                typeof(LocalPilot.Chat.LocalPilotChatWindow), 0, true, _package.DisposalToken)
                as LocalPilot.Chat.LocalPilotChatWindow;

            if (win?.Content is LocalPilot.Chat.LocalPilotChatControl ctrl)
            {
                ctrl.FireQuickAction("refactor", prompt);
            }
        }
    }
}
