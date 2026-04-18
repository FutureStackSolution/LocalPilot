using LocalPilot.Models;
using LocalPilot.Services;
using LocalPilot.Settings;
using LocalPilot.Chat.ViewModels;
using System.IO;
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
        private string _lastAuthoringCode = null; // Buffer for original code during Refactor/Fix
        private string _currentAction = null;     // Tracks active quick action context
        private int _lastStreamId = 0; // Class-level ID to track active stream
        
        // Agent Mode Services
        private readonly ToolRegistry _toolRegistry;
        private readonly AgentOrchestrator _agentOrchestrator;
        private TaskCompletionSource<bool> _permissionTcs;
        private StackPanel _agentCurrentContainer;
        private StackPanel _agentTurnContainer;
        private StringBuilder _agentResponseSb = new StringBuilder();
        private readonly ProjectMapService _projectMap;
        private bool _isStreaming = false;
        private readonly ChatSessionViewModel _sessionViewModel;
        private readonly AgentTurnCoordinator _agentTurnCoordinator;
        private readonly AgentUiRenderer _agentUiRenderer;
        private readonly AgentTurnLayoutBuilder _agentTurnLayoutBuilder;
        private readonly StagingPanelBuilder _stagingPanelBuilder;
        
        
        // 🚀 UI Performance State
        private double _lastScrollTime = 0;
        private string _lastRenderedMarkdown = "";
        private object _lastActiveBlockElement = null;
        private string _activeBlockType = null; // "text", "code", "thought"
        private DateTime _lastUiUpdateTime = DateTime.MinValue;
        private StringBuilder _currentChunkSb = new StringBuilder(); // 🚀 Text since last activity
        private StackPanel _currentNarrativeContainer = null;
        private StackPanel _currentActivityContainer;
        private FrameworkElement _currentNarrativeLabel;
        private FrameworkElement _currentActivityLabel;

        private Brush ThemeWindowBg => (Brush)this.Resources["LpWindowBgBrush"];
        private Brush ThemeWindowFg => (Brush)this.Resources["LpWindowFgBrush"];
        private Brush ThemeSurface  => (Brush)this.Resources["LpMenuBgBrush"];
        private Brush ThemeBorder   => (Brush)this.Resources["LpMenuBorderBrush"];

        // Design tokens for rendering logic
        private static readonly FontFamily UIFont      = new FontFamily("Segoe UI");
        private static readonly FontFamily ConsoleFont = new FontFamily("Consolas");

        public LocalPilotChatControl()
        {
            InitializeComponent();
            _sessionViewModel = new ChatSessionViewModel();
            _agentTurnCoordinator = new AgentTurnCoordinator();
            _agentUiRenderer = new AgentUiRenderer();
            _agentTurnLayoutBuilder = new AgentTurnLayoutBuilder();
            _stagingPanelBuilder = new StagingPanelBuilder();
            DataContext = _sessionViewModel;
            _ollama = new OllamaService(LocalPilotSettings.Instance.OllamaBaseUrl);
            
            // Initialize Agent Services
            _toolRegistry = new ToolRegistry();
            _projectMap = new ProjectMapService();
            _agentOrchestrator = new AgentOrchestrator(_ollama, _toolRegistry, ProjectContextService.Instance, _projectMap);
            
            // Wire up Agent events
            _agentOrchestrator.OnStatusUpdate += OnAgentStatusUpdate;
            _agentOrchestrator.OnToolCallPending += OnAgentToolCallPending;
            _agentOrchestrator.OnMessageFragment += OnAgentMessageFragment;
            _agentOrchestrator.OnMessageCompleted += OnAgentMessageCompleted;
            _agentOrchestrator.OnTurnModificationsPending += OnAgentModificationsPending;
            _agentOrchestrator.RequestPermissionAsync = HandlePermissionRequestAsync;



            UpdateBrushes();
            
            // Initialize history immediately to prevent race conditions during async loading
            if (_history.Count == 0) ShowWelcomeMessage();
            
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }







        private void BtnRate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Placeholder URL for the Marketplace
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://marketplace.visualstudio.com/items?itemName=FutureStack.LocalPilot") { UseShellExecute = true });
            }
            catch { }
        }

        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/FutureStackSolution/LocalPilot/issues") { UseShellExecute = true });
            }
            catch { }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateBrushes();
            
            // Only show welcome if history was cleared or never initialized.
            if (_history.Count == 0) 
            {
                ShowWelcomeMessage();
            }

            VSColorTheme.ThemeChanged += OnThemeChanged;


            // 🚀 Professional Background Grounding
            if (LocalPilotSettings.Instance.EnableProjectMap)
            {
                _ = StartBackgroundIndexingAsync();
            }
        }

        private async Task StartBackgroundIndexingAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution == null || string.IsNullOrEmpty(solution.FullPath)) return;

                string root = System.IO.Path.GetDirectoryName(solution.FullPath);
                LocalPilotLogger.Log($"[Background] Triggering pre-emptive context indexing for {root}...");

                // 1. Warm up the Project Map (Pre-calculate headers)
                _ = Task.Run(async () => {
                    await _projectMap.GenerateProjectMapAsync(root);
                });

                // 2. Perform Semantic Indexing (RAG)
                _ = ProjectContextService.Instance.IndexSolutionAsync(_ollama);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.Log($"[Background] Pre-indexing failed: {ex.Message}");
            }
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
                // Base theme brushes from Visual Studio
                var toolWindowBg = Application.Current.FindResource(VsBrushes.ToolWindowBackgroundKey) as SolidColorBrush;
                var toolWindowFg = Application.Current.FindResource(VsBrushes.ToolWindowTextKey) as Brush ?? Brushes.Black;
                var borderBrush  = Application.Current.FindResource(VsBrushes.ToolWindowBorderKey) as Brush ?? Brushes.Gray;
                var grayText     = Application.Current.FindResource(VsBrushes.GrayTextKey) as Brush ?? Brushes.DarkGray;

                // Fallback when VS theme resources are unavailable
                var baseBgColor = toolWindowBg?.Color ?? Colors.White;
                bool isDark = IsDark(baseBgColor);

                // Derived surfaces for better separation across Dark/Light/Blue themes
                var menuBgColor = AdjustColor(baseBgColor, isDark ? 8 : -8);
                var userBubbleColor = AdjustColor(baseBgColor, isDark ? 12 : -12);

                this.Resources["LpWindowBgBrush"] = new SolidColorBrush(baseBgColor);
                this.Resources["LpWindowFgBrush"] = toolWindowFg;
                this.Resources["LpMenuBgBrush"] = new SolidColorBrush(menuBgColor);
                this.Resources["LpMenuBorderBrush"] = borderBrush;
                this.Resources["LpMutedFgBrush"] = grayText;
                
                // 💎 Adaptive Ghost Engine Tokens
                if (isDark)
                {
                    this.Resources["LpGlassBgBrush"] = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)); // Subtle White Glass
                    this.Resources["LpGlassBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                    this.Resources["LpTimelineLineBrush"] = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                    ((System.Windows.Media.Effects.DropShadowEffect)this.Resources["LpGhostShadow"]).Opacity = 0.5;
                }
                else
                {
                    this.Resources["LpGlassBgBrush"] = new SolidColorBrush(Color.FromArgb(0x0F, 0x00, 0x00, 0x00)); // Slightly darker Glass
                    this.Resources["LpGlassBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00));
                    this.Resources["LpTimelineLineBrush"] = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
                    this.Resources["LpMutedFgBrush"] = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)); // Force Dark Gray for readability
                    ((System.Windows.Media.Effects.DropShadowEffect)this.Resources["LpGhostShadow"]).Opacity = 0.15;
                }

                // 🎨 Accent & Highlight area - Aggressive Discovery for Brand Colors (e.g. Pink, Mango)
                var accentBrush = Application.Current.FindResource(VsBrushes.HighlightKey) as Brush
                                  ?? Application.Current.FindResource(VsBrushes.ControlLinkTextKey) as Brush
                                  ?? new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF));
                
                this.Resources["LpAccentBrush"]    = accentBrush;
                var accentColor = (accentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x2D, 0x8C, 0xFF);
                this.Resources["LpAccentHoverBrush"] = new SolidColorBrush(AdjustColor(accentColor, isDark ? 14 : -10));
                this.Resources["LpStopBrush"]      = new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)); 
                this.Resources["LpHoverBgBrush"]   = Application.Current.FindResource(VsBrushes.CommandBarHoverKey) as Brush
                                                    ?? new SolidColorBrush(AdjustColor(baseBgColor, isDark ? 16 : -16));
                this.Resources["LpHoverFgBrush"]   = toolWindowFg;
                this.Resources["LpUserBubbleBgBrush"] = new SolidColorBrush(userBubbleColor);
                this.Resources["LpSuccessBrush"]    = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)); // VS Tealer/Green


                UpdateSyntaxBrushes();
                ChatScroll.Background = (Brush)this.Resources["LpWindowBgBrush"];
            }
            catch { }
        }

        private void UpdateSyntaxBrushes()
        {
            var bgBrush = Application.Current.FindResource(VsBrushes.ToolWindowBackgroundKey) as SolidColorBrush;
            bool isDark = bgBrush != null && (bgBrush.Color.R + bgBrush.Color.G + bgBrush.Color.B) / 3.0 < 128;

            if (isDark)
            {
                SetBrush("LpCodeKwBrush",      Color.FromRgb(0x56, 0x9C, 0xD6)); 
                SetBrush("LpCodeCommentBrush", Color.FromRgb(0x6A, 0x99, 0x55)); 
                SetBrush("LpCodeStringBrush",  Color.FromRgb(0xD6, 0x9D, 0x85)); 
                SetBrush("LpCodeNumberBrush",  Color.FromRgb(0xB5, 0xCE, 0xA8)); 
                SetBrush("LpCodeTypeBrush",    Color.FromRgb(0x4E, 0xC9, 0xB0)); 
                SetBrush("LpCodeMethodBrush",  Color.FromRgb(0xDC, 0xDC, 0xAA)); 
            }
            else
            {
                SetBrush("LpCodeKwBrush",      Color.FromRgb(0x00, 0x00, 0xFF)); 
                SetBrush("LpCodeCommentBrush", Color.FromRgb(0x00, 0x80, 0x00)); 
                SetBrush("LpCodeStringBrush",  Color.FromRgb(0xA3, 0x15, 0x15)); 
                SetBrush("LpCodeNumberBrush",  Color.FromRgb(0x09, 0x86, 0x58)); 
                SetBrush("LpCodeTypeBrush",    Color.FromRgb(0x26, 0x7F, 0x99)); 
                SetBrush("LpCodeMethodBrush",  Color.FromRgb(0x79, 0x5E, 0x26)); 
            }
        }

        private void SetBrush(string key, Color color)
        {
            this.Resources[key] = new SolidColorBrush(color);
        }

        private static bool IsDark(Color c)
        {
            return ((c.R + c.G + c.B) / 3.0) < 128;
        }

        private static Color AdjustColor(Color c, int delta)
        {
            byte Clamp(int v) => (byte)Math.Max(0, Math.Min(255, v));
            return Color.FromArgb(
                c.A,
                Clamp(c.R + delta),
                Clamp(c.G + delta),
                Clamp(c.B + delta));
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
                Content = PromptLoader.GetPrompt("SystemPrompt")
            });

            // Modern introductory text is now partly in XAML, but we can add a greet
            AppendAIBubble("Hi, I am ready to help with your code. Select text and use the actions above, or ask a question below.");
        }

        // ── Send message ──────────────────────────────────────────────────────

        private void BtnSend_Click(object sender, RoutedEventArgs e) => HandleSendInput();

        private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter: Let it fall through to insert newline
                    return;
                }
                
                // Enter only: Send
                e.Handled = true;
                HandleSendInput();
            }
        }

        private void HandleSendInput()
        {
            if (_isStreaming)
            {
                _cts?.Cancel();
                _isStreaming = false;
                SetStreaming(false);
                return;
            }

            string text = TxtInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SendMessageAsync(text);
            });
        }

        private async Task SendMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            TxtInput.Clear();
            
            // Note: History management and context injection moved to AgentOrchestrator
            // so that slash commands can resolve selection/files in a background-friendly way.
            await RunAgentTaskAsync(text);
        }

        private async Task RunAgentTaskAsync(string task)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 👻 Hide welcome clutter from the timeline once the user takes action
            if (WelcomePanel.Visibility == Visibility.Visible)
            {
                WelcomePanel.Visibility = Visibility.Collapsed;
            }

            TxtInput.Clear();
            AppendUserBubble(task);
            
            SetStreaming(true);
            
            StartNewAgentTurn();

            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                
                await _agentOrchestrator.RunTaskAsync(task, _cts.Token);
            }
            catch (Exception ex)
            {
                AppendAIBubble($"❌ Agent Error: {ex.Message}");
                SetStreaming(false);
            }
        }

        private void StartNewAgentTurn()
        {
            _agentResponseSb.Clear();
            _currentChunkSb.Clear();
            _currentNarrativeContainer = null;
            var layout = _agentTurnLayoutBuilder.BuildTurnLayout(() => CreateAIHeader(out _), this.Resources);
            _agentTurnContainer = layout.TurnContainer;
            _agentCurrentContainer = layout.CurrentContainer;
            _currentActivityContainer = layout.ActivityContainer;
            _currentNarrativeContainer = layout.NarrativeContainer;
            _currentNarrativeLabel = layout.NarrativeLabel;
            _currentActivityLabel = layout.ActivityLabel;

            MessagesContainer.Children.Add(_agentTurnContainer);
            ChatScroll.ScrollToEnd();
        }

        private void OnAgentToolCallPending(ToolCallRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var display = _agentUiRenderer.GetToolCallDisplayInfo(request);

                // Reset chunk tracking to force a NEW narrative block for any text following this action
                _currentChunkSb.Clear();
                _currentNarrativeContainer = null;
                _lastRenderedMarkdown = "";
                _lastActiveBlockElement = null;

                // Append the activity row to the DEDICATED activity container
                AddWorkRow(display.Label, display.Icon, display.Detail);
            });
        }

        private void AddWorkRow(string label, string icon, string detail = null)
        {
            if (_agentTurnContainer == null) StartNewAgentTurn();
            if (_currentActivityContainer == null) return;

            var node = _agentUiRenderer.CreateWorkRow(label, icon, detail, this.Resources);
            _currentActivityContainer.Children.Add(node);

            // Show ACTIVITY header when the first row is added
            if (_currentActivityLabel != null && _currentActivityLabel.Visibility != Visibility.Visible)
            {
                _currentActivityLabel.Visibility = Visibility.Visible;
            }
        }


        private void EnsureAgentBubble()
        {
            _agentCurrentContainer = _agentTurnLayoutBuilder.EnsureAgentBubble(
                _agentCurrentContainer,
                () => AppendAIBubble(string.Empty));
        }

        private void OnAgentMessageFragment(string fragment)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();


                _agentResponseSb.Append(fragment);
                _currentChunkSb.Append(fragment);

                // Show the RESPONSE header once we actually have text from the model
                if (_currentNarrativeLabel != null && _currentNarrativeLabel.Visibility != Visibility.Visible)
                {
                    _currentNarrativeLabel.Visibility = Visibility.Visible;
                }

                if (_currentNarrativeContainer == null)
                {
                    AppendNarrativeBlock();
                }

                if (_currentNarrativeContainer == null) return; // 🛡️ Safety: Skip fragment if container lost

                // 🚀 UI OPTIMIZATION: Throttled Incremental Update
                if ((DateTime.Now - _lastUiUpdateTime).TotalMilliseconds > 32) // ~30 FPS
                {
                    RenderMarkdownIncremental(_currentNarrativeContainer, _currentChunkSb.ToString());
                    _lastUiUpdateTime = DateTime.Now;
                }
            });
        }

        private void OnAgentModificationsPending(Dictionary<string, string> changes)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                var panel = BuildStagingPanel(changes);
                if (_agentTurnContainer != null)
                {
                    _agentTurnContainer.Children.Add(panel);
                }
                else
                {
                    MessagesContainer.Children.Add(panel);
                }
                ChatScroll.ScrollToEnd();
            });
        }

        private FrameworkElement BuildStagingPanel(Dictionary<string, string> changes)
        {
            return _stagingPanelBuilder.Build(
                changes,
                this.Resources,
                WriteFileAsync,
                ShowDiffAsync,
                message => AppendAIBubble(message));
        }

        private async Task WriteFileAsync(string path, string content)
        {
            try
            {
                await Task.Run(() => System.IO.File.WriteAllText(path, content));
                LocalPilotLogger.Log($"[UI] File written: {path}");
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[UI] Failed to write file: {path}", ex);
            }
        }

        private async Task ShowDiffAsync(string path, string newContent)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                // Create temp file for comparison
                string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LocalPilotDiff");
                if (!System.IO.Directory.Exists(tempDir)) System.IO.Directory.CreateDirectory(tempDir);
                
                string tempFile = System.IO.Path.Combine(tempDir, "proposed_" + System.IO.Path.GetFileName(path));
                System.IO.File.WriteAllText(tempFile, newContent);

                var dte = await VS.GetRequiredServiceAsync<global::EnvDTE.DTE, global::EnvDTE.DTE>();
                dte.ExecuteCommand("Tools.DiffFiles", $"\"{path}\" \"{tempFile}\"");
            }
            catch (Exception ex)
            {
               LocalPilotLogger.LogError("[UI] Diff failed", ex);
            }
        }

        private void OnAgentMessageCompleted(string fullMessage)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                // Final full render to ensure all closing tags/backticks are perfectly handled.
                if (_currentNarrativeContainer != null)
                {
                    // 🛡️ DUPLICATION PROTECTION:
                    // If the full message is identical to what's already there (rare but possible with loops),
                    // skip re-rendering to avoid flicker and visual duplication.
                    if (fullMessage == _lastRenderedMarkdown) return;

                    RenderFullMarkdown(_currentNarrativeContainer, fullMessage);
                }
                else
                {
                    // If no current narrative block, create one to ensure we don't clear the whole turn
                    var narrative = AppendNarrativeBlock();
                    if (narrative != null)
                    {
                        RenderFullMarkdown(narrative, fullMessage);
                    }
                }
                
                _lastUiUpdateTime = DateTime.Now;
                _lastRenderedMarkdown = fullMessage;

                // Reset narrative state for potential next turn in the same task
                _currentNarrativeContainer = null;
                _currentChunkSb.Clear();
                _agentResponseSb.Clear();
                
                ChatScroll.ScrollToEnd();
            });
        }


        private FrameworkElement BuildThoughtCard(string thought)
        {
            // 👻 Ghost UI: Modern, panel-less look with subtle accent grounding
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 10), Opacity = 0.9 };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new TextBlock
            {
                Text = "\uE9CE", // Brain/Intelligence icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = (Brush)this.Resources["LpAccentBrush"],
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = "REASONING",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)this.Resources["LpMutedFgBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(header);

            var rtb = CreateRichTextBox();
            rtb.Opacity = 0.75;
            rtb.FontSize = 12;
            rtb.FontStyle = FontStyles.Italic;
            
            // Use existing markdown logic for the thought content
            RenderMarkdown(rtb, thought.Trim());
            
            var border = new Border
            {
                BorderBrush = (Brush)this.Resources["LpAccentBrush"],
                BorderThickness = new Thickness(1.5, 0, 0, 0),
                Padding = new Thickness(14, 0, 0, 0),
                Margin = new Thickness(4, 2, 0, 4),
                Child = rtb
            };
            
            root.Children.Add(border);
            root.Tag = rtb; // Store reference for incremental updates
            return root;
        }


        private void OnAgentStatusUpdate(AgentStatus status, string detail)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                string model = LocalPilotSettings.Instance.ChatModel;
                var statusState = _agentTurnCoordinator.BuildStatusState(model, status, detail);
                _sessionViewModel.AgentTurn.StatusText = statusState.HeaderText;
                _sessionViewModel.AgentTurn.DetailText = statusState.DetailText;
                
                if (!statusState.IsCompletion)
                {
                    if (statusState.IsCancelled || statusState.IsFailure)
                    {
                        var container = _agentCurrentContainer;
                        _agentCurrentContainer = null;

                        if (container != null)
                        {
                            var badge = _agentUiRenderer.CreateTerminalBadge(statusState, this.Resources);
                            container.Children.Add(badge);
                        }
                        else
                        {
                            AppendAIBubble(statusState.IsCancelled
                                ? "**Task cancelled by user.**"
                                : "**Task stopped due to an error.**");
                        }

                        await Task.Delay(400); // Brief pause for visual confirmation
                        SetStreaming(false);
                    }
                    else
                    {
                        EnsureAgentBubble();
                        AgentStatusBar.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    // 🏁 Task Completed Successfully - Clean exit
                    if (_currentActivityContainer != null)
                    {
                         AddWorkRow("Task Completed", "\uE73E", null);
                    }
                    else
                    {
                         // No specific work row added, but model should have given a final summary narrative.
                         // We avoid adding a generic bubble here to prevent duplication of intent.
                    }
                    
                    _agentCurrentContainer = null;
                    SetStreaming(false);
                }
                ChatScroll.ScrollToEnd();
            });
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
            _currentAction = action;
            _sessionViewModel.CurrentAction = action;
            LocalPilotLogger.Log($"[Chat] Handling Quick Action: {action}");

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
                    AppendAIBubble("**No code selected.** I could not find highlighted code in your editor. Select code and try again.");
                    return;
                }

                // 2. Prepare Prompt
                string prompt = BuildActionPrompt(action, selectedCode);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                // 3. UI Update: Show the user's request as a slash command for visual consistency
                TxtInput.Clear();
                AppendUserBubble($"/{action}");
                
                // 4. Trigger Agent Task
                await RunAgentTaskAsync(prompt);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"Critical error in HandleQuickAction (action: {action})", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AppendAIBubble($"❌ A critical error occurred: {ex.Message}");
            }
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
            var s = LocalPilotSettings.Instance;
            bool hasCode = !string.IsNullOrWhiteSpace(code);
            string codeBlock = hasCode ? $"\n\n```\n{code}\n```" : "(no code selected)";

            string templateName = action switch
            {
                "explain"  => "ExplainPrompt",
                "refactor" => "RefactorPrompt",
                "document" => "DocumentPrompt",
                "review"   => "ReviewPrompt",
                "fix"      => "FixPrompt",
                "test"     => "TestPrompt",
                _          => null
            };

            if (templateName == null) return string.Empty;
            return PromptLoader.GetPrompt(templateName, new Dictionary<string, string> { { "codeBlock", codeBlock } });
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
                            AppendAIBubble("**Connection error:** Could not reach local Ollama.\n\nCheck that Ollama is running at: " + LocalPilotSettings.Instance.OllamaBaseUrl);
                            return;
                        }
                    }
                }
                catch { /* Silent failure: the main streaming call below will handle deeper issues */ }

                await Task.Yield(); // Yield again before starting the main loop
                string finalMd = string.Empty;
                var sb = new StringBuilder();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var startTime = DateTime.Now;
                RichTextBox localStreamingBlock = null;
                StackPanel localContainer = null;
                bool forceFullMarkdown = false;

                try
                {
                    var options = new OllamaOptions
                    {
                        Temperature = (float)LocalPilotSettings.Instance.Temperature,
                        NumPredict = LocalPilotSettings.Instance.MaxChatTokens
                    };

                    string modelName = LocalPilotSettings.Instance.ChatModel;
                    SetStreaming(true, modelName);

                    int tokenCount = 0;
                    int batchSize = 12;
                    var uiBuffer = new StringBuilder();

                    // 1. PROJECT CONTEXT INTEGRATION (v1.3) - History-Aware Search
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

                            if (localContainer == null && !string.IsNullOrEmpty(batchContent))
                            {
                                localContainer = AppendAIBubble(string.Empty);
                                localStreamingBlock = CreateRichTextBox();
                                localContainer.Children.Add(localStreamingBlock);
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
                                
                                // Robust Scroll: Ensure we reach the true bottom after layout refresh (VSTHRD010 safe)
                                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                                {
                                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                    ChatScroll.ScrollToEnd();
                                });
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

                // Finalizing response status (Success or Cancel)
                if (localContainer != null && localContainer.Tag is TextBlock statusLabel)
                {
                    var duration = DateTime.Now - startTime;
                    statusLabel.Text = $"  ·  worked for {duration.TotalSeconds:F1}s";
                    statusLabel.FontStyle = FontStyles.Normal;
                    statusLabel.Opacity = 0.5;
                }

                if (!string.IsNullOrEmpty(finalMd) && localContainer != null)
                {
                    RenderFullMarkdown(localContainer, finalMd);
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
        //
        //  Design language: GitHub Copilot / Antigravity parity
        //  • User  → right-aligned, rounded, very subtle background tint, NO visible border
        //  • AI    → left-aligned, NO background card, NO border — just text on the panel bg
        //  • Tool  → single-line status chip (icon + italic label, muted accent colour)
        //

        private void AppendUserBubble(string text)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 4, 16) };
            
            var bubble = new Border
            {
                Background      = (Brush)this.Resources["LpGlassBgBrush"],
                BorderBrush     = (Brush)this.Resources["LpGlassBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(14, 14, 2, 14),
                Padding         = new Thickness(14, 10, 14, 10),
                Margin          = new Thickness(60, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Effect          = (System.Windows.Media.Effects.Effect)this.Resources["LpGhostShadow"]
            };

            var body = new TextBlock
            {
                Text            = text,
                TextWrapping    = TextWrapping.Wrap,
                Foreground      = (Brush)this.Resources["LpWindowFgBrush"],
                FontSize        = 12.5,
                FontFamily      = UIFont,
                Opacity         = 0.95
            };

            bubble.Child = body;
            row.Children.Add(bubble);

            MessagesContainer.Children.Add(row);
            ChatScroll.ScrollToEnd();
            
            // Enter animation
            var slide = new System.Windows.Media.Animation.DoubleAnimation(10, 0, new Duration(TimeSpan.FromSeconds(0.4))) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
            row.RenderTransform = new TranslateTransform();
            row.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void ApplySimpleMarkdown(TextBlock tb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            tb.Inlines.Clear();
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(\*\*.*?\*\*)").Where(p => !string.IsNullOrEmpty(p));
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                {
                    tb.Inlines.Add(new Bold(new Run(part.Substring(2, part.Length - 4))));
                }
                else
                {
                    tb.Inlines.Add(new Run(part));
                }
            }
        }

        private StackPanel CreateAIHeader(out TextBlock statusLabelRef, string status = null)
        {
            statusLabelRef = null;
            var labelRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                Margin              = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Minimalist Logo (matching header)
            var logo = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/LocalPilot;component/Assets/Logo_Concept_Minimalist.png")),
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Opacity = 0.8
            };
            labelRow.Children.Add(logo);

            var nameLabel = new TextBlock
            {
                Text              = "LocalPilot",
                FontSize          = 11,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = (Brush)this.Resources["LpMutedFgBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Opacity           = 0.9
            };
            labelRow.Children.Add(nameLabel);

            if (!string.IsNullOrEmpty(status))
            {
                var statusLabel = new TextBlock
                {
                    Text              = $"  ·  {status}",
                    FontSize          = 11,
                    FontStyle         = FontStyles.Italic,
                    Foreground        = (Brush)this.Resources["LpMutedFgBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = 0.6
                };
                labelRow.Children.Add(statusLabel);
                statusLabelRef = statusLabel;
            }

            return labelRow;
        }

        private StackPanel AppendAIBubble(string text)
        {
            // AI message: minimalist flat layout matching VS Code / Antigravity
            var msgContainer = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };

            string status = string.IsNullOrEmpty(text) ? "thinking" : null;
            var labelRow = CreateAIHeader(out var statusLabel, status);
            msgContainer.Children.Add(labelRow);

            var contentArea = new StackPanel();
            contentArea.Tag = statusLabel; // Store reference for later updates
            
            // 🚀 Reset UI Performance tracking for each new bubble
            _lastRenderedMarkdown = "";
            _lastActiveBlockElement = null;
            _activeBlockType = null;

            if (string.IsNullOrEmpty(text))
            {
                msgContainer.Children.Add(contentArea);
                MessagesContainer.Children.Add(msgContainer);
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ChatScroll.ScrollToEnd();
                });
                return contentArea;
            }

            RenderFullMarkdown(contentArea, text);
            msgContainer.Children.Add(contentArea);
            MessagesContainer.Children.Add(msgContainer);

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ChatScroll.ScrollToEnd();
            });

            return contentArea;
        }

        private StackPanel AppendNarrativeBlock()
        {
            // 🛡️ Robustness Check: Ensure we have a turn container to append to
            if (_agentTurnContainer == null)
            {
                StartNewAgentTurn();
            }

            // If still null (unlikely but possible if StartNewAgentTurn failed), bail
            if (_agentTurnContainer == null) return null;

            var container = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            _agentTurnContainer.Children.Add(container);
            _currentNarrativeContainer = container;

            if (_currentNarrativeLabel != null && _currentNarrativeLabel.Visibility != Visibility.Visible)
            {
                _currentNarrativeLabel.Visibility = Visibility.Visible;
            }

            return container;
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
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
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
            Brush hoverBg    = (Brush)this.Resources["LpAccentBrush"];
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

        /// <summary>
        /// 🚀 ULTRA PERFORMANCE: Incremental Markdown Renderer
        /// Instead of clearing and rebuilding, this identifies the change and appends
        /// directly to the active UI element.
        /// </summary>
        private void RenderMarkdownIncremental(StackPanel container, string md)
        {
            if (string.IsNullOrEmpty(md) || md == _lastRenderedMarkdown) return;

            // 1. Calculate the 'Delta' (what's new since last render)
            string delta = md.Length > _lastRenderedMarkdown.Length 
                           ? md.Substring(_lastRenderedMarkdown.Length) 
                           : md;

            // 2. Identify the current block context
            // Heuristic: Are we inside a ``` block or <thought> tag?
            bool inCode = md.LastIndexOf("```") > md.LastIndexOf("```", Math.Max(0, md.LastIndexOf("```") - 1)) && !md.EndsWith("```");
            bool inThought = md.LastIndexOf("<thought") > md.LastIndexOf("</thought>");

            string currentType = inCode ? "code" : (inThought ? "thought" : "text");

            if (currentType != _activeBlockType || container.Children.Count == 0)
            {
                // Edge case: Type changed (e.g. finished code, started text)
                // Force a full re-sync for this turn to ensure block hygiene
                RenderFullMarkdown(container, md);
                _activeBlockType = currentType;
                _lastRenderedMarkdown = md;
                return;
            }

            // 3. Append to existing element
            if (_lastActiveBlockElement is RichTextBox rtb && currentType == "text")
            {
                AppendToRichTextBox(rtb, delta);
            }
            else if (_lastActiveBlockElement is Border border && border.Tag is RichTextBox blockRtb)
            {
                // Handles both code blocks and thought cards
                AppendToRichTextBox(blockRtb, delta);
            }
            else if (_lastActiveBlockElement is Border codeBorder && codeBorder.Tag is TextBlock codeText)
            {
                codeText.Text += delta;
            }
            else
            {
                // Fallback for safety if elements got out of sync
                RenderFullMarkdown(container, md);
            }

            _lastRenderedMarkdown = md;
            
            // Smoothed scrolling
            if ((DateTime.Now.Ticks / 10000 - _lastScrollTime) > 100)
            {
                ChatScroll.ScrollToEnd();
                _lastScrollTime = DateTime.Now.Ticks / 10000;
            }
        }

        private void AppendToRichTextBox(RichTextBox rtb, string text)
        {
            var para = rtb.Document.Blocks.LastBlock as Paragraph;
            if (para == null)
            {
                para = new Paragraph { Margin = new Thickness(0) };
                rtb.Document.Blocks.Add(para);
            }
            para.Inlines.Add(new Run(text));
        }

        private void RenderFullMarkdown(StackPanel container, string md)
        {
            if (container == null || string.IsNullOrEmpty(md)) return;
            container.Children.Clear();
            _lastActiveBlockElement = null; // Reset tracking

            // 🎨 Theme-Aware Resolution
            var accentBrush = (Brush)this.Resources["LpAccentBrush"];
            var mutedBrush = (Brush)this.Resources["LpMutedFgBrush"];

            // 1. PROJECT CONTEXT INDICATOR (v2.0)
            if (md.Contains("--- PROJECT_SOURCE_CONTEXT ---"))
            {
                var indicator = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)),
                    BorderBrush = accentBrush,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 4, 0, 16),
                    CornerRadius = new CornerRadius(2)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = "\uE945", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = accentBrush, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = "PROJECT INTELLIGENCE ACTIVE", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = accentBrush, VerticalAlignment = VerticalAlignment.Center });
                indicator.Child = sp;
                container.Children.Add(indicator);
            }

            // 📸 SHARED REGEX FOR BLOCKS (interleaved)
            var blockRegex = new System.Text.RegularExpressions.Regex(@"(```[\s\S]*?(?:```|$))|(<thought[^>]*>[\s\S]*?(?:</thought>|$))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var matches = blockRegex.Matches(md);
            int lastIndex = 0;

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string textPart = md.Substring(lastIndex, match.Index - lastIndex);
                if (!string.IsNullOrWhiteSpace(textPart))
                {
                    var rtb = CreateRichTextBox();
                    RenderMarkdown(rtb, textPart);
                    container.Children.Add(rtb);
                    _lastActiveBlockElement = rtb;
                }

                string block = match.Value;

                // ── THOUGHT BLOCK ──────────────────────────────────────────
                if (block.StartsWith("<thought", StringComparison.OrdinalIgnoreCase))
                {
                    string content = System.Text.RegularExpressions.Regex.Replace(block, @"^<thought[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    content = System.Text.RegularExpressions.Regex.Replace(content, @"</thought>$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // 🛡️ GLOBAL TURN DEDUPLICATION: Check if this exact thought exists anywhere in the current turn container
                        bool isDuplicate = false;
                        if (_agentTurnContainer != null)
                        {
                            isDuplicate = GetAllChildren(_agentTurnContainer)
                                          .OfType<StackPanel>()
                                          .Any(sp => sp.Tag is RichTextBox rtb && GetRichText(rtb).Trim() == content);
                        }

                        if (!isDuplicate)
                        {
                            var thoughtCard = BuildThoughtCard(content);
                            container.Children.Add(thoughtCard);
                            _lastActiveBlockElement = thoughtCard;
                        }
                    }
                }
                // ── CODE BLOCK ─────────────────────────────────────────────
                else if (block.StartsWith("```"))
                {
                    string rawCode = block.Trim('`', ' ', '\n', '\r');
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
                    
                    // 🛡️ EMPTY BLOCK SUPPRESSION:
                    // If the code block is empty (e.g., the model has only output ``` so far),
                    // don't render the header and grid yet to avoid "Empty Code" flicker.
                    if (string.IsNullOrWhiteSpace(cleanCode)) continue;

                    var codeGrid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
                    codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
                        Width = double.NaN,
                        Height = double.NaN,
                        ToolTip = "Copy code to clipboard",
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(8, 4, 8, 4)
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

                    if ((_currentAction == "refactor" || _currentAction == "fix") && !string.IsNullOrEmpty(_lastAuthoringCode))
                    {
                        var diffBtn = new Button { 
                            Style = (Style)this.FindResource("IconButtonStyle"), 
                            Width = double.NaN,
                            Height = double.NaN,
                            ToolTip = "Preview changes in Side-by-Side Diff",
                            VerticalAlignment = VerticalAlignment.Center,
                            Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        DockPanel.SetDock(diffBtn, Dock.Right);
                        var diffStack = new StackPanel { Orientation = Orientation.Horizontal };
                        diffStack.Children.Add(new TextBlock { Text = "\uEABE", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center });
                        diffStack.Children.Add(new TextBlock { Text = "PREVIEW DIFF", FontSize = 8, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
                        diffBtn.Content = diffStack;
                        diffBtn.Click += (s, e) => { _ = OpenDiffViewAsync(_lastAuthoringCode, cleanCode); };
                        headerStack.Children.Add(diffBtn);

                        var applyBtn = new Button { 
                            Style = (Style)this.FindResource("IconButtonStyle"), 
                            ToolTip = "Apply these changes to your editor",
                            VerticalAlignment = VerticalAlignment.Center,
                            Padding = new Thickness(6, 2, 6, 2),
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        DockPanel.SetDock(applyBtn, Dock.Right);
                        var applyStack = new StackPanel { Orientation = Orientation.Horizontal };
                        applyStack.Children.Add(new TextBlock { Text = "\uE8FB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = accentBrush, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center });
                        applyStack.Children.Add(new TextBlock { Text = "APPLY", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = accentBrush, VerticalAlignment = VerticalAlignment.Center });
                        applyBtn.Content = applyStack;
                        applyBtn.Click += (s, e) => { _ = ApplyRefactoredCodeAsync(cleanCode); };
                        headerStack.Children.Add(applyBtn);
                    }

                    headerBar.Child = headerStack;
                    Grid.SetRow(headerBar, 0);
                    codeGrid.Children.Add(headerBar);

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
                }

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
                    rtb.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0), FontSize = 4 });
                    continue;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

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
                    paragraph.Margin = new Thickness(0, 4, 0, 2);
                }
                // 2. Lists (- * 1.)
                else if (trimmed.StartsWith("-") || trimmed.StartsWith("*") || (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.'))
                {
                    paragraph.Margin = new Thickness(12, 0, 0, 1);
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
                    run.Foreground = (Brush)this.Resources["LpAccentBrush"];
                    run.Background = (Brush)this.Resources["LpHoverBgBrush"];
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
            "throw", "override", "virtual", "abstract", "get", "set", "interface", "enum", "decimal",
            "double", "float", "long", "short", "byte", "object", "sealed", "partial", "readonly",
            "ref", "out", "in", "params", "is", "as", "true", "false", "null", "this", "base",
            "typeof", "sizeof", "lock", "checked", "unchecked", "unsafe", "stackalloc", "fixed",
            "extern", "delegate", "event", "struct", "record", "where", "yield"
        };

        private void HighlightCode(Paragraph p, string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            p.Inlines.Clear();
            
            // 🎨 Theme-Aware Syntax Palette (Dynamic lookup from Resources)
            var brushKw      = (Brush)this.Resources["LpCodeKwBrush"]      ?? new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
            var brushComment = (Brush)this.Resources["LpCodeCommentBrush"] ?? new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            var brushStr     = (Brush)this.Resources["LpCodeStringBrush"]  ?? new SolidColorBrush(Color.FromRgb(0xD6, 0x9D, 0x85));
            var brushNum     = (Brush)this.Resources["LpCodeNumberBrush"]  ?? new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
            var brushType    = (Brush)this.Resources["LpCodeTypeBrush"]    ?? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
            var brushMethod  = (Brush)this.Resources["LpCodeMethodBrush"]  ?? new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
            var brushNormal  = ThemeWindowFg ?? Brushes.White;

            var lines = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            
            // Enhanced Regex for proper tokenization: 
            // Groups: 1=Comment, 2=String, 3=Number, 4=TypePre (Upper), 5=MethodPre (word before '('), 6=Word
            var regex = new System.Text.RegularExpressions.Regex(
                @"(//.*?$)|("".*?""|'.*?')|(\b\d+\b)|(\b[A-Z]\w*\b)|(\b\w+(?=\s*\())|(\b\w+\b)", 
                System.Text.RegularExpressions.RegexOptions.Compiled);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                int lastPos = 0;
                
                var matches = regex.Matches(line);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    // Add plain text between matches (operators, spaces, etc.)
                    if (match.Index > lastPos)
                    {
                        p.Inlines.Add(new Run(line.Substring(lastPos, match.Index - lastPos)) { 
                            Foreground = brushNormal, FontFamily = ConsoleFont, FontSize = 12 
                        });
                    }

                    var token = match.Value;
                    var run = new Run(token) { FontFamily = ConsoleFont, FontSize = 12 };

                    if (match.Groups[1].Success)      run.Foreground = brushComment;
                    else if (match.Groups[2].Success) run.Foreground = brushStr;
                    else if (match.Groups[3].Success) run.Foreground = brushNum;
                    else if (match.Groups[5].Success) run.Foreground = brushMethod;
                    else if (match.Groups[4].Success) run.Foreground = brushType;
                    else if (Keywords.Contains(token)) run.Foreground = brushKw;
                    else run.Foreground = brushNormal;

                    p.Inlines.Add(run);
                    lastPos = match.Index + match.Length;
                }

                if (lastPos < line.Length)
                {
                    p.Inlines.Add(new Run(line.Substring(lastPos)) { 
                        Foreground = brushNormal, FontFamily = ConsoleFont, FontSize = 12 
                    });
                }

                if (i < lines.Length - 1) p.Inlines.Add(new LineBreak());
            }
        }

        private void SetRichText(RichTextBox rtb, string text)
        {
            rtb.Document.Blocks.Clear();
            var para = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
            rtb.Document.Blocks.Add(para);
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

        private void SetStreaming(bool streaming, string modelName = null)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                _isStreaming = streaming;
                string model = modelName ?? LocalPilotSettings.Instance.ChatModel;
                var streamingState = _agentTurnCoordinator.BuildStreamingState(streaming, model);

                _sessionViewModel.IsStreaming = streamingState.IsStreaming;
                _sessionViewModel.IsInputEnabled = streamingState.IsInputEnabled;
                _sessionViewModel.InputOpacity = streamingState.InputOpacity;
                
                if (streaming)
                {
                    AgentStatusBar.Visibility = streamingState.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
                    _sessionViewModel.AgentTurn.StatusText = streamingState.StatusText;
                    _sessionViewModel.AgentTurn.DetailText = streamingState.DetailText;
                    
                    // Change Send icon to Stop (Square)
                    BtnSendIcon.Data = Geometry.Parse("M6,6h12v12H6z"); 
                    BtnSendIcon.Fill = Brushes.White;
                    BtnSend.ToolTip = "Stop (Esc)";
                    BtnSend.Background = (Brush)this.Resources["LpStopBrush"];
                }
                else
                {
                    AgentStatusBar.Visibility = Visibility.Collapsed;
                    
                    // Restore Send icon (Sleeker Plane)
                    BtnSendIcon.Data = Geometry.Parse("M2,21L23,12L2,3V10L17,12L2,14V21Z");
                    BtnSendIcon.Fill = Brushes.White;
                    BtnSend.ToolTip = "Send (Enter)";
                    BtnSend.Background = (Brush)this.Resources["LpAccentBrush"];
                }
                
                // Lock all interaction during streaming to prevent action queuing
                BtnSend.IsEnabled           = true; 
                BtnClear.IsEnabled          = !streaming;
                BtnQuickActions.IsEnabled    = !streaming;

                if (!streaming) TxtInput.Focus();
            });
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
            var capabilities = CapabilityCatalog.All.ToDictionary(c => c.Action, c => c, StringComparer.OrdinalIgnoreCase);

            // In WPF, items in a ContextMenu are not generated as fields for the UserControl.
            // We find them by name or tag to safely toggle visibility.
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi)
                {
                    string action = mi.Tag?.ToString();
                    if (string.IsNullOrWhiteSpace(action))
                    {
                        mi.Visibility = Visibility.Visible;
                        continue;
                    }

                    if (capabilities.TryGetValue(action, out var capability))
                    {
                        mi.Visibility = capability.IsEnabled(s) ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else
                    {
                        mi.Visibility = Visibility.Visible;
                    }
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

        private async Task OpenDiffViewAsync(string leftCode, string rightCode)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                var diffService = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
                if (diffService == null) return;

                // Create temp files for the comparison
                string oldPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LocalPilot_Original.txt");
                string newPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LocalPilot_Refactored.txt");

                System.IO.File.WriteAllText(oldPath, leftCode);
                System.IO.File.WriteAllText(newPath, rightCode);

                diffService.OpenComparisonWindow2(oldPath, newPath, "LocalPilot Refactor: Original vs New", "LocalPilot AI Refactoring", "Original Code", "Improved Code", "Apply AI Refactor", "Close", 0);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("Failed to open Diff View", ex);
            }
        }

        private async Task ApplyRefactoredCodeAsync(string newCode)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                
                // Elite Focus: Ensure the document is active and visible before applying
                if (dte?.ActiveDocument != null)
                {
                    dte.ActiveDocument.Activate();
                    if (dte.ActiveDocument.Selection is EnvDTE.TextSelection sel)
                    {
                        // Lite version of vsInsertFlagsNone = 0
                        sel.Insert(newCode, 0);
                        
                        LocalPilotLogger.Log("[Chat] Successfully applied AI refactor to editor.");
                        AppendAIBubble("Code was successfully updated in your editor.");
                    }
                }
                else
                {
                    AppendAIBubble("**Editor context lost.** I could not find an active document to apply the changes. Click into your editor and try again.");
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("Failed to apply refactored code", ex);
                AppendAIBubble($"❌ Failed to apply changes: {ex.Message}");
            }
        }
        private async Task<bool> OnAgentPermissionRequestedAsync(ToolCallRequest tool)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _permissionTcs = new TaskCompletionSource<bool>();

            ConfirmationPanel.Visibility = Visibility.Visible;
            TxtConfirmDetail.Text = BuildConfirmationMessage(tool);

            // Scroll to bottom so user sees the prompt
            ChatScroll.ScrollToEnd();

            return await _permissionTcs.Task;
        }

        private string BuildConfirmationMessage(ToolCallRequest tool)
        {
            if (tool == null) return "The agent wants to run an action that can modify your project. Do you want to allow it?";

            string GetArg(string key)
            {
                if (tool.Arguments == null) return null;
                if (!tool.Arguments.TryGetValue(key, out var v) || v == null) return null;
                return v.ToString();
            }

            string ShortPath(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                try
                {
                    return System.IO.Path.GetFileName(path);
                }
                catch
                {
                    return path;
                }
            }

            return tool.Name switch
            {
                "write_file" => BuildWriteFileMessage(GetArg("path"), GetArg("content")),
                "replace_text" => BuildReplaceTextMessage(GetArg("path"), GetArg("old_text"), GetArg("new_text")),
                "delete_file" => $"The agent wants to delete the file '{ShortPath(GetArg("path")) ?? "selected file"}'.\n\nThis will permanently remove it from disk. Allow this action?",
                "run_terminal" => BuildRunCommandMessage(GetArg("command")),
                _ => $"The agent wants permission to run '{tool.Name}'.\n\nAllow this action?"
            };
        }

        private string BuildWriteFileMessage(string path, string content)
        {
            string file = string.IsNullOrWhiteSpace(path) ? "a file" : $"'{System.IO.Path.GetFileName(path)}'";
            int length = string.IsNullOrEmpty(content) ? 0 : content.Length;
            string sizeHint = length > 0 ? $" (~{length} characters)" : string.Empty;

            return $"The agent wants to create or update {file}{sizeHint}.\n\nAllow this action?";
        }

        private string BuildReplaceTextMessage(string path, string oldText, string newText)
        {
            string file = string.IsNullOrWhiteSpace(path) ? "a file" : $"'{System.IO.Path.GetFileName(path)}'";
            string oldPreview = BuildPreview(oldText);
            string newPreview = BuildPreview(newText);

            return $"The agent wants to update content in {file}.\n\nFind:\n{oldPreview}\n\nReplace with:\n{newPreview}\n\nAllow this action?";
        }

        private string BuildRunCommandMessage(string command)
        {
            string preview = BuildPreview(command, 120);
            return $"The agent wants to run a terminal command in your workspace:\n{preview}\n\nThis may modify files or run scripts. Allow this action?";
        }

        private string BuildPreview(string text, int maxLen = 180)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(empty)";
            string normalized = text.Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (normalized.Length <= maxLen) return normalized;
            return normalized.Substring(0, maxLen) + "...";
        }
        private IEnumerable<DependencyObject> GetAllChildren(DependencyObject parent)
        {
            if (parent == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendent in GetAllChildren(child))
                {
                    yield return descendent;
                }
            }
        }

        private string GetRichText(RichTextBox rtb)
        {
            if (rtb == null) return string.Empty;
            return new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text;
        }

        private void BtnApprove_Click(object sender, RoutedEventArgs e)
        {
            ConfirmationPanel.Visibility = Visibility.Collapsed;
            _permissionTcs?.TrySetResult(true);
        }

        private void BtnDeny_Click(object sender, RoutedEventArgs e)
        {
            ConfirmationPanel.Visibility = Visibility.Collapsed;
            _permissionTcs?.TrySetResult(false);
        }

        private async Task<bool> HandlePermissionRequestAsync(ToolCallRequest toolCall)
        {
            _permissionTcs = new TaskCompletionSource<bool>();
            
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            
            // Highlight the risky tool
            string actionDescription = $"{toolCall.Name} on {toolCall.Arguments?["TargetFile"] ?? toolCall.Arguments?["path"] ?? "workspace"}";
            TxtConfirmDetail.Text = $"Agent is requesting permission to perform a potentially destructive action:\n\n{actionDescription}\n\nDo you want to allow this?";
            
            ConfirmationPanel.Visibility = Visibility.Visible;
            ChatScroll.ScrollToEnd();

            return await _permissionTcs.Task;
        }
    }
}
