using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using LocalPilot.Services;
using LocalPilot.Settings;

namespace LocalPilot.Chat
{
    public partial class LocalPilotChatControl : UserControl
    {
        private readonly OllamaService _ollama;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private CancellationTokenSource _cts;
        private TextBlock _streamingBlock;    // current AI message being streamed

        // Fixed accent/code colours — look fine on both light and dark themes
        private static readonly SolidColorBrush BrushAccent = new SolidColorBrush(Color.FromRgb(0x7C, 0x6A, 0xF7));
        private static readonly SolidColorBrush BrushCode   = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
        private static readonly FontFamily ConsoleFont = new FontFamily("Consolas");
        private static readonly FontFamily UIFont      = new FontFamily("Segoe UI");

        // VS theme-aware colours (resolved at runtime)
        private static Brush ThemeWindowBg   => GetVsBrush(VsBrushes.ToolWindowBackgroundKey);
        private static Brush ThemeWindowFg   => GetVsBrush(VsBrushes.ToolWindowTextKey);
        private static Brush ThemeSurface    => GetVsBrush(VsBrushes.ButtonFaceKey);
        private static Brush ThemeBorder     => GetVsBrush(VsBrushes.ActiveBorderKey);

        private static Brush GetVsBrush(object key)
        {
            try
            {
                var brush = Application.Current?.Resources[key];
                return brush as Brush ?? SystemColors.WindowBrush;
            }
            catch { return SystemColors.WindowBrush; }
        }

        public LocalPilotChatControl()
        {
            InitializeComponent();
            _ollama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await PopulateModelsAsync();
            ShowWelcomeMessage();
        }

        // ── Model population ──────────────────────────────────────────────────
        private async Task PopulateModelsAsync()
        {
            var models = await _ollama.GetAvailableModelsAsync();
            CmbModel.Items.Clear();

            if (models.Count == 0)
            {
                CmbModel.Items.Add(new ComboBoxItem
                {
                    Content    = "No models found",
                    Foreground = SystemColors.GrayTextBrush
                });
                CmbModel.SelectedIndex = 0;
                return;
            }

            string preferred = LocalPilotSettings.Instance.ChatModel;
            int selectIdx = 0;

            for (int i = 0; i < models.Count; i++)
            {
                CmbModel.Items.Add(new ComboBoxItem { Content = models[i] });
                if (models[i].Equals(preferred, StringComparison.OrdinalIgnoreCase))
                    selectIdx = i;
            }

            CmbModel.SelectedIndex = selectIdx;
        }

        // ── Welcome message ───────────────────────────────────────────────────
        private void ShowWelcomeMessage()
        {
            _history.Add(new ChatMessage
            {
                Role    = "system",
                Content = "You are LocalPilot, an expert AI coding assistant. " +
                          "Help the user write, explain, refactor, document and debug code. " +
                          "Be concise, precise, and always use proper code formatting."
            });

            AppendAIBubble(
                "👋 Hi! I'm **LocalPilot**, your local AI coding assistant powered by Ollama.\n\n" +
                "I can help you:\n" +
                "• **Explain** selected code\n" +
                "• **Refactor** for better quality\n" +
                "• **Generate** documentation\n" +
                "• **Review** for bugs and improvements\n" +
                "• **Fix** errors and issues\n" +
                "• **Write** unit tests\n\n" +
                "Select code in the editor and use the quick action chips above, or just ask me anything!",
                isStreaming: false);
        }

        // ── Send message ──────────────────────────────────────────────────────
        private async void BtnSend_Click(object sender, RoutedEventArgs e)
            => await SendMessageAsync(TxtInput.Text.Trim());

