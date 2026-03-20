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
            _ollama.UpdateBaseUrl(TxtBaseUrl.Text.Trim());
            TxtConnectionResult.Visibility = Visibility.Visible;
            TxtConnectionResult.Text       = "Testing connection…";
            TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA));

            bool ok = await _ollama.IsAvailableAsync();
            if (ok)
            {
                TxtConnectionResult.Text       = "✓  Ollama is running and reachable!";
                TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                SetConnectionStatus(true);
                await LoadModelsAsync(TxtBaseUrl.Text.Trim());
            }
            else
            {
                TxtConnectionResult.Text       = "✗  Cannot reach Ollama. Make sure 'ollama serve' is running.";
                TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                SetConnectionStatus(false);
            }
        }

        private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
        {
            await LoadModelsAsync(TxtBaseUrl.Text.Trim());
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            MessageBox.Show("Settings saved!", "LocalPilot", MessageBoxButton.OK,
                            MessageBoxImage.Information);
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
            => TxtDelay.Text = $"{(int)e.NewValue} ms";

        private void SldrMaxTokens_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => TxtMaxTokens.Text = $"{(int)e.NewValue}";

        private void SldrTemp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => TxtTemp.Text = $"{e.NewValue:F2}";

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task LoadModelsAsync(string baseUrl)
        {
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
