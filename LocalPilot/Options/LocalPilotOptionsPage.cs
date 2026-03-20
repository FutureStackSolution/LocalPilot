using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using LocalPilot.Settings;
using LocalPilot.Services;

namespace LocalPilot.Options
{
    /// <summary>
    /// VS DialogPage that hosts the WPF Options UI.
    /// </summary>
    [System.ComponentModel.DesignerCategory("")]
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