        private async void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                await SendMessageAsync(TxtInput.Text.Trim());
            }
        }

        private async Task SendMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            TxtInput.Clear();
            AppendUserBubble(text);

            _history.Add(new ChatMessage { Role = "user", Content = text });

            await StreamResponseAsync();
        }

        // ── Quick action chips ────────────────────────────────────────────────
        private async void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var action = btn.Tag?.ToString() ?? string.Empty;

            // Try to get editor selection
            string selectedCode = TryGetEditorSelection();

            string prompt = BuildActionPrompt(action, selectedCode);
            if (string.IsNullOrEmpty(prompt)) return;

            AppendUserBubble($"/{action}" + (string.IsNullOrEmpty(selectedCode)
                                              ? string.Empty
                                              : $"\n```\n{selectedCode}\n```"));

            _history.Add(new ChatMessage { Role = "user", Content = prompt });
            await StreamResponseAsync();
        }

        private string BuildActionPrompt(string action, string code)
        {
            bool hasCode = !string.IsNullOrWhiteSpace(code);
            string codeBlock = hasCode ? $"\n\n```\n{code}\n```" : "(no code selected)";

            return action switch
            {
                "explain"  => $"Explain the following code clearly and concisely:{codeBlock}",
                "refactor" => $"Refactor the following code to improve readability, performance and best practices. Show the improved version:{codeBlock}",
                "document" => $"Generate complete XML documentation comments for the following code:{codeBlock}",
                "review"   => $"Review the following code for bugs, security issues, and improvements:{codeBlock}",
                "fix"      => $"Identify and fix all issues in the following code:{codeBlock}",
                "test"     => $"Write comprehensive unit tests using xUnit for the following code:{codeBlock}",
                _          => string.Empty
            };
        }

        private string TryGetEditorSelection()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                              typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.ActiveDocument == null) return string.Empty;

                var sel = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                return sel?.Text ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        // ── Streaming response ────────────────────────────────────────────────
        private async Task StreamResponseAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            string model = (CmbModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                           ?? LocalPilotSettings.Instance.ChatModel;

            SetStreaming(true);

            // Create an AI bubble that will be updated live
            _streamingBlock = AppendAIBubble(string.Empty, isStreaming: true);

            var sb = new StringBuilder();

            try
            {
                await foreach (var chunk in _ollama.StreamChatAsync(model, _history, null, token))
                {
                    sb.Append(chunk);

                    // Update UI on UI thread
                    Dispatcher.Invoke(() =>
                    {
                        if (_streamingBlock != null)
                            RenderMarkdown(_streamingBlock, sb.ToString());

                        // Auto-scroll
                        ChatScroll.ScrollToBottom();
                    });
                }

                // Done — add to history
                var reply = sb.ToString();
                _history.Add(new ChatMessage { Role = "assistant", Content = reply });

                // Trim history if too long
                TrimHistory();
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_streamingBlock != null && string.IsNullOrEmpty(sb.ToString()))
                        RenderMarkdown(_streamingBlock, "_[Stopped]_");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    AppendAIBubble($"❌ Error: {ex.Message}", isStreaming: false));
            }
            finally
            {
                Dispatcher.Invoke(() => SetStreaming(false));
                _streamingBlock = null;
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void AppendUserBubble(string text)
        {
            var border = new Border
            {
                Background      = ThemeSurface,
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x80, 0x7C, 0x6A, 0xF7)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10, 10, 3, 10),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(40, 4, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth        = 340
            };

            var container = new StackPanel { Orientation = Orientation.Vertical };

            var label = new TextBlock
            {
                Text       = "You",
                Foreground = BrushAccent,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 3)
            };

            var body = new TextBlock
            {
                Foreground   = ThemeWindowFg,
                FontFamily   = UIFont,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                Text         = text
            };

            container.Children.Add(label);
            container.Children.Add(body);
            border.Child = container;
            MessagesContainer.Children.Add(border);

            ChatScroll.ScrollToBottom();
        }

        private TextBlock AppendAIBubble(string text, bool isStreaming)
        {
            var border = new Border
            {
                Background   = ThemeWindowBg,
                BorderBrush  = ThemeBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10, 10, 10, 3),
                Padding      = new Thickness(12, 8, 12, 8),
                Margin       = new Thickness(4, 4, 40, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var container = new StackPanel { Orientation = Orientation.Vertical };

            var label = new TextBlock
            {
                Text       = "⚡ LocalPilot",
                Foreground = BrushAccent,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 3)
            };

            var body = new TextBlock
            {
                Foreground   = ThemeWindowFg,
                FontFamily   = UIFont,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap
            };

            if (!string.IsNullOrEmpty(text))
                RenderMarkdown(body, text);

            container.Children.Add(label);
            container.Children.Add(body);
            border.Child = container;
            MessagesContainer.Children.Add(border);

            ChatScroll.ScrollToBottom();
            return body;
        }

        /// <summary>
        /// Very simple markdown renderer — handles bold, code blocks and plain text.
        /// For production, replace with a proper markdown library.
        /// </summary>
        private void RenderMarkdown(TextBlock tb, string md)
        {
            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(md)) return;

            // Split on code fences
            var parts = md.Split(new[] { "```" }, StringSplitOptions.None);

            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 1)
                {
                    // Code block — strip language identifier on first line
                    var code = parts[i];
                    var nl   = code.IndexOf('\n');
                    if (nl >= 0) code = code.Substring(nl + 1);

                    tb.Inlines.Add(new Run(code.TrimEnd())
                    {
                        FontFamily = ConsoleFont,
                        FontSize   = 12,
                        Foreground = BrushCode
                    });
                }
                else
                {
                    // Plain text with **bold** support
                    var segments = parts[i].Split(new[] { "**" }, StringSplitOptions.None);
                    for (int j = 0; j < segments.Length; j++)
                    {
                        var run = new Run(segments[j])
                        {
                            Foreground = ThemeWindowFg
                        };
                        if (j % 2 == 1) run.FontWeight = FontWeights.Bold;
                        tb.Inlines.Add(run);
                    }
                }
            }
        }

        private void SetRichText(RichTextBox rtb, string text)
        {
            rtb.Document.Blocks.Clear();
            var para = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
            rtb.Document.Blocks.Add(para);
        }

        // Remove unused method — user bubbles now use TextBlock directly

        private void SetStreaming(bool streaming)
        {
            StreamingBar.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
            BtnSend.IsEnabled       = !streaming;
        }

        private void TrimHistory()
        {
            int max = LocalPilotSettings.Instance.ChatHistoryMaxItems * 2; // user+ai pairs
            while (_history.Count > max + 1) // keep system message
                _history.RemoveAt(1);
        }

        // ── Toolbar events ────────────────────────────────────────────────────
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            MessagesContainer.Children.Clear();
            _history.Clear();
            ShowWelcomeMessage();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        // ── Public API (called by command handlers) ────────────────────────
        public void FireQuickAction(string action)
        {
            // Must run on the UI thread — this may be called from JoinableTaskFactory
            Dispatcher.Invoke(() =>
            {
                var btn = new Button { Tag = action };
                QuickAction_Click(btn, new RoutedEventArgs());
            });
        }
    }
}
