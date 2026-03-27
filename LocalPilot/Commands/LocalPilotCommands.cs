using LocalPilot.Chat;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
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
        public static readonly Guid CommandSetGuid = new Guid("E1F2A3B4-C5D6-47E8-F9A0-B1C2D3E4F5A6");

        public const int CmdIdOpenChat        = 0x0100;
        public const int CmdIdExplainCode     = 0x0101;
        public const int CmdIdRefactorCode    = 0x0102;
        public const int CmdIdGenerateDoc     = 0x0103;
        public const int CmdIdReviewCode      = 0x0104;
        public const int CmdIdFixCode         = 0x0105;
        public const int CmdIdGenerateTest    = 0x0106;
        public const int CmdIdOpenOptions     = 0x0107;

        private readonly AsyncPackage _package;

        private LocalPilotCommands(AsyncPackage package) => _package = package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var cmdService = await package.GetServiceAsync(typeof(IMenuCommandService))
                             as IMenuCommandService;
            if (cmdService == null) return;

            var instance = new LocalPilotCommands(package);

            Register(cmdService, CmdIdOpenChat,     instance.OpenChat);
            Register(cmdService, CmdIdExplainCode,  instance.ExplainCode);
            Register(cmdService, CmdIdRefactorCode, instance.RefactorCode);
            Register(cmdService, CmdIdGenerateDoc,  instance.GenerateDoc);
            Register(cmdService, CmdIdReviewCode,   instance.ReviewCode);
            Register(cmdService, CmdIdFixCode,      instance.FixCode);
            Register(cmdService, CmdIdGenerateTest, instance.GenerateTest);
            Register(cmdService, CmdIdOpenOptions,  instance.OpenOptions);
        }

        private static void Register(IMenuCommandService svc, int id, EventHandler handler)
        {
            var cmdId = new CommandID(CommandSetGuid, id);
            var cmd   = new MenuCommand(handler, cmdId);
            svc.AddCommand(cmd);
        }

        // ── Command handlers ──────────────────────────────────────────────────
        private void OpenChat(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var win = await _package.ShowToolWindowAsync(
                    typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken);
            });
        }

        private void ExplainCode(object sender, EventArgs e)
        {
            OpenChatWithAction("explain");
        }

        private void RefactorCode(object sender, EventArgs e)
        {
            OpenChatWithAction("refactor");
        }

        private void GenerateDoc(object sender, EventArgs e)
        {
            OpenChatWithAction("document");
        }

        private void ReviewCode(object sender, EventArgs e)
        {
            OpenChatWithAction("review");
        }

        private void FixCode(object sender, EventArgs e)
        {
            OpenChatWithAction("fix");
        }

        private void GenerateTest(object sender, EventArgs e)
        {
            OpenChatWithAction("test");
        }

        private void OpenOptions(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ExecuteCommand("Tools.Options", "LocalPilot.General");
            });
        }

        private void OpenChatWithAction(string action)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await _package.ShowToolWindowAsync(
                    typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken);

                // Fire quick action — find the shown window and trigger
                var win = await _package.FindToolWindowAsync(
                    typeof(LocalPilotChatWindow), 0, false, _package.DisposalToken)
                    as LocalPilotChatWindow;

                if (win?.Content is LocalPilotChatControl ctrl)
                {
                    // Simulate the quick-action click via the public method
                    ctrl.FireQuickAction(action);
                }
            });
        }
    }
}
