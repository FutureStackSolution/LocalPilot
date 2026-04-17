using Community.VisualStudio.Toolkit;
using LocalPilot.Chat;
using LocalPilot.Settings;
using LocalPilot.Services;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading.Tasks;

namespace LocalPilot.Commands
{
    /// <summary>
    /// All menu commands registered by LocalPilot.
    /// Commands map to IDs in the .vsct file.
    /// </summary>
    internal sealed class LocalPilotCommands
    {
        // ── GUIDs (must match .vsct) ──────────────────────────────────────────
        public static readonly Guid CommandSetGuid = new Guid("BA6A4123-D789-4B52-A9F0-C1D2E3F4B5A6");

        public const int CmdIdOpenChat        = 0x0100;
        public const int CmdIdExplainCode     = 0x0101;
        public const int CmdIdRefactorCode    = 0x0102;
        public const int CmdIdGenerateDoc     = 0x0103;
        public const int CmdIdReviewCode      = 0x0104;
        public const int CmdIdFixCode         = 0x0105;
        public const int CmdIdGenerateTest    = 0x0106;
        public const int CmdIdOpenOptions     = 0x0107;
        public const int CmdIdInlineChat      = 0x0108;

        private readonly AsyncPackage _package;

        private LocalPilotCommands(AsyncPackage package) => _package = package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var cmdService = await package.GetServiceAsync(typeof(IMenuCommandService))
                             as OleMenuCommandService;
            if (cmdService == null) return;

            var instance = new LocalPilotCommands(package);

            // Tools menu items (Visibility toggled by settings)
            Register(cmdService, CmdIdOpenChat,     instance.OpenChat);
            Register(cmdService, CmdIdOpenOptions,  instance.OpenOptions);

            // Context menu items (Visibility toggled by settings)
            Register(cmdService, CmdIdExplainCode,  instance.ExplainCode,  () => LocalPilotSettings.Instance.EnableExplain);
            Register(cmdService, CmdIdRefactorCode, instance.RefactorCode, () => LocalPilotSettings.Instance.EnableRefactor);
            Register(cmdService, CmdIdGenerateDoc,  instance.GenerateDoc,  () => LocalPilotSettings.Instance.EnableDocGen);
            Register(cmdService, CmdIdReviewCode,   instance.ReviewCode,   () => LocalPilotSettings.Instance.EnableReview);
            Register(cmdService, CmdIdFixCode,      instance.FixCode,      () => LocalPilotSettings.Instance.EnableFix);
            Register(cmdService, CmdIdGenerateTest, instance.GenerateTest, () => LocalPilotSettings.Instance.EnableUnitTest);
            Register(cmdService, CmdIdInlineChat,   instance.OpenInlineChat);
        }

        private static void Register(IMenuCommandService svc, int id, EventHandler handler, Func<bool> isVisible = null)
        {
            var cmdId = new CommandID(CommandSetGuid, id);
            var cmd   = new OleMenuCommand(handler, cmdId);

            if (isVisible != null)
            {
                cmd.BeforeQueryStatus += (s, e) =>
                {
                    var c = (OleMenuCommand)s;
                    c.Visible = isVisible();
                    c.Enabled = c.Visible;
                };
            }

            svc.AddCommand(cmd);
        }

        // ── Command handlers ──────────────────────────────────────────────────
        private void OpenChat(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await LocalPilotCommandRouter.Instance.OpenChatAsync();
            });
        }

        private void ExplainCode(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdExplainCode);
        private void RefactorCode(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdRefactorCode);
        private void GenerateDoc(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdGenerateDoc);
        private void ReviewCode(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdReviewCode);
        private void FixCode(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdFixCode);
        private void GenerateTest(object sender, EventArgs e) => _ = OpenChatWithCommandCapabilityAsync(CmdIdGenerateTest);

        private void OpenInlineChat(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = InlineChatOverlayManager.Instance.ShowAsync();
        }

        private void OpenOptions(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ExecuteCommand("Tools.Options", "LocalPilot.General");
            });
        }

        private Task OpenChatWithCommandCapabilityAsync(int commandId)
        {
            var capability = CapabilityCatalog.FromCommandId(commandId);
            if (capability == null || !capability.IsEnabled(LocalPilotSettings.Instance))
            {
                return Task.CompletedTask;
            }

            return LocalPilotCommandRouter.Instance.ExecuteQuickActionAsync(capability.Action);
        }
    }
}
