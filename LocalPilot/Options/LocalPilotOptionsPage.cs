using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace LocalPilot.Options
{
    /// <summary>
    /// VS DialogPage that hosts the WPF Options UI.
    /// </summary>
    [System.ComponentModel.DesignerCategory("")]
    [ComVisible(true)]
    [Guid("4A5B6C7D-8E9F-4A5B-6C7D-8E9F4A5B6C7D")]
    public class LocalPilotOptionsPage : UIElementDialogPage
    {
        private LocalPilotOptionsControl _control;

        protected override UIElement Child
        {
            get
            {
                if (_control == null)
                {
                    _control = new LocalPilotOptionsControl();
                    _control.LoadSettings(LocalPilotSettings.Instance);
                }
                return _control;
            }
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            _control?.SaveSettings();
            base.OnApply(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _control?.SaveSettings();
            base.OnClosed(e);
        }

        public override void ResetSettings()
        {
            LocalPilotSettings.UpdateInstance(new LocalPilotSettings());
            _control?.LoadSettings(LocalPilotSettings.Instance);
        }
    }
}
