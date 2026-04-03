using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
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
using Community.VisualStudio.Toolkit;
using System.Linq;

namespace LocalPilot.Chat
{
    public partial class LocalPilotChatControl : UserControl
    {
        private readonly OllamaService _ollama;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private CancellationTokenSource _cts;
        private int _lastStreamId = 0; // Class-level ID to track active stream

        private Brush ThemeWindowBg => (Brush)this.Resources["LpWindowBgBrush"];
        private Brush ThemeWindowFg => (Brush)this.Resources["LpWindowFgBrush"];
        private Brush ThemeSurface  => (Brush)this.Resources["LpMenuBgBrush"];
        private Brush ThemeBorder   => (Brush)this.Resources["LpMenuBorderBrush"];

        // Design tokens for rendering logic
        private static readonly FontFamily UIFont      = new FontFamily("Segoe UI");
        private static readonly FontFamily ConsoleFont = new FontFamily("Consolas");
        private static readonly SolidColorBrush BrushAccent = new SolidColorBrush(Color.FromRgb(0x7C, 0x6A, 0xF7));
        private static readonly SolidColorBrush BrushCode   = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));

        public LocalPilotChatControl()
        {
            InitializeComponent();
            _ollama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            UpdateBrushes();
            
            // Initialize history immediately to prevent race conditions during async loading
            if (_history.Count == 0) ShowWelcomeMessage();
            
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateBrushes();
            
            // Only show welcome if history was cleared or never initialized.
            // This prevents clearing history when Right-Click actions fire before Load is fully complete.
            if (_history.Count == 0) 
            {
                ShowWelcomeMessage();
            }
            
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            // No longer canceling _cts here to allow background AI streaming to finish 
            // even if the user switches tabs or focuses the editor.
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
                
                // 🎨 Theme-Aware Accent & Bubble Tinting
                var accentBrush = (Brush)Application.Current.FindResource(VsBrushes.ControlLinkTextKey) ?? BrushAccent;
                this.Resources["LpAccentBrush"] = accentBrush;
                
                var accentColor = (accentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x7C, 0x6A, 0xF7);
                var tintColor = Color.FromArgb(0x18, accentColor.R, accentColor.G, accentColor.B);
                this.Resources["LpUserBubbleBgBrush"] = new SolidColorBrush(tintColor);
                
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
                Content = "You are LocalPilot, a world-class AI pair programmer for Visual Studio. " +
                          "Your goal is to help the user write, explain, and refactor production-ready, high-performance code. " +
                          "RULES: 1. Be extremely concise and skip pleasantries. 2. Use professional, clean markdown formatting. " +
                          "3. Always prioritize the provided <PROJECT_SOURCE_CONTEXT> if available. " +
                          "4. If you don't know the answer from the context, admit it instead of hallucinating."
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
            
            // Context injection: automatically include selection if user didn't provide any block
            string selection = await TryGetEditorSelectionAsync();
            string finalPrompt = text;
            string displayMessage = text;

            if (!string.IsNullOrWhiteSpace(selection) && !text.Contains("```"))
            {
                // Concatenate selection to provide implicit context
                finalPrompt = $"Context code selected by user:\n```\n{selection}\n```\n\nUser question: {text}";
                displayMessage = $"{text}\n\n*(using current editor selection)*";
            }

            AppendUserBubble(displayMessage);
            _history.Add(new ChatMessage { Role = "user", Content = finalPrompt });

            await StreamResponseAsync(LocalPilotSettings.Instance.ChatModel);
        }

        private void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var action = btn.Tag?.ToString() ?? string.Empty;
            _ = HandleQuickActionAsync(action);
        }

        private async Task HandleQuickActionAsync(string action, string preCapturedSelection = null)
        {
            if (string.IsNullOrEmpty(action)) return;
            LocalPilotLogger.Log($"[Chat] Handling Quick Action: {action}");

            // Execute the action logic in a background-friendly way
            _ = Task.Run(async () =>
            {
                try 
                {
                    // 1. Resolve Selection
                    string selectedCode = preCapturedSelection;
                    if (string.IsNullOrWhiteSpace(selectedCode))
                    {
                        selectedCode = await TryGetEditorSelectionAsync().ConfigureAwait(false);
                    }

                    if (string.IsNullOrWhiteSpace(selectedCode))
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        AppendAIBubble("⚠️ **No code selected**. I couldn't find any code highlighted in your editor. Please select some code and try the action again.");
                        return;
                    }

                    // 2. Prepare Prompt
                    string prompt = BuildActionPrompt(action, selectedCode);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    
                    // 3. UI Update: Show the user's request
                    AppendUserBubble($"⚡ **{action.ToUpper()}**\n```\n{selectedCode}\n```");

                    // Phase 3: Create a thread-safe snapshot of history for the background task
                    List<ChatMessage> historySnapshot;
                    lock (_history)
                    {
                        if (_history.Count > 0 && _history[_history.Count - 1].Role == "user")
                            _history.RemoveAt(_history.Count - 1);
                        
                        _history.Add(new ChatMessage { Role = "user", Content = prompt });
                        historySnapshot = new List<ChatMessage>(_history);
                    }
                    
                    // 4. Trigger AI Stream
                    string model = GetActionModel(action);
                    await StreamResponseAsync(model, historySnapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError($"Critical error in HandleQuickAction (action: {action})", ex);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendAIBubble($"❌ A critical error occurred: {ex.Message}");
                }
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
            LocalPilotLogger.Log("[Chat] TryGetEditorSelectionAsync starting...");
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(global::EnvDTE.DTE)) as global::EnvDTE.DTE;

                // 1. DTE ActiveWindow
                if (dte?.ActiveWindow?.Document?.Selection is global::EnvDTE.TextSelection sel1 && !string.IsNullOrWhiteSpace(sel1.Text))
                {
                    LocalPilotLogger.Log("[Chat] Found selection via DTE.ActiveWindow");
                    return sel1.Text;
                }

                // 2. DTE ActiveDocument
                if (dte?.ActiveDocument?.Selection is global::EnvDTE.TextSelection sel2 && !string.IsNullOrWhiteSpace(sel2.Text))
                {
                    LocalPilotLogger.Log("[Chat] Found selection via DTE.ActiveDocument");
                    return sel2.Text;
                }

                // 3. Toolkit fallback
                try
                {
                    var docView = await VS.Documents.GetActiveDocumentViewAsync();
                    if (docView?.TextView?.Selection != null)
                    {
                        var selection = docView.TextView.Selection.SelectedSpans.Count > 0 
                                        ? docView.TextView.Selection.SelectedSpans[0].GetText() 
                                        : string.Empty;
                        if (!string.IsNullOrEmpty(selection)) return selection;
                    }
                } catch { /* ignore toolkit failures */ }
                
                return string.Empty;
            }
            catch { return string.Empty; }
        }

        // ── Streaming response ────────────────────────────────────────────────
        private async Task StreamResponseAsync(string model, List<ChatMessage> historyContext = null)
        {
            // Signal cancellation to any currently running stream
            _cts?.Cancel();

            _lastStreamId++;
            int myStreamId = _lastStreamId;
            
            try
            {
                // We do NOT dispose the old _cts here. Disposal while a token is being monitored
                // by the JTF or a background HttpClient request can cause hard hangs/crashes in VS.
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                if (string.IsNullOrEmpty(model))
                    model = LocalPilotSettings.Instance.ChatModel;

                // Use provided context or fall back to current history (safely copied)
                var activeHistory = historyContext ?? new List<ChatMessage>(_history);
                
                _ollama.UpdateBaseUrl(LocalPilotSettings.Instance.OllamaBaseUrl);
                SetStreaming(true);

                LocalPilotLogger.Log($"[Chat] StreamResponseAsync setup (ID: {myStreamId}, Model: {model}, History: {activeHistory.Count})");

                // Early Connectivity Check: Fast-fail if Ollama is not running (with strict timeout)
                try
                {
                    using (var earlyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        await Task.Yield(); // Ensure we yield once to let any pending UI tasks through
                        bool isOllamaUp = await _ollama.IsAvailableAsync(earlyCts.Token).ConfigureAwait(false);
                        if (!isOllamaUp)
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            AppendAIBubble("❌ **Connection Error**: Could not reach local Ollama.\n\nPlease check if Ollama is running at: " + LocalPilotSettings.Instance.OllamaBaseUrl);
                            return;
                        }
                    }
                }
                catch { /* Silent failure: the main streaming call below will handle deeper issues */ }

                await Task.Yield(); // Yield again before starting the main loop
                string finalMd = string.Empty;
                var sb = new StringBuilder();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                RichTextBox localStreamingBlock = null;
                StackPanel localContainer = null;
                bool forceFullMarkdown = false;

                try
                {
                    var options = new OllamaOptions
                    {
                        Temperature = LocalPilotSettings.Instance.Temperature,
                        NumPredict = LocalPilotSettings.Instance.MaxChatTokens
                    };

                    int tokenCount = 0;
                    int batchSize = 12;
                    var uiBuffer = new StringBuilder();

                    // 1. PROJECT CONTEXT INTEGRATION (New in v1.2) - History-Aware Search
                    // Retrieve top semantically relevant chunks based on recent conversation flow
                    var contextHistory = activeHistory.Skip(Math.Max(0, activeHistory.Count - 3)).Select(m => m.Content);
                    string searchQuery = string.Join(" ", contextHistory);
                    
                    string projectContext = await ProjectContextService.Instance.SearchContextAsync(_ollama, searchQuery, topN: 5);
                    
                    if (!string.IsNullOrEmpty(projectContext))
                    {
                        // Add as a 'Fresh Grounding' message right before the user's latest turn
                        activeHistory.Add(new ChatMessage 
                        { 
                            Role = "system", 
                            Content = projectContext 
                        });
                    }

                    // Buffer for incoming text to avoid hammering the UI thread
                    await foreach (var chunk in _ollama.StreamChatAsync(model, activeHistory, options, token).ConfigureAwait(false))
                    {
                        sb.Append(chunk);
                        uiBuffer.Append(chunk);
                        tokenCount++;

                        if (tokenCount % batchSize == 0 || tokenCount == 1)
                        {
                            if (tokenCount > 500) batchSize = 24;
                            if (tokenCount > 2000) batchSize = 48;

                            var batchContent = uiBuffer.ToString();
                            uiBuffer.Clear();

                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                            token.ThrowIfCancellationRequested();

                            if (localStreamingBlock == null && !string.IsNullOrEmpty(batchContent))
                            {
                                localStreamingBlock = AppendAIBubble(string.Empty);
                                localContainer = (StackPanel)localStreamingBlock.Parent;
                            }

                            if (localContainer != null)
                            {
                                // LIVE MARKDOWN UPDATE:
                                // To provide a 'premium' live feel, we re-render the whole bubble intermittently.
                                string currentMd = sb.ToString();

                                // Once we detect a code block or complex markdown, we stay in 'full' mode 
                                // to avoid disappearing controls and NullReferences.
                                if (forceFullMarkdown || currentMd.Contains("```") || currentMd.Contains("#"))
                                {
                                    forceFullMarkdown = true;
                                    RenderFullMarkdown(localContainer, currentMd);
                                }
                                else
                                {
                                    // Fast-path for simple streaming text (Header-free and Code-free)
                                    RenderMarkdown(localStreamingBlock, currentMd);
                                }
                                
                                ChatScroll.ScrollToEnd();
                            }
                            await Task.Yield();
                        }
                    }
                    finalMd = sb.ToString();
                }
                catch (OperationCanceledException) 
                { 
                    finalMd = sb.ToString(); 
                    LocalPilotLogger.Log($"[Chat] Stream cancelled (ID: {myStreamId})");
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError($"[Chat] Connection error during stream (ID: {myStreamId})", ex);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AppendAIBubble($"❌ Error: {ex.Message}");
                }

                // Phase 2: Final UI Cleanup (Outside the loop to avoid partial renders)
                stopwatch.Stop();
                double seconds = stopwatch.Elapsed.TotalSeconds;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!string.IsNullOrEmpty(finalMd) && localContainer != null)
                {
                    RenderFullMarkdown(localContainer, finalMd);
                    
                    // Update Header Metric (Prestige Style) - Robust WPF Tree Traversal
                    try 
                    {
                        var border = System.Windows.Media.VisualTreeHelper.GetParent(localContainer) as System.Windows.FrameworkElement;
                        var bubbleContainer = System.Windows.Media.VisualTreeHelper.GetParent(border) as System.Windows.Controls.StackPanel;
                        var header = bubbleContainer?.Children[0] as System.Windows.Controls.StackPanel;
                        
                        if (header?.Children.Count > 1 && header.Children[1] is System.Windows.Controls.TextBlock metric)
                        {
                            if (seconds >= 60)
                            {
                                int mins = (int)seconds / 60;
                                double secs = seconds % 60;
                                metric.Text = $"worked for {mins}m {secs:F1}s";
                            }
                            else
                            {
                                metric.Text = $"worked for {seconds:F1}s";
                            }
                            metric.FontWeight = System.Windows.FontWeights.Bold;
                            metric.Foreground = (System.Windows.Media.Brush)this.Resources["LpAccentBrush"];
                        }
                    } catch { /* Handle edge cases where UI structure changed during stream */ }

                    _history.Add(new ChatMessage { Role = "assistant", Content = finalMd });
                    TrimHistory();
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Chat] Critical failure in StreamResponseAsync (ID: {myStreamId})", ex);
            }
            finally
            {
                // Always unlock UI if this is still the most recent requested session
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (myStreamId == _lastStreamId) SetStreaming(false);
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void AppendUserBubble(string text)
        {
            var bubbleContainer = new StackPanel { Margin = new Thickness(0, 8, 8, 8) };
            
            // 👤 Role Header (Right-Aligned)
            var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,0,4,2) };
            header.Children.Add(new TextBlock { Text = "YOU", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)this.Resources["LpMutedFgBrush"], VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = "\uE77B", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = (Brush)this.Resources["LpMutedFgBrush"], Margin = new Thickness(4,0,0,0) });
            bubbleContainer.Children.Add(header);

            var border = new Border
            {
                Background      = (Brush)this.Resources["LpUserBubbleBgBrush"],
                BorderBrush     = (Brush)this.Resources["LpAccentBrush"],
                Opacity         = 0.95, // Slight transparency for a glass feel
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(12, 12, 2, 12),
                Padding         = new Thickness(14, 10, 14, 10),
                Margin          = new Thickness(40, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };

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

            border.Child = body;
            bubbleContainer.Children.Add(border);
            MessagesContainer.Children.Add(bubbleContainer);

            ChatScroll.ScrollToEnd();
        }

        private RichTextBox AppendAIBubble(string text)
        {
            var bubbleContainer = new StackPanel { Margin = new Thickness(8, 8, 0, 8) };

            // 🤖 Role Header (Left-Aligned - Branded Prestige Style)
            var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4,0,0,5) };
            
            // Integrated Official Brand Logo
            try {
                var logoBrush = new Image { 
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/LocalPilot;component/Assets/Logo_Concept_Minimalist.png")),
                    Width = 14, Height = 14, Margin = new Thickness(0,0,6,0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                header.Children.Add(logoBrush);
            } catch { /* Handle missing asset gracefully */ }

            header.Children.Add(new TextBlock { Text = "(thinking...)", FontSize = 9, FontWeight = FontWeights.Normal, Foreground = (Brush)this.Resources["LpMutedFgBrush"], VerticalAlignment = VerticalAlignment.Center });
            bubbleContainer.Children.Add(header);

            var border = new Border
            {
                Background   = ThemeWindowBg,
                BorderBrush  = Brushes.Transparent, // Removed border for AI (Antigravity Style)
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12, 12, 12, 2),
                Padding      = new Thickness(14, 10, 14, 10),
                Margin       = new Thickness(0, 0, 40, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth     = 1600
            };

            var container = new StackPanel { Orientation = Orientation.Vertical };

            if (string.IsNullOrEmpty(text))
            {
                var body = CreateRichTextBox();
                container.Children.Add(body);
                border.Child = container;
                bubbleContainer.Children.Add(border);
                MessagesContainer.Children.Add(bubbleContainer);
                ChatScroll.ScrollToEnd();
                return body;
            }
            else
            {
                RenderFullMarkdown(container, text);
            }

            border.Child = container;
            bubbleContainer.Children.Add(border);
            MessagesContainer.Children.Add(bubbleContainer);
            ChatScroll.ScrollToEnd();

            return null;
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

        private void RenderFullMarkdown(StackPanel container, string md)
        {
            if (string.IsNullOrEmpty(md)) return;
            container.Children.Clear();

            // 🎨 Theme-Aware Resolution with Safe Fallbacks
            var accentBrush = (Brush)this.FindResource("LpAccentBrush") ?? BrushAccent;
            var mutedBrush = (Brush)this.FindResource("LpMutedFgBrush") ?? Brushes.Gray;

            // 1. PROJECT CONTEXT INDICATOR (v2.0)
            if (md.Contains("--- PROJECT_SOURCE_CONTEXT ---"))
            {
                var indicator = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)),
                    BorderBrush = accentBrush,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 16),
                    CornerRadius = new CornerRadius(2)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = "\uE945", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = accentBrush, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = "PROJECT INTELLIGENCE ACTIVE", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = accentBrush, VerticalAlignment = VerticalAlignment.Center });
                indicator.Child = sp;
                container.Children.Add(indicator);
            }

            var regex = new System.Text.RegularExpressions.Regex(@"```([\s\S]*?)```");
            var matches = regex.Matches(md);
            int lastIndex = 0;

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string textPart = md.Substring(lastIndex, match.Index - lastIndex);
                if (!string.IsNullOrWhiteSpace(textPart))
                {
                    var rtb = CreateRichTextBox();
                    RenderMarkdown(rtb, textPart);
                    container.Children.Add(rtb);
                }

                // 2. ENTERPRISE CODE BLOCK (v2.0)
                string rawCode = match.Groups[1].Value;
                string lang = "CODE";
                string cleanCode = rawCode;
                int firstNewline = rawCode.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    string firstLine = rawCode.Substring(0, firstNewline).Trim();
                    if (!string.IsNullOrEmpty(firstLine) && !firstLine.Contains(" ") && !firstLine.Contains("\n"))
                    {
                        lang = firstLine.ToUpper();
                        cleanCode = rawCode.Substring(firstNewline + 1).Trim();
                    }
                }
                cleanCode = cleanCode.Trim();

                var codeGrid = new Grid { Margin = new Thickness(0, 8, 0, 16) };
                codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // 🏗️ Header Bar
                var headerBar = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1, 1, 1, 0),
                    CornerRadius = new CornerRadius(6, 6, 0, 0),
                    Padding = new Thickness(12, 4, 8, 4)
                };
                var headerStack = new DockPanel { LastChildFill = false };
                headerStack.Children.Add(new TextBlock { Text = lang, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center });
                
                var copyBtn = new Button { 
                    Style = (Style)this.FindResource("IconButtonStyle"), 
                    ToolTip = "Copy code to clipboard",
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 2, 6, 2)
                };
                DockPanel.SetDock(copyBtn, Dock.Right);
                var copyStack = new StackPanel { Orientation = Orientation.Horizontal };
                copyStack.Children.Add(new TextBlock { Text = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center });
                var copyText = new TextBlock { Text = "COPY", FontSize = 8, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                copyStack.Children.Add(copyText);
                copyBtn.Content = copyStack;
                copyBtn.Click += (s, e) => { 
                    Clipboard.SetText(cleanCode); 
                    copyText.Text = "COPIED!";
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                        await Task.Delay(2000);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        copyText.Text = "COPY";
                    });
                };
                
                headerStack.Children.Add(copyBtn);
                headerBar.Child = headerStack;
                Grid.SetRow(headerBar, 0);
                codeGrid.Children.Add(headerBar);

                // 📝 Code Content
                var contentBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x00, 0x00)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(0, 0, 6, 6),
                    Padding = new Thickness(12)
                };
                var codeRtb = CreateRichTextBox();
                if (codeRtb.Document.Blocks.FirstBlock is Paragraph p) HighlightCode(p, cleanCode);
                else SetRichText(codeRtb, cleanCode);
                
                contentBorder.Child = codeRtb;
                Grid.SetRow(contentBorder, 1);
                codeGrid.Children.Add(contentBorder);

                container.Children.Add(codeGrid);
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < md.Length)
            {
                string textTail = md.Substring(lastIndex);
                if (!string.IsNullOrWhiteSpace(textTail))
                {
                    var rtbTail = CreateRichTextBox();
                    RenderMarkdown(rtbTail, textTail);
                    container.Children.Add(rtbTail);
                }
            }
        }

        // ── Markdown rendering for RichTextBox ───────────────────────────────
        private void RenderMarkdown(RichTextBox rtb, string md)
        {
            if (string.IsNullOrEmpty(md)) return;
            rtb.Document.Blocks.Clear();

            var lines = md.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) 
                {
                    rtb.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 8) });
                    continue;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };

                // 1. Headings (# ## ###)
                if (trimmed.StartsWith("#"))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    
                    var headerText = trimmed.Substring(level).Trim();
                    var run = new Run(headerText) 
                    { 
                        FontSize = level == 1 ? 20 : (level == 2 ? 18 : 16),
                        FontWeight = FontWeights.Bold,
                        Foreground = ThemeWindowFg
                    };
                    paragraph.Inlines.Add(run);
                    paragraph.Margin = new Thickness(0, 8, 0, 4);
                }
                // 2. Lists (- * 1.)
                else if (trimmed.StartsWith("-") || trimmed.StartsWith("*") || (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.'))
                {
                    paragraph.Margin = new Thickness(12, 0, 0, 2);
                    RenderInlineMarkdown(paragraph, trimmed);
                }
                // 3. Normal Paragraph
                else
                {
                    RenderInlineMarkdown(paragraph, line);
                }

                rtb.Document.Blocks.Add(paragraph);
            }
        }

        private void RenderInlineMarkdown(Paragraph p, string text)
        {
            // Simple Inline parsing for **Bold** and `Code`
            var tokens = System.Text.RegularExpressions.Regex.Split(text, @"(\*\*|`)").Where(t => !string.IsNullOrEmpty(t)).ToList();
            bool isBold = false;
            bool isCode = false;

            foreach (var token in tokens)
            {
                if (token == "**") { isBold = !isBold; continue; }
                if (token == "`") { isCode = !isCode; continue; }

                var run = new Run(token);
                if (isBold) run.FontWeight = FontWeights.Bold;
                if (isCode)
                {
                    run.FontFamily = ConsoleFont;
                    run.Foreground = BrushAccent;
                    run.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0x7C, 0x6A, 0xF7));
                }
                else
                {
                    run.Foreground = ThemeWindowFg;
                }
                p.Inlines.Add(run);
            }
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

        private void AppendToRichTextBox(RichTextBox rtb, string text)
        {
            if (rtb.Document.Blocks.FirstBlock is Paragraph p)
            {
                p.Inlines.Add(new Run(text) { Foreground = ThemeWindowFg });
            }
        }

        // Remove unused method — user bubbles now use TextBlock directly

        private void SetStreaming(bool streaming)
        {
            // Ensure we are on the UI thread before touching any WPF controls.
            // This is critical now that most logic runs on a background Task.Run.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                StreamingBar.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
                
                // Lock all interaction during streaming to prevent action queuing
                BtnSend.IsEnabled           = !streaming;
                BtnClear.IsEnabled          = !streaming;
                BtnQuickActions.IsEnabled    = !streaming;
                TxtInput.IsEnabled          = !streaming;
                TxtInput.Opacity           = streaming ? 0.6 : 1.0;

                if (!streaming) TxtInput.Focus();
            });
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
            AppendAIBubble("Conversation cleared. Project context will still be used if indexed.");
        }


        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        public void FireQuickAction(string action, string capturedSelection = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = HandleQuickActionAsync(action, capturedSelection);
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
