using LocalPilot.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalPilot.Editor
{
    internal static class InlineDiffAdornmentLayer
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("LocalPilotInlineDiff")]
        [Order(After = PredefinedAdornmentLayers.Selection)]
        [TextViewRole(PredefinedTextViewRoles.Editable)]
        public static AdornmentLayerDefinition EditorAdornmentLayer = null;
    }

    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class InlineDiffAdornmentListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService TextDocumentFactory { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            TextDocumentFactory.TryGetTextDocument(textView.TextBuffer, out var doc);
            if (doc != null)
            {
                new InlineDiffAdornmentManager(textView, doc);
            }
        }
    }

    internal sealed class InlineDiffAdornmentManager
    {
        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private readonly IAdornmentLayer _layer;

        private static readonly Brush AddedBgBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x3F, 0xB9, 0x50));
        private static readonly Brush AddedBorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x3F, 0xB9, 0x50));
        private static readonly Brush RemovedBgBrush = new SolidColorBrush(Color.FromArgb(0x32, 0xF8, 0x51, 0x49));
        private static readonly Brush PillBgBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1E, 0x26));
        private static readonly Brush PillBorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x45, 0x47, 0x5A));

        public InlineDiffAdornmentManager(IWpfTextView view, ITextDocument document)
        {
            _view = view;
            _document = document;
            _layer = view.GetAdornmentLayer("LocalPilotInlineDiff");

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
            InlineDiffService.Instance.OnDiffsChanged += OnDiffsChanged;

            RenderHunks();
        }

        private void OnDiffsChanged(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(_document?.FilePath)) return;
            if (!string.Equals(filePath, _document.FilePath, StringComparison.OrdinalIgnoreCase)) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RenderHunks();
            });
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_layer.IsEmpty && InlineDiffService.Instance.GetHunks(_document?.FilePath).Count == 0)
                return;
            RenderHunks();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            InlineDiffService.Instance.OnDiffsChanged -= OnDiffsChanged;
        }

        private void RenderHunks()
        {
            _layer.RemoveAllAdornments();
            if (_view.IsClosed || string.IsNullOrEmpty(_document?.FilePath)) return;

            var hunks = InlineDiffService.Instance.GetHunks(_document.FilePath);
            if (hunks.Count == 0) return;

            var snapshot = _view.TextSnapshot;
            foreach (var hunk in hunks)
            {
                int startLineIdx = Math.Max(0, Math.Min(hunk.StartLine, snapshot.LineCount - 1));
                int endLineIdx = Math.Max(startLineIdx, Math.Min(Math.Max(startLineIdx, hunk.EndLineExclusive - 1), snapshot.LineCount - 1));

                var startSnapLine = snapshot.GetLineFromLineNumber(startLineIdx);
                var endSnapLine = snapshot.GetLineFromLineNumber(endLineIdx);
                var span = new SnapshotSpan(startSnapLine.Start, endSnapLine.End);

                var startViewLine = _view.GetTextViewLineContainingBufferPosition(startSnapLine.Start);
                var endViewLine = _view.GetTextViewLineContainingBufferPosition(endSnapLine.Start);
                if (startViewLine == null || endViewLine == null) continue;

                double top = startViewLine.Top;
                double height = Math.Max(startViewLine.Height, (endViewLine.Bottom - startViewLine.Top));
                double width = Math.Max(200, _view.ViewportWidth - 24);

                // Highlight added/modified lines
                if (hunk.AddedLineCount > 0)
                {
                    var highlight = new Border
                    {
                        Width = width,
                        Height = height,
                        Background = AddedBgBrush,
                        BorderBrush = AddedBorderBrush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(highlight, _view.ViewportLeft);
                    Canvas.SetTop(highlight, top);
                    _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, hunk.Id + "_bg", highlight, null);
                }

                // Floating Accept / Reject pill at top-right of hunk
                var pill = BuildActionPill(hunk);
                Canvas.SetLeft(pill, Math.Max(_view.ViewportLeft + 20, _view.ViewportRight - 190));
                Canvas.SetTop(pill, Math.Max(0, top - 2));
                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, hunk.Id + "_pill", pill, null);
            }
        }

        private UIElement BuildActionPill(DiffHunk hunk)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            if (hunk.RemovedLines.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"-{hunk.RemovedLines.Count}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),
                    Background = RemovedBgBrush,
                    FontSize = 10,
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var acceptBtn = new Button
            {
                Content = "✓ Keep",
                FontSize = 10,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            acceptBtn.Click += (s, e) =>
            {
                InlineDiffService.Instance.AcceptHunk(hunk.FilePath, hunk.Id);
            };

            var rejectBtn = new Button
            {
                Content = "✕ Undo",
                FontSize = 10,
                Padding = new Thickness(6, 2, 6, 2),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            rejectBtn.Click += (s, e) =>
            {
                _ = InlineDiffService.Instance.RejectHunkAsync(hunk.FilePath, hunk.Id);
            };

            panel.Children.Add(acceptBtn);
            panel.Children.Add(rejectBtn);

            return new Border
            {
                Background = PillBgBrush,
                BorderBrush = PillBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Child = panel
            };
        }
    }
}
