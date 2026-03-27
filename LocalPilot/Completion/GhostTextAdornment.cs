using Microsoft.VisualStudio.Text.Editor;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalPilot.Completion
{
    /// <summary>
    /// Renders the Copilot-style "ghost text" suggestion inline in the editor.
    /// The text is overlaid as a WPF TextBlock adornment at the caret position.
    /// The user accepts it with Tab, dismisses with Escape (handled by
    /// the completion keyboard command filter).
    /// </summary>
    public class GhostTextAdornment
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private TextBlock _ghostBlock;
        private string _pendingCompletion;

        public string PendingCompletion => _pendingCompletion;

        public GhostTextAdornment(IWpfTextView view)
        {
            _view  = view;
            _layer = view.GetAdornmentLayer("LocalPilotGhostText");
        }

        public void ShowGhost(string completionText)
        {
            _pendingCompletion = completionText;

            // Remove previous ghost
            _layer.RemoveAllAdornments();

            var caretPos  = _view.Caret.Position.BufferPosition;
            var caretLine = _view.GetTextViewLineContainingBufferPosition(caretPos);
            if (caretLine == null) return;

            // Measure caret X position
            double caretX = _view.Caret.Left;
            double caretY = caretLine.Top;

            // Show only the first line of completion as ghost
            string displayText = completionText.Contains("\n")
                ? completionText.Substring(0, completionText.IndexOf('\n'))
                : completionText;

            _ghostBlock = new TextBlock
            {
                Text              = displayText,
                Foreground        = new SolidColorBrush(Color.FromArgb(0x88, 0x9B, 0x8F, 0xFB)),
                FontFamily        = _view.FormattedLineSource?.DefaultTextProperties.Typeface.FontFamily
                                    ?? new FontFamily("Consolas"),
                FontSize          = _view.FormattedLineSource?.DefaultTextProperties.FontRenderingEmSize
                                    ?? 13,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible  = false
            };

            Canvas.SetLeft(_ghostBlock, caretX);
            Canvas.SetTop(_ghostBlock, caretY);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new Microsoft.VisualStudio.Text.SnapshotSpan(caretPos, 0),
                "ghost",
                _ghostBlock,
                null);
        }

        public void HideGhost()
        {
            _pendingCompletion = null;
            _layer.RemoveAllAdornments();
        }
    }
}
