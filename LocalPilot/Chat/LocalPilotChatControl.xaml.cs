using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
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

namespace LocalPilot.Chat
{
    public partial class LocalPilotChatControl : UserControl
    {
        private readonly OllamaService _ollama;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _streamSemaphore = new SemaphoreSlim(1, 1);
        private RichTextBox _streamingBlock;    // current AI message being streamed

        // VS theme-aware colours (Dynamically linked to VS Theme)
        private Brush ThemeWindowBg => (Brush)this.Resources["LpWindowBgBrush"];
        private Brush ThemeWindowFg => (Brush)this.Resources["LpWindowFgBrush"];
        private Brush ThemeSurface  => (Brush)this.Resources["LpMenuBgBrush"];
        private Brush ThemeBorder   => (Brush)this.Resources["LpMenuBorderBrush"];

        // Fixed accent/code colours — look fine on both light and dark themes
        private static readonly SolidColorBrush BrushAccent = new SolidColorBrush(Color.FromRgb(0x7C, 0x6A, 0xF7));
        private static readonly SolidColorBrush BrushCode   = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
        private static readonly FontFamily ConsoleFont = new FontFamily("Consolas");
        private static readonly FontFamily UIFont      = new FontFamily("Segoe UI");

        public LocalPilotChatControl()
        {
            InitializeComponent();
            _ollama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            UpdateBrushes(); // Initialize brushes immediately for first-time command awareness
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateBrushes();
            ShowWelcomeMessage();
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private void OnThemeChanged(ThemeChangedEventArgs e) => UpdateBrushes();

        private void UpdateBrushes()
        {
            try
            {
                // Dynamic VS Theme linkage
                SetResourceBrush("LpWindowBgBrush",   VsBrushes.ToolWindowBackgroundKey,  Brushes.White);
                SetResourceBrush("LpWindowFgBrush",   VsBrushes.ToolWindowTextKey,        Brushes.Black);
                SetResourceBrush("LpMenuBgBrush",     VsBrushes.ToolWindowBackgroundKey,  Brushes.White);
                SetResourceBrush("LpMenuBorderBrush", VsBrushes.ToolWindowBorderKey,      Brushes.Gray);
                SetResourceBrush("LpMutedFgBrush",    VsBrushes.GrayTextKey,              Brushes.DarkGray);
                
                ChatScroll.Background = (Brush)this.Resources["LpWindowBgBrush"];
            }
            catch { /* Fallback to XAML defaults if shell is busy */ }
        }

        private void SetResourceBrush(string key, object vsKey, Brush fallback)
        {
            var brush = Application.Current.FindResource(vsKey) as Brush;
            if (brush != null)
            {
                if (brush.CanFreeze) brush.Freeze(); 
                this.Resources[key] = brush;
            }
            else if (fallback != null)
            {
                this.Resources[key] = fallback;
            }
        }

        // ── Welcome message ───────────────────────────────────────────────────
        private void ShowWelcomeMessage()
        {
            _history.Clear();
            _history.Add(new ChatMessage
            {
                Role    = "system",
                Content = "You are LocalPilot, an expert AI coding assistant. " +
                          "Help the user write, explain, refactor, document and debug code. " +
                          "Be concise, precise, and always use proper code formatting."
            });

            // Modern introductory text is now partly in XAML, but we can add a greet
            AppendAIBubble("👋 Hi! I'm ready to help with your code. Select some text and use the actions above, or just ask me a question below.");
        }

        // ── Send message ──────────────────────────────────────────────────────
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SendMessageAsync(TxtInput.Text.Trim());
            });
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter: Insert newline
                    int caretIndex = TxtInput.CaretIndex;
                    TxtInput.Text = TxtInput.Text.Insert(caretIndex, Environment.NewLine);
                    TxtInput.CaretIndex = caretIndex + Environment.NewLine.Length;
                    e.Handled = true;
                }
                else
                {
                    // Enter only: Send
                    e.Handled = true;
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await SendMessageAsync(TxtInput.Text.Trim());
                    });
                }
            }
        }

        private async Task SendMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            TxtInput.Clear();
            AppendUserBubble(text);

            _history.Add(new ChatMessage { Role = "user", Content = text });

            await StreamResponseAsync(LocalPilotSettings.Instance.ChatModel);
        }

        private void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var btn = (Button)sender;
                var action = btn.Tag?.ToString() ?? string.Empty;

                // Try to get editor selection
                string selectedCode = await TryGetEditorSelectionAsync();

                if (string.IsNullOrWhiteSpace(selectedCode))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendAIBubble("⚠️ No code selected. Please select some code in the editor first to use this quick action.");
                    return;
                }

                string prompt = BuildActionPrompt(action, selectedCode);
                if (string.IsNullOrEmpty(prompt)) return;

                AppendUserBubble($"/{action}\n```\n{selectedCode}\n```");

                _history.Add(new ChatMessage { Role = "user", Content = prompt });
                
                // Use action-specific model if configured
                string model = GetActionModel(action);
                await StreamResponseAsync(model);
            });
        }

        private string GetActionModel(string action)
        {
            var s = LocalPilotSettings.Instance;
            return action switch
            {
                "explain"  => s.ExplainModel,
                "refactor" => s.RefactorModel,
                "document" => s.DocModel,
                "review"   => s.ReviewModel,
                _          => s.ChatModel
            };
        }

        private string BuildActionPrompt(string action, string code)
        {
            bool hasCode = !string.IsNullOrWhiteSpace(code);
            string codeBlock = hasCode ? $"\n\n```\n{code}\n```" : "(no code selected)";

            return action switch
            {
                "explain"  => $"Explain the following code clearly and concisely:{codeBlock}",
                "refactor" => $"Refactor the following code to improve readability, performance and best practices. Show the improved version:{codeBlock}",
                "document" => $"Add XML documentation comments (summary, params, returns) for the following code and return only the documented code block:{codeBlock}",
                "review"   => $"Perform a rigorous security and quality review of the following code. Identify potential bugs, performance bottlenecks, and security vulnerabilities. Provide specific, actionable suggestions for improvement:{codeBlock}",
                "fix"      => $"Identify and fix all issues in the following code:{codeBlock}",
                "test"     => $"Write comprehensive unit tests using xUnit for the following code:{codeBlock}",
                _          => string.Empty
            };
        }

        private async Task<string> TryGetEditorSelectionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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
        private async Task StreamResponseAsync(string model)
        {
            // Signal cancellation to any currently running stream
            _cts?.Cancel();

            // Wait for any previous stream to clean up and release the semaphore
            await _streamSemaphore.WaitAsync();
            try
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                if (string.IsNullOrEmpty(model))
                    model = LocalPilotSettings.Instance.ChatModel;
                
                _ollama.UpdateBaseUrl(LocalPilotSettings.Instance.OllamaBaseUrl);
                SetStreaming(true);

                var sb = new StringBuilder();
                try
                {
                    var options = new OllamaOptions
                    {
                        Temperature = LocalPilotSettings.Instance.Temperature,
                        NumPredict = LocalPilotSettings.Instance.MaxChatTokens
                    };

                    int tokenCount = 0;
                    int batchSize = 12;

                    // Buffer for incoming text to avoid hammering the UI thread
                    await foreach (var chunk in _ollama.StreamChatAsync(model, _history, options, token).ConfigureAwait(false))
                    {
                        sb.Append(chunk);
                        tokenCount++;

                        // UI Batching: Update UI less frequently as the message grows
                        // This prevents O(N^2) rendering from hanging the UI thread
                        if (tokenCount % batchSize == 0 || tokenCount == 1)
                        {
                            // Dynamic batching: Increase batch size for very long messages
                            if (tokenCount > 500) batchSize = 24;
                            if (tokenCount > 2000) batchSize = 48;

                            var currentText = sb.ToString();
                            
                            // Switch to UI thread and check for cancellation immediately
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                            token.ThrowIfCancellationRequested();

                            if (_streamingBlock == null && !string.IsNullOrEmpty(currentText))
                                _streamingBlock = AppendAIBubble(string.Empty);

                            if (_streamingBlock != null)
                            {
                                RenderMarkdown(_streamingBlock, currentText);
                                ChatScroll.ScrollToBottom();
                            }
                            
                            // Yield back to the UI thread to allow 'Stop' button and other messages to process
                            await Task.Yield();
                        }
                    }
                }
                catch (OperationCanceledException) { /* Handle naturally in finally */ }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendAIBubble($"❌ Error: {ex.Message}");
                }
                finally
                {
                    string finalMd = sb.ToString();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_streamingBlock != null)
                    {
                        var container = (StackPanel)_streamingBlock.Parent;
                        RenderFullMarkdown(container, finalMd);
                        _history.Add(new ChatMessage { Role = "assistant", Content = finalMd });
                        TrimHistory();
                    }
                    SetStreaming(false);
                    _streamingBlock = null;
                }
            }
            finally
            {
                _streamSemaphore.Release();
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void AppendUserBubble(string text)
        {
            var border = new Border
            {
                Background      = ThemeSurface,
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x20, 0x7C, 0x6A, 0xF7)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(12, 12, 2, 12),
                Padding         = new Thickness(14, 10, 14, 10),
                Margin          = new Thickness(24, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var container = new StackPanel { Orientation = Orientation.Vertical };

            var body = new RichTextBox
            {
                Background   = Brushes.Transparent,
                Foreground   = ThemeWindowFg,
                BorderThickness = new Thickness(0),
                IsReadOnly   = true,
                FontFamily   = UIFont,
                FontSize     = 13,
                IsDocumentEnabled = true,
                Padding      = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                ContextMenu  = BuildRichTextBoxContextMenu()
            };
            SetRichText(body, text);

            // container.Children.Add(label); // Removed
            container.Children.Add(body);
            border.Child = container;
            MessagesContainer.Children.Add(border);

            ChatScroll.ScrollToBottom();
        }

        private RichTextBox AppendAIBubble(string text)
        {
            var border = new Border
            {
                Background   = ThemeWindowBg,
                BorderBrush  = ThemeBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12, 12, 12, 2),
                Padding      = new Thickness(14, 10, 14, 10),
                Margin       = new Thickness(0, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth     = 1600
            };

            var container = new StackPanel { Orientation = Orientation.Vertical };

            if (string.IsNullOrEmpty(text))
            {
                // Streaming placeholder
                var body = CreateRichTextBox();
                _streamingBlock = body;
                container.Children.Add(body);
            }
            else
            {
                RenderFullMarkdown(container, text);
            }

            border.Child = container;
            MessagesContainer.Children.Add(border);
            ChatScroll.ScrollToBottom();

            return _streamingBlock;
        }

        private RichTextBox CreateRichTextBox()
        {
            var rtb = new RichTextBox
            {
                Background   = Brushes.Transparent,
                Foreground   = ThemeWindowFg,
                BorderThickness = new Thickness(0),
                IsReadOnly   = true,
                FontFamily   = UIFont,
                FontSize     = 13,
                IsDocumentEnabled = true,
                Padding      = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                ContextMenu  = BuildRichTextBoxContextMenu()
            };
            return rtb;
        }

        /// <summary>
        /// Builds a VS-theme-aware right-click context menu for read-only RichTextBox controls
        /// (Copy + Select All). Uses the same VsBrushes keys as the XAML styles.
        /// </summary>
        private ContextMenu BuildRichTextBoxContextMenu()
        {
            Brush menuBg     = ThemeSurface;
            Brush menuBorder = ThemeBorder;
            Brush itemFg     = ThemeWindowFg;
            Brush hoverBg    = BrushAccent;
            Brush hoverFg    = Brushes.White;
            Brush sepColor   = ThemeBorder;

            ContextMenu MakeMenu()
            {
                var menu = new ContextMenu
                {
                    Background = menuBg,
                    BorderBrush = menuBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0, 4, 0, 4)
                };

                // Round corners via the border template
                menu.Template = CreateMenuTemplate();

                menu.Items.Add(MakeMenuItem("Copy",       ApplicationCommands.Copy,      "\uE8C8", itemFg, hoverBg, hoverFg));
                menu.Items.Add(new Separator { Background = sepColor, Margin = new Thickness(4, 2, 4, 2) });
                menu.Items.Add(MakeMenuItem("Select All", ApplicationCommands.SelectAll, "\uE8B3", itemFg, hoverBg, hoverFg));

                return menu;
            }

            return MakeMenu();
        }

        private static ControlTemplate CreateMenuTemplate()
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var factory  = new FrameworkElementFactory(typeof(Border));
            factory.SetBinding(Border.BackgroundProperty,       new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderBrushProperty,      new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetBinding(Border.BorderThicknessProperty,  new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            factory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            factory.AppendChild(items);
            template.VisualTree = factory;
            return template;
        }

        private static MenuItem MakeMenuItem(string header, ICommand command, string icon,
                                             Brush fg, Brush hoverBg, Brush hoverFg)
        {
            var iconBlock = new TextBlock
            {
                Text       = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            };

            var item = new MenuItem
            {
                Header  = header,
                Command = command,
                Icon    = iconBlock,
                Foreground = fg,
                Background = Brushes.Transparent,
                Padding  = new Thickness(28, 6, 20, 6),
                FontSize = 12
            };

            // Hover highlight via triggers
            var style = new Style(typeof(MenuItem));
            var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(MenuItem.BackgroundProperty, hoverBg));
            hoverTrigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, hoverFg));
            style.Triggers.Add(hoverTrigger);
            item.Style = style;
            return item;
        }

        private Brush GetVsBrush(Microsoft.VisualStudio.Shell.ThemeResourceKey key)
        {
            return (Brush)Application.Current.FindResource(key);
        }

        private void RenderFullMarkdown(StackPanel container, string md)
        {
            container.Children.Clear();
            var parts = md.Split(new[] { "```" }, StringSplitOptions.None);

            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 1)
                {
                    // Code block with Copy button
                    var codePart = parts[i];
                    var nl = codePart.IndexOf('\n');
                    string lang = string.Empty;
                    if (nl >= 0)
                    {
                        lang = codePart.Substring(0, nl).Trim();
                        codePart = codePart.Substring(nl + 1);
                    }
                    string cleanCode = codePart.TrimEnd();

                    var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                    var codeBorder = new Border {
                        Background = new SolidColorBrush(Color.FromArgb(0x06, 0x00, 0x00, 0x00)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0x7C, 0x6A, 0xF7)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10)
                    };
                    
                    var codeRtb = CreateRichTextBox();
                    HighlightCode((Paragraph)codeRtb.Document.Blocks.FirstBlock, cleanCode);
                    codeBorder.Child = codeRtb;
                    grid.Children.Add(codeBorder);

                    var copyBtn = new Button {
                        Content = "📋 Copy Code",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 4, 4, 0),
                        Padding = new Thickness(6, 2, 6, 2),
                        FontSize = 9,
                        Cursor = Cursors.Hand,
                        ToolTip = "Copy this code block"
                    };

                    // Use common icon style if available
                    if (Application.Current.Resources.Contains("IconButtonStyle"))
                        copyBtn.Style = (Style)Application.Current.Resources["IconButtonStyle"];
                    else
                    {
                        copyBtn.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));
                        copyBtn.Foreground = Brushes.White;
                        copyBtn.BorderThickness = new Thickness(0);
                    }

                    copyBtn.Click += (s, e) => {
                        Clipboard.SetText(cleanCode);
                        copyBtn.Content = "✓ Copied";
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await Task.Delay(1500);
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            copyBtn.Content = "📋 Copy Code";
                        });
                    };
                    grid.Children.Add(copyBtn);
                    container.Children.Add(grid);
                }
                else
                {
                    // Plain text
                    if (string.IsNullOrWhiteSpace(parts[i])) continue;
                    var rtb = CreateRichTextBox();
                    RenderMarkdown(rtb, parts[i]);
                    container.Children.Add(rtb);
                }
            }
        }

        // ── Markdown rendering for RichTextBox ───────────────────────────────
        private void RenderMarkdown(RichTextBox rtb, string md)
        {
            if (string.IsNullOrEmpty(md)) return;

            // Enterprise Optimization: Only update if the content has actually changed
            // This avoids unnecessary WPF layout passes
            rtb.Document.Blocks.Clear();

            var paragraph = new Paragraph { Margin = new Thickness(0) };
            
            // Split on code fences - use a more stable approach for streaming
            var parts = md.Split(new[] { "```" }, StringSplitOptions.None);

            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 1)
                {
                    // Code block with Basic Syntax Highlighting
                    var code = parts[i];
                    var nl = code.IndexOf('\n');
                    if (nl >= 0) code = code.Substring(nl + 1);

                    HighlightCode(paragraph, code.TrimEnd());
                }
                else
                {
                    // Plain text with **bold** support
                    var segments = parts[i].Split(new[] { "**" }, StringSplitOptions.None);
                    for (int j = 0; j < segments.Length; j++)
                    {
                        var run = new Run(segments[j]) { Foreground = ThemeWindowFg };
                        if (j % 2 == 1) run.FontWeight = FontWeights.Bold;
                        paragraph.Inlines.Add(run);
                    }
                }
            }

            rtb.Document.Blocks.Add(paragraph);
        }

        private static readonly string[] Keywords = {
            "public", "private", "protected", "internal", "static", "void", "async", "await", "task",
            "class", "namespace", "using", "var", "string", "int", "bool", "return", "if", "else",
            "foreach", "for", "while", "switch", "case", "break", "new", "try", "catch", "finally",
            "throw", "override", "virtual", "abstract", "get", "set"
        };

        private void HighlightCode(Paragraph p, string code)
        {
            // Simple approach: split by non-word chars and match keywords
            // Or use Regex for a more robust (but still lightweight) approach
            var lines = code.Split('\n');
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                if (line.TrimStart().StartsWith("//"))
                {
                    p.Inlines.Add(new Run(line) { Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)), FontFamily = ConsoleFont });
                }
                else
                {
                    // Tokenize basic parts
                    var tokens = System.Text.RegularExpressions.Regex.Split(line, @"(\W)");
                    foreach (var token in tokens)
                    {
                        var run = new Run(token) { FontFamily = ConsoleFont, FontSize = 12 };
                        
                        if (Array.Exists(Keywords, k => k == token))
                            run.Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); // Blue Key
                        else if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d+$"))
                            run.Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)); // Green Num
                        else if (token.StartsWith("\"") || token.StartsWith("'"))
                            run.Foreground = BrushCode; // Orange String
                        else
                            run.Foreground = ThemeWindowFg; // Normal

                        p.Inlines.Add(run);
                    }
                }
                if (lineIdx < lines.Length - 1) p.Inlines.Add(new LineBreak());
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
            
            // Lock all interaction during streaming to prevent action queuing
            BtnSend.IsEnabled           = !streaming;
            BtnClear.IsEnabled          = !streaming;
            BtnQuickActions.IsEnabled    = !streaming;
            TxtInput.IsEnabled          = !streaming;
            TxtInput.Opacity           = streaming ? 0.6 : 1.0;
        }

        private void TrimHistory()
        {
            // 1. Data history trimming (what goes to AI)
            int maxHistory = LocalPilotSettings.Instance.ChatHistoryMaxItems * 2; // user+ai pairs
            while (_history.Count > maxHistory + 1) // keep system message [0]
                _history.RemoveAt(1);

            // 2. UI tree pruning (what stays in WPF memory)
            // Each message adds one 'Border' to MessagesContainer.
            // Keeping too many UI elements causes high RAM usage and lag.
            const int maxUiElements = 50; 
            while (MessagesContainer.Children.Count > maxUiElements)
            {
                MessagesContainer.Children.RemoveAt(0);
            }
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

        public void FireQuickAction(string action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mockBtn = new Button { Tag = action };
            QuickAction_Click(mockBtn, new RoutedEventArgs());
        }

        private void BtnQuickActions_Click(object sender, RoutedEventArgs e)
        {
            var menu = BtnQuickActions.ContextMenu;
            if (menu == null) return;

            var s = LocalPilotSettings.Instance;

            // In WPF, items in a ContextMenu are not generated as fields for the UserControl.
            // We find them by name or tag to safely toggle visibility.
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi)
                {
                    string action = mi.Tag?.ToString();
                    mi.Visibility = action switch
                    {
                        "explain"  => s.EnableExplain  ? Visibility.Visible : Visibility.Collapsed,
                        "refactor" => s.EnableRefactor ? Visibility.Visible : Visibility.Collapsed,
                        "document" => s.EnableDocGen   ? Visibility.Visible : Visibility.Collapsed,
                        "review"   => s.EnableReview   ? Visibility.Visible : Visibility.Collapsed,
                        "fix"      => s.EnableFix      ? Visibility.Visible : Visibility.Collapsed,
                        "test"     => s.EnableUnitTest ? Visibility.Visible : Visibility.Collapsed,
                        _          => Visibility.Visible
                    };
                }
            }

            menu.PlacementTarget = BtnQuickActions;
            menu.IsOpen = true;
        }

        private void MenuQuickAction_Click(object sender, RoutedEventArgs e)
        {
            var item = (MenuItem)sender;
            var action = item.Tag?.ToString();
            if (string.IsNullOrEmpty(action)) return;

            var mockBtn = new Button { Tag = action };
            QuickAction_Click(mockBtn, new RoutedEventArgs());
        }
    }
}
