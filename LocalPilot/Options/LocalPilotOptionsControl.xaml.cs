using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalPilot.Options
{
    public partial class LocalPilotOptionsControl : UserControl
    {
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isSaving = false;

        private ILlmService GetActiveService()
        {
            var provider = (LlmProviderType)(CmbProviderType?.SelectedIndex == 1 ? 1 : 0);
            string url = TxtBaseUrl?.Text?.Trim() ?? "";
            string apiKey = TxtApiKey?.Password?.Trim() ?? "";
            return LlmProviderFactory.CreateService(provider, url, apiKey);
        }

        public LocalPilotOptionsControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_cts == null)
            {
                _cts = new CancellationTokenSource();
            }
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await InitializeInternalAsync();
            });
        }

        /// <summary>
        /// Programmatically switch to a specific tab (0=General, 1=Advanced)
        /// </summary>
        public void SetSelectedTab(int index)
        {
            if (MainTabControl != null && index >= 0 && index < MainTabControl.Items.Count)
            {
                MainTabControl.SelectedIndex = index;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        private void UpdateBrushes() { }

        private async Task InitializeInternalAsync()
        {
            await RefreshConnectionStatusAsync();
        }

        private void SetResourceBrush(string key, object vsKey)
        {
            var brush = Application.Current.FindResource(vsKey) as Brush;
            if (brush != null)
            {
                this.Resources[key] = brush;
            }
        }

        // ── Load settings into all controls ───────────────────────────────────
        public void LoadSettings(LocalPilotSettings s)
        {
            if (CmbProviderType != null)
            {
                CmbProviderType.SelectedIndex = (int)s.ProviderType;
            }

            if (s.ProviderType == LlmProviderType.OpenAiCompatible)
            {
                TxtBaseUrl.Text = string.IsNullOrEmpty(s.OpenAiBaseUrl) ? "https://api.openai.com/v1" : s.OpenAiBaseUrl;
                if (LblBaseUrl != null) LblBaseUrl.Text = "Endpoint Base URL";
                if (PnlApiKey != null) PnlApiKey.Visibility = Visibility.Visible;
            }
            else
            {
                TxtBaseUrl.Text = string.IsNullOrEmpty(s.OllamaBaseUrl) ? "http://localhost:11434" : s.OllamaBaseUrl;
                if (LblBaseUrl != null) LblBaseUrl.Text = "Ollama Base URL";
                if (PnlApiKey != null) PnlApiKey.Visibility = Visibility.Collapsed;
            }
            if (TxtApiKey != null) TxtApiKey.Password = s.OpenAiApiKey ?? "";
            
            // Intelligence Mode
            CmbPerformanceMode.SelectedIndex = (int)s.Mode;

            // Toggles
            ChkEnableInline.IsChecked = s.EnableInlineCompletion;
            ChkShowGhost.IsChecked    = s.ShowCompletionGhost;
            ChkExplain.IsChecked      = s.EnableExplain;
            ChkRefactor.IsChecked     = s.EnableRefactor;
            ChkDocGen.IsChecked       = s.EnableDocGen;
            ChkReview.IsChecked       = s.EnableReview;
            ChkFix.IsChecked          = s.EnableFix;
            ChkUnitTest.IsChecked     = s.EnableUnitTest;
            ChkAutoFixOnBuild.IsChecked = s.EnableAutoFixOnBuildFailure;
            ChkStatusBar.IsChecked    = s.ShowStatusBar;
            ChkEnableLogging.IsChecked = s.EnableLogging;

            ChkEnableProjectMap.IsChecked = s.EnableProjectMap;
            SldConcurrency.Value = s.BackgroundIndexingConcurrency;

            TxtChatHistory.Text       = s.ChatHistoryMaxItems.ToString();
            TxtNumCtx.Text            = s.ContextWindowSize.ToString();
            TxtNumPredict.Text        = s.MaxOutputTokens.ToString();
            TxtRequestTimeout.Text    = s.RequestTimeoutSeconds.ToString();
            TxtKeepAlive.Text         = string.IsNullOrWhiteSpace(s.KeepAliveDuration) ? "15m" : s.KeepAliveDuration;

            // Populate model combos with current value; 
            // full list populated after async fetch
            SetComboItem(CmbCompletionModel, s.CompletionModel);
            SetComboItem(CmbChatModel,        s.ChatModel);
            SetComboItem(CmbEmbeddingModel,   s.EmbeddingModel);
            SetComboItem(CmbExplainModel,     s.ExplainModel);
            SetComboItem(CmbRefactorModel,    s.RefactorModel);
            SetComboItem(CmbDocModel,         s.DocModel);
            SetComboItem(CmbReviewModel,      s.ReviewModel);

            // Kick off background model fetch
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await LoadModelsAsync(GetActiveService(), token);
            });
        }

        private void CmbProviderType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProviderType == null || TxtBaseUrl == null) return;

            bool isOpenAi = CmbProviderType.SelectedIndex == 1;
            if (LblBaseUrl != null)
            {
                LblBaseUrl.Text = isOpenAi ? "Endpoint Base URL" : "Ollama Base URL";
            }
            if (PnlApiKey != null)
            {
                PnlApiKey.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
            }

            var s = LocalPilotSettings.Instance;
            if (isOpenAi)
            {
                if (string.IsNullOrWhiteSpace(TxtBaseUrl.Text) || TxtBaseUrl.Text.Contains("11434"))
                {
                    TxtBaseUrl.Text = string.IsNullOrEmpty(s.OpenAiBaseUrl) ? "https://api.openai.com/v1" : s.OpenAiBaseUrl;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TxtBaseUrl.Text) || TxtBaseUrl.Text.Contains("openai.com"))
                {
                    TxtBaseUrl.Text = string.IsNullOrEmpty(s.OllamaBaseUrl) ? "http://localhost:11434" : s.OllamaBaseUrl;
                }
            }
        }

        // ── Save settings from controls ───────────────────────────────────────
        public void SaveSettings()
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (MainTabControl == null) return;

                var s = LocalPilotSettings.Instance;

                // Save all fields from both tabs (both tabs are populated by LoadSettings)
                {
                    if (CmbProviderType != null)
                    {
                        s.ProviderType = (LlmProviderType)(CmbProviderType.SelectedIndex == 1 ? 1 : 0);
                    }
                    if (TxtBaseUrl != null)
                    {
                        if (s.ProviderType == LlmProviderType.OpenAiCompatible)
                            s.OpenAiBaseUrl = TxtBaseUrl.Text.Trim();
                        else
                            s.OllamaBaseUrl = TxtBaseUrl.Text.Trim();
                    }
                    if (TxtApiKey != null)
                    {
                        s.OpenAiApiKey = TxtApiKey.Password?.Trim() ?? "";
                    }
                    
                    s.EnableInlineCompletion = ChkEnableInline?.IsChecked == true;
                    s.ShowCompletionGhost    = ChkShowGhost?.IsChecked    == true;
                    s.EnableExplain          = ChkExplain?.IsChecked      == true;
                    s.EnableRefactor         = ChkRefactor?.IsChecked     == true;
                    s.EnableDocGen           = ChkDocGen?.IsChecked       == true;
                    s.EnableReview           = ChkReview?.IsChecked       == true;
                    s.EnableFix              = ChkFix?.IsChecked          == true;
                    s.EnableUnitTest         = ChkUnitTest?.IsChecked     == true;
                    s.EnableAutoFixOnBuildFailure = ChkAutoFixOnBuild?.IsChecked == true;
                    s.ShowStatusBar          = ChkStatusBar?.IsChecked    == true;
                    s.EnableLogging          = ChkEnableLogging?.IsChecked == true;

                    if (CmbCompletionModel != null) s.CompletionModel = (CmbCompletionModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbCompletionModel.Text;
                    if (CmbChatModel != null)       s.ChatModel       = (CmbChatModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbChatModel.Text;
                    if (CmbEmbeddingModel != null)  s.EmbeddingModel  = (CmbEmbeddingModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbEmbeddingModel.Text;
                    if (CmbExplainModel != null)    s.ExplainModel    = (CmbExplainModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbExplainModel.Text;
                    if (CmbRefactorModel != null)   s.RefactorModel   = (CmbRefactorModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbRefactorModel.Text;
                    if (CmbDocModel != null)        s.DocModel        = (CmbDocModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbDocModel.Text;
                    if (CmbReviewModel != null)     s.ReviewModel     = (CmbReviewModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbReviewModel.Text;
                }
                {
                    if (CmbPerformanceMode != null && CmbPerformanceMode.SelectedIndex >= 0) s.Mode = (PerformanceMode)CmbPerformanceMode.SelectedIndex;
                    if (ChkEnableProjectMap != null) s.EnableProjectMap = ChkEnableProjectMap.IsChecked == true;
                    if (SldConcurrency != null) s.BackgroundIndexingConcurrency = (int)SldConcurrency.Value;
                    
                    if (TxtChatHistory != null && int.TryParse(TxtChatHistory.Text, out int ch)) s.ChatHistoryMaxItems = ch;
                    if (TxtNumCtx != null && int.TryParse(TxtNumCtx.Text, out int nc) && nc >= 512) s.ContextWindowSize = nc;
                    if (TxtNumPredict != null && int.TryParse(TxtNumPredict.Text, out int np) && np >= 128) s.MaxOutputTokens = np;
                    if (TxtRequestTimeout != null && int.TryParse(TxtRequestTimeout.Text, out int rt) && rt >= 0) s.RequestTimeoutSeconds = rt;
                    if (TxtKeepAlive != null)
                    {
                        string val = TxtKeepAlive.Text.Trim();
                        s.KeepAliveDuration = string.IsNullOrWhiteSpace(val) ? "15m" : val;
                    }
                }

                // Persist to disk
                SettingsPersistence.Save(s);
            });
        }

        private void BtnUnloadModels_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    BtnUnloadModels.IsEnabled = false;
                    BtnUnloadModels.Content = "Unloading...";
                    TxtUnloadStatus.Text = "Sending unload signal to Ollama...";
                    TxtUnloadStatus.Foreground = (Brush)this.Resources["LpMutedFgBrush"];
                    TxtUnloadStatus.Visibility = Visibility.Visible;

                    var service = GetActiveService();
                    int count = await service.UnloadAllModelsAsync();

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TxtUnloadStatus.Text = count > 0 
                        ? $"✓ Unloaded {count} model(s) from VRAM." 
                        : "✓ No active models were loaded in VRAM.";
                    TxtUnloadStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TxtUnloadStatus.Text = $"✗ Failed to unload: {ex.Message}";
                    TxtUnloadStatus.Foreground = Brushes.Red;
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    BtnUnloadModels.IsEnabled = true;
                    BtnUnloadModels.Content = "⚡  Unload Models from VRAM";
                }
            });
        }

        private void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null) _cts = new CancellationTokenSource();
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    
                    string url = TxtBaseUrl.Text.Trim();
                    if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        TxtConnectionResult.Text = "✗  URL must start with http:// or https://";
                        TxtConnectionResult.Foreground = Brushes.Red;
                        TxtConnectionResult.Visibility = Visibility.Visible;
                        return;
                    }

                    // Busy State
                    BtnTestConnection.IsEnabled = false;
                    BtnTestConnection.Content = "Testing...";
                    TxtConnectionResult.Text = "Checking connection status...";
                    TxtConnectionResult.Foreground = (Brush)this.Resources["LpMutedFgBrush"];
                    TxtConnectionResult.Visibility = Visibility.Visible;

                    var service = GetActiveService();
                    bool ok = await service.IsAvailableAsync();

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    
                    string providerName = CmbProviderType?.SelectedIndex == 1 ? "OpenAI-compatible endpoint" : "Ollama";
                    if (ok)
                    {
                        TxtConnectionResult.Text = $"✓  {providerName} is running and reachable!";
                        TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                        SetConnectionStatus(true);
                        
                        // Try to reload models immediately if connected
                        if (_cts == null) _cts = new CancellationTokenSource();
                        await LoadModelsAsync(service, _cts.Token);

                        MessageBox.Show($"Successfully connected to {providerName}!", "LocalPilot",
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        TxtConnectionResult.Text = $"✗  Cannot reach {providerName}. Ensure it's running and credentials are valid.";
                        TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                        SetConnectionStatus(false);
                        
                        MessageBox.Show($"Failed to connect to {providerName}. Please verify the URL and credentials.", "Connection Failed",
                                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    TxtConnectionResult.Text = $"✗  Error: {ex.Message}";
                    TxtConnectionResult.Foreground = Brushes.Red;
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    BtnTestConnection.IsEnabled = true;
                    BtnTestConnection.Content = "Test Connection";
                }
            });
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null) _cts = new CancellationTokenSource();
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var service = GetActiveService();
                    await LoadModelsAsync(service, _cts.Token);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Refresh failed: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaving) return;
            _isSaving = true;
            BtnSave.IsEnabled = false;

            if (_cts == null) _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    SaveSettings();
                    await ShowToastAsync(success: true,
                        title: "Settings saved",
                        subtitle: "All changes have been persisted.",
                        token: token);
                }
                catch (Exception ex)
                {
                    await ShowToastAsync(success: false,
                        title: "Save failed",
                        subtitle: ex.Message,
                        token: token);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _isSaving = false;
                    BtnSave.IsEnabled = true;
                }
            });
        }



        private void BtnDismissToast_Click(object sender, RoutedEventArgs e)
        {
            SaveToast.Visibility = Visibility.Collapsed;
            SaveToast.Opacity    = 0;
        }



        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = LocalPilotLogger.GetLogPath();
                if (System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start("notepad.exe", path); // Or just process.start(path)
                }
                else
                {
                    // Create an empty file so it opens
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(path, $"--- LocalPilot Log Initialized {DateTime.Now} ---" + Environment.NewLine);
                    System.Diagnostics.Process.Start("notepad.exe", path);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open log: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenPrompts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = PromptLoader.GetPromptsDirectoryPath();
                if (System.IO.Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", path);
                }
                else
                {
                    MessageBox.Show("Prompts directory not found. Try restarting Visual Studio.", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open directory: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Shows the inline toast banner with a fade-in → hold → fade-out animation,
        /// then collapses it. Non-blocking — awaits only a Task.Delay.
        /// </summary>
        private async Task ShowToastAsync(bool success, string title, string subtitle, CancellationToken token = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (SaveToast == null || ToastTitle == null || ToastSubtitle == null) return;

            // Configure appearance
            ToastTitle.Text    = title;
            ToastSubtitle.Text = subtitle;

            SaveToast.BorderBrush = success
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))   // teal  ✓
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));   // red   ✗

            // Swap the tick icon colour
            var iconBlock = FindToastIcon();
            if (iconBlock != null)
            {
                iconBlock.Text       = success ? "\uE73E" : "\uEA39";      // check / error
                iconBlock.Foreground = success
                    ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                    : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            }

            // Make visible and start XAML storyboard
            SaveToast.Opacity    = 0;
            SaveToast.Visibility = Visibility.Visible;

            // Updated Icon Border Background
            if (iconBlock?.Parent is Border b)
            {
                b.Background = success 
                    ? new SolidColorBrush(Color.FromArgb(0x1A, 0x4E, 0xC9, 0xB0))
                    : new SolidColorBrush(Color.FromArgb(0x1A, 0xF4, 0x47, 0x47));
            }

            var sb = SaveToast.Resources["ToastStoryboard"] as System.Windows.Media.Animation.Storyboard;
            sb?.Begin(SaveToast);

            try 
            {
                // Wait for the total animation duration (2.75 s) then hide
                await Task.Delay(2800, token);
            }
            catch (TaskCanceledException) { return; }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (SaveToast != null && SaveToast.Opacity <= 0.05)          // only collapse if not re-triggered
                SaveToast.Visibility = Visibility.Collapsed;
        }

        private TextBlock FindToastIcon()
        {
            // The icon TextBlock is nested inside the first Border child of the Grid
            if (SaveToast.Child is System.Windows.Controls.Grid grid &&
                grid.Children.Count > 0 &&
                grid.Children[0] is Border iconBorder &&
                iconBorder.Child is TextBlock tb)
                return tb;
            return null;
        }




        private void CmbPerformanceMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Mode is applied and persisted when the user confirms via SaveSettings()
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task LoadModelsAsync(ILlmService service, CancellationToken ct)
        {
            try
            {
                if (service == null) return;

                var models = await service.GetAvailableModelsAsync(ct);

                if (ct.IsCancellationRequested) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                PopulateCombo(CmbCompletionModel, models, LocalPilotSettings.Instance.CompletionModel);
                PopulateCombo(CmbChatModel, models, LocalPilotSettings.Instance.ChatModel);
                PopulateCombo(CmbEmbeddingModel, models, LocalPilotSettings.Instance.EmbeddingModel);
                PopulateCombo(CmbExplainModel, models, LocalPilotSettings.Instance.ExplainModel);
                PopulateCombo(CmbRefactorModel, models, LocalPilotSettings.Instance.RefactorModel);
                PopulateCombo(CmbDocModel, models, LocalPilotSettings.Instance.DocModel);
                PopulateCombo(CmbReviewModel, models, LocalPilotSettings.Instance.ReviewModel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalPilot] LoadModelsAsync failed: {ex.Message}");
            }
        }

        private void PopulateCombo(ComboBox cmb, List<string> models, string selected)
        {
            cmb.Items.Clear();

            if (models == null || models.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = "(No models found)", IsEnabled = false });
                cmb.SelectedIndex = 0;
                return;
            }

            foreach (var m in models)
            {
                var item = new ComboBoxItem { Content = m };
                cmb.Items.Add(item);

                // Exact match or contains (handles cases where ollama adds :latest etc)
                if (m.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    cmb.SelectedItem = item;
            }

            // Fallback: If the selected model isn't in the list, keep it as custom item
            // or pick the first available one so the user can start immediately.
            if (cmb.SelectedItem == null && !string.IsNullOrWhiteSpace(selected))
            {
                var customItem = new ComboBoxItem { Content = selected };
                cmb.Items.Insert(0, customItem);
                cmb.SelectedItem = customItem;
            }
            else if (cmb.SelectedItem == null && cmb.Items.Count > 0)
            {
                cmb.SelectedIndex = 0;
            }
        }

        private void SetComboItem(ComboBox cmb, string value)
        {
            cmb.Items.Clear();
            var item = new ComboBoxItem { Content = value };
            cmb.Items.Add(item);
            cmb.SelectedIndex = 0;
        }

        private async Task RefreshConnectionStatusAsync()
        {
            var service = GetActiveService();
            bool ok = await service.IsAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetConnectionStatus(ok);
        }

        private void SetConnectionStatus(bool connected)
        {
            StatusDot.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            string name = CmbProviderType?.SelectedIndex == 1 ? "OpenAI" : "Ollama";
            StatusText.Text = connected ? $"{name} Connected" : $"{name} Offline";
        }
    }
}
