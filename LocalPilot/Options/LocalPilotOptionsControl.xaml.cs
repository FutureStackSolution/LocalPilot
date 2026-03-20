using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalPilot.Services;
using LocalPilot.Settings;

namespace LocalPilot.Options
{
    public partial class LocalPilotOptionsControl : UserControl
    {
        private readonly OllamaService _ollama = new OllamaService();
        private CancellationTokenSource _cts;

        public LocalPilotOptionsControl()
        {
            InitializeComponent();
            Loaded += async (s, e) => await RefreshConnectionStatusAsync();
        }

        // ── Load settings into all controls ───────────────────────────────────
        public void LoadSettings(LocalPilotSettings s)
        {
            TxtBaseUrl.Text          = s.OllamaBaseUrl;

            // Sliders
            SldrDelay.Value          = s.CompletionDelayMs;
            SldrMaxTokens.Value      = s.MaxCompletionTokens;
            SldrMaxChatTokens.Value  = s.MaxChatTokens;
            SldrTemp.Value           = s.Temperature;

            // Context
            TxtContextBefore.Text   = s.ContextLinesBefore.ToString();
            TxtContextAfter.Text    = s.ContextLinesAfter.ToString();

            // Toggles
            ChkEnableInline.IsChecked = s.EnableInlineCompletion;
            ChkShowGhost.IsChecked    = s.ShowCompletionGhost;
            ChkExplain.IsChecked      = s.EnableExplain;
            ChkRefactor.IsChecked     = s.EnableRefactor;
            ChkDocGen.IsChecked       = s.EnableDocGen;
            ChkReview.IsChecked       = s.EnableReview;
            ChkFix.IsChecked          = s.EnableFix;
            ChkUnitTest.IsChecked     = s.EnableUnitTest;
            ChkEnableChat.IsChecked   = s.EnableChatPanel;
            ChkStatusBar.IsChecked    = s.ShowStatusBar;

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
            _ = LoadModelsAsync(s.OllamaBaseUrl);
        }

        // ── Save settings from controls ───────────────────────────────────────
        public void SaveSettings()
        {
            var s = LocalPilotSettings.Instance;

            s.OllamaBaseUrl         = TxtBaseUrl.Text.Trim();
            s.CompletionDelayMs     = (int)SldrDelay.Value;
            s.MaxCompletionTokens   = (int)SldrMaxTokens.Value;
            s.MaxChatTokens         = (int)SldrMaxChatTokens.Value;
            s.Temperature           = Math.Round(SldrTemp.Value, 2);

            if (int.TryParse(TxtContextBefore.Text, out int cb)) s.ContextLinesBefore = cb;
            if (int.TryParse(TxtContextAfter.Text,  out int ca)) s.ContextLinesAfter  = ca;
            if (int.TryParse(TxtChatHistory.Text,   out int ch)) s.ChatHistoryMaxItems = ch;

            s.EnableInlineCompletion = ChkEnableInline.IsChecked  == true;
            s.ShowCompletionGhost    = ChkShowGhost.IsChecked      == true;
            s.EnableExplain          = ChkExplain.IsChecked        == true;
            s.EnableRefactor         = ChkRefactor.IsChecked       == true;
            s.EnableDocGen           = ChkDocGen.IsChecked         == true;
            s.EnableReview           = ChkReview.IsChecked         == true;
            s.EnableFix              = ChkFix.IsChecked            == true;
            s.EnableUnitTest         = ChkUnitTest.IsChecked       == true;
            s.EnableChatPanel        = ChkEnableChat.IsChecked     == true;
            s.ShowStatusBar          = ChkStatusBar.IsChecked      == true;

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
        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = TxtBaseUrl.Text.Trim();
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    TxtConnectionResult.Text = "✗  URL must start with http:// or https://";
                    TxtConnectionResult.Foreground = Brushes.Red;
                    TxtConnectionResult.Visibility = Visibility.Visible;
                    return;
                }

                _ollama.UpdateBaseUrl(url);
                TxtConnectionResult.Visibility = Visibility.Visible;
                TxtConnectionResult.Text       = "Testing connection…";
                TxtConnectionResult.Foreground = SystemColors.GrayTextBrush;

                bool ok = await _ollama.IsAvailableAsync();
                if (ok)
                {
                    TxtConnectionResult.Text       = "✓  Ollama is running and reachable!";
                    TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                    SetConnectionStatus(true);
                    await LoadModelsAsync(url);
                }
                else
                {
                    TxtConnectionResult.Text       = "✗  Cannot reach Ollama. Check URL and ensure it's running.";
                    TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                    SetConnectionStatus(false);
                }
            }
            catch (Exception ex)
            {
                TxtConnectionResult.Text = $"✗  Error: {ex.Message}";
                TxtConnectionResult.Foreground = Brushes.Red;
            }
        }

        private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadModelsAsync(TxtBaseUrl.Text.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Refresh failed: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings();
                MessageBox.Show("Settings saved!", "LocalPilot", MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "LocalPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        private void SldrDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDelay != null)
                TxtDelay.Text = $"{(int)e.NewValue} ms";
        }

        private void SldrMaxTokens_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMaxTokens != null)
                TxtMaxTokens.Text = $"{(int)e.NewValue}";
        }

        private void SldrTemp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtTemp != null)
                TxtTemp.Text = $"{e.NewValue:F2}";
        }

        private void SldrMaxChatTokens_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMaxChatTokens != null)
                TxtMaxChatTokens.Text = $"{(int)e.NewValue}";
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task LoadModelsAsync(string baseUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.Contains("://"))
                    return;

                _ollama.UpdateBaseUrl(baseUrl);
                var models = await _ollama.GetAvailableModelsAsync();

                Dispatcher.Invoke(() =>
                {
                    PopulateCombo(CmbCompletionModel, models, LocalPilotSettings.Instance.CompletionModel);
                    PopulateCombo(CmbChatModel,        models, LocalPilotSettings.Instance.ChatModel);
                    PopulateCombo(CmbExplainModel,     models, LocalPilotSettings.Instance.ExplainModel);
                    PopulateCombo(CmbRefactorModel,    models, LocalPilotSettings.Instance.RefactorModel);
                    PopulateCombo(CmbDocModel,         models, LocalPilotSettings.Instance.DocModel);
                    PopulateCombo(CmbReviewModel,      models, LocalPilotSettings.Instance.ReviewModel);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalPilot] LoadModelsAsync failed: {ex.Message}");
            }
        }

        private void PopulateCombo(ComboBox cmb, List<string> models, string selected)
        {
            cmb.Items.Clear();
            foreach (var m in models)
            {
                var item = new ComboBoxItem { Content = m };
                cmb.Items.Add(item);
                if (m.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    cmb.SelectedItem = item;
            }
            if (cmb.SelectedItem == null && cmb.Items.Count > 0)
                cmb.SelectedIndex = 0;
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
            Dispatcher.Invoke(() => SetConnectionStatus(ok));
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
