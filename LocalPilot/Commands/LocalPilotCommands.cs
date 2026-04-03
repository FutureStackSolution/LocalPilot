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
            Register(cmdService, CmdIdOpenChat,     instance.OpenChat,     () => LocalPilotSettings.Instance.EnableChatPanel);
            Register(cmdService, CmdIdOpenOptions,  instance.OpenOptions);

            // Context menu items (Visibility toggled by settings)
            Register(cmdService, CmdIdExplainCode,  instance.ExplainCode,  () => LocalPilotSettings.Instance.EnableExplain);
            Register(cmdService, CmdIdRefactorCode, instance.RefactorCode, () => LocalPilotSettings.Instance.EnableRefactor);
            Register(cmdService, CmdIdGenerateDoc,  instance.GenerateDoc,  () => LocalPilotSettings.Instance.EnableDocGen);
            Register(cmdService, CmdIdReviewCode,   instance.ReviewCode,   () => LocalPilotSettings.Instance.EnableReview);
            Register(cmdService, CmdIdFixCode,      instance.FixCode,      () => LocalPilotSettings.Instance.EnableFix);
            Register(cmdService, CmdIdGenerateTest, instance.GenerateTest, () => LocalPilotSettings.Instance.EnableUnitTest);
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
                var win = await _package.ShowToolWindowAsync(
                    typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken);
            });
        }

        private void ExplainCode(object sender, EventArgs e) => _ = OpenChatWithActionAsync("explain");
        private void RefactorCode(object sender, EventArgs e) => _ = OpenChatWithActionAsync("refactor");
        private void GenerateDoc(object sender, EventArgs e) => _ = OpenChatWithActionAsync("document");
        private void ReviewCode(object sender, EventArgs e) => _ = OpenChatWithActionAsync("review");
        private void FixCode(object sender, EventArgs e) => _ = OpenChatWithActionAsync("fix");
        private void GenerateTest(object sender, EventArgs e) => _ = OpenChatWithActionAsync("test");

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

        private async Task OpenChatWithActionAsync(string action)
        {
            LocalPilotLogger.Log($"[Commands] OpenChatWithActionAsync started for action: {action}");
            
            // 1. Capture context immediately on the original thread/context if possible
            string selectedCode = string.Empty;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                // Primary: Modern Toolkit approach (usually more stable)
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView?.Selection != null && docView.TextView.Selection.SelectedSpans.Count > 0)
                {
                    selectedCode = docView.TextView.Selection.SelectedSpans[0].GetText();
                    if (!string.IsNullOrWhiteSpace(selectedCode))
                        LocalPilotLogger.Log("[Commands] Captured code via Toolkit TextView");
                }
                
                // Fallback: DTE Selection
                if (string.IsNullOrWhiteSpace(selectedCode))
                {
                    var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    if (dte?.ActiveDocument?.Selection is EnvDTE.TextSelection sel && !string.IsNullOrWhiteSpace(sel.Text))
                    {
                        selectedCode = sel.Text;
                        LocalPilotLogger.Log("[Commands] Captured code via DTE Selection");
                    }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("Failed to capture code selection", ex);
            }

            LocalPilotLogger.Log($"[Commands] Final captured code length: {selectedCode?.Length ?? 0}");

            // 2. Ensure window is visible (This triggers Load events)
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var win = await _package.ShowToolWindowAsync(
                typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken)
                as LocalPilotChatWindow;

            // 3. Dispatch to control
            if (win?.Content is LocalPilotChatControl ctrl)
            {
                // Force a small yield to let the window finish its own Load/Layout events 
                // before we hammer it with a new AI request.
                await Task.Delay(50).ConfigureAwait(true);
                ctrl.FireQuickAction(action, selectedCode);
            }
        }
    }
}
