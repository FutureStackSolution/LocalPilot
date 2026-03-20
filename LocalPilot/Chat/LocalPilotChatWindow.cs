using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace LocalPilot.Chat
{
    /// <summary>
    /// LocalPilot Chat Tool Window — dockable panel similar to GitHub Copilot Chat.
    /// </summary>
    [Guid(WindowGuidString)]
    public class LocalPilotChatWindow : ToolWindowPane
    {
        public const string WindowGuidString = "D7A8B1C2-E3F4-4D5E-B6C7-D8E9F0A1B2C3";

        public LocalPilotChatWindow() : base(null)
        {
            Caption = "LocalPilot Chat";
            Content = new LocalPilotChatControl();
        }
    }
}
