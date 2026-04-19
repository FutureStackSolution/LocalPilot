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
        private readonly OllamaService _ollama = new OllamaService();
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public LocalPilotOptionsControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await InitializeInternalAsync();
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
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
            TxtBaseUrl.Text          = s.OllamaBaseUrl;

            
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
            ChkStatusBar.IsChecked    = s.ShowStatusBar;
            ChkEnableLogging.IsChecked = s.EnableLogging;

            ChkEnableProjectMap.IsChecked = s.EnableProjectMap;

            TxtChatHistory.Text       = s.ChatHistoryMaxItems.ToString();

            // Populate model combos with current value; 
            // full list populated after async fetch
            SetComboItem(CmbCompletionModel, s.CompletionModel);
            SetComboItem(CmbChatModel,        s.ChatModel);
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
                await LoadModelsAsync(s.OllamaBaseUrl, token);
            });
        }

        // ── Save settings from controls ───────────────────────────────────────
        public void SaveSettings()
        {
            var s = LocalPilotSettings.Instance;

            s.OllamaBaseUrl         = TxtBaseUrl.Text.Trim();
            s.Mode                  = (PerformanceMode)CmbPerformanceMode.SelectedIndex;
            s.EnableProjectMap      = ChkEnableProjectMap.IsChecked == true;

            if (int.TryParse(TxtChatHistory.Text,   out int ch)) s.ChatHistoryMaxItems = ch;

            s.EnableInlineCompletion = ChkEnableInline.IsChecked  == true;
            s.ShowCompletionGhost    = ChkShowGhost.IsChecked      == true;
            s.EnableExplain          = ChkExplain.IsChecked        == true;
            s.EnableRefactor         = ChkRefactor.IsChecked       == true;
            s.EnableDocGen           = ChkDocGen.IsChecked         == true;
            s.EnableReview           = ChkReview.IsChecked         == true;
            s.EnableFix              = ChkFix.IsChecked            == true;
            s.EnableUnitTest         = ChkUnitTest.IsChecked       == true;
            s.ShowStatusBar          = ChkStatusBar.IsChecked      == true;
            s.EnableLogging          = ChkEnableLogging.IsChecked  == true;


            s.CompletionModel = (CmbCompletionModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbCompletionModel.Text;
            s.ChatModel       = (CmbChatModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbChatModel.Text;
            s.ExplainModel    = (CmbExplainModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbExplainModel.Text;
            s.RefactorModel   = (CmbRefactorModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbRefactorModel.Text;
            s.DocModel        = (CmbDocModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbDocModel.Text;
            s.ReviewModel     = (CmbReviewModel.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                ?? CmbReviewModel.Text;

            // Persist to disk
            SettingsPersistence.Save(s);
        }

        // ── Event Handlers ────────────────────────────────────────────────────
        private void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
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
                    TxtConnectionResult.Text = "Checking Ollama status...";
                    TxtConnectionResult.Foreground = (Brush)this.Resources["LpMutedFgBrush"];
                    TxtConnectionResult.Visibility = Visibility.Visible;

                    _ollama.UpdateBaseUrl(url);
                    bool ok = await _ollama.IsAvailableAsync();

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    
                    if (ok)
                    {
                        TxtConnectionResult.Text = "✓  Ollama is running and reachable!";
                        TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                        SetConnectionStatus(true);
                        
                        // Try to reload models immediately if connected
                        await LoadModelsAsync(url, _cts.Token);

                        MessageBox.Show("Successfully connected to Ollama!", "LocalPilot",
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        TxtConnectionResult.Text = "✗  Cannot reach Ollama. Ensure it's running at the specified URL.";
                        TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                        SetConnectionStatus(false);
                        
                        MessageBox.Show("Failed to connect to Ollama. Please verify the service is running.", "Connection Failed",
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
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await LoadModelsAsync(TxtBaseUrl.Text.Trim(), _cts.Token);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Refresh failed: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    SaveSettings();
                    await ShowToastAsync(success: true,
                        title: "Settings saved",
                        subtitle: "All changes have been persisted.");
                }
                catch (Exception ex)
                {
                    await ShowToastAsync(success: false,
                        title: "Save failed",
                        subtitle: ex.Message);
                }
            });
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Reset all settings to defaults?", "LocalPilot",
                                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                LocalPilotSettings.UpdateInstance(new LocalPilotSettings());
                LoadSettings(LocalPilotSettings.Instance);
            }
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
        private async Task ShowToastAsync(bool success, string title, string subtitle)
        {
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

            // Wait for the total animation duration (2.75 s) then hide
            await Task.Delay(2800);

            if (SaveToast.Opacity <= 0.05)          // only collapse if not re-triggered
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
            if (CmbPerformanceMode == null || CmbPerformanceMode.SelectedIndex == -1) return;
            
            var selectedMode = (PerformanceMode)CmbPerformanceMode.SelectedIndex;
            var s = LocalPilotSettings.Instance;
            s.Mode = selectedMode; 
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task LoadModelsAsync(string baseUrl, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.Contains("://"))
                    return;

                _ollama.UpdateBaseUrl(baseUrl);
                var models = await _ollama.GetAvailableModelsAsync(ct);

                if (ct.IsCancellationRequested) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                PopulateCombo(CmbCompletionModel, models, LocalPilotSettings.Instance.CompletionModel);
                PopulateCombo(CmbChatModel, models, LocalPilotSettings.Instance.ChatModel);
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

            // Fallback: If the selected model isn't in the list, 
            // pick the first available one so the user can start immediately.
            if (cmb.SelectedItem == null && cmb.Items.Count > 0)
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
            bool ok = await _ollama.IsAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetConnectionStatus(ok);
        }

        private void SetConnectionStatus(bool connected)
        {
            StatusDot.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusText.Text = connected ? "Ollama Connected" : "Ollama Offline";
        }
    }
}
