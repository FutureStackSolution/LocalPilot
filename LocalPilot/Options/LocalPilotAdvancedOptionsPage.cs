using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace LocalPilot.Options
{
    /// <summary>
    /// VS DialogPage that hosts the Advanced portion of the Options UI.
    /// </summary>
    [System.ComponentModel.DesignerCategory("")]
    [ComVisible(true)]
    [Guid("5B6C7D8E-9F0A-5B6C-7D8E-9F0A5B6C7D8E")]
    public class LocalPilotAdvancedOptionsPage : UIElementDialogPage
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
                    
                    // Force the Advanced tab to be selected
                    _control.SetSelectedTab(1); 
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
