using System;
using System.Windows;
using System.Windows.Input;

namespace LocalPilot.UI
{
    public partial class InlineChatOverlay : Window
    {
        public string Result { get; private set; }
        public bool IsCancelled { get; private set; } = true;

        public InlineChatOverlay()
        {
            InitializeComponent();
            InputBox.TextChanged += (s, e) => {
                Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            
            this.Deactivated += (s, e) => {
                if (!IsCancelled) return;
                Close();
            };
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Result = InputBox.Text;
                IsCancelled = false;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                IsCancelled = true;
                Close();
            }
        }

        public new void Show()
        {
            base.Show();
            InputBox.Focus();
        }
    }
}
