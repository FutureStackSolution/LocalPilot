using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LocalPilot.Chat;
using Microsoft.VisualStudio.Shell;

namespace LocalPilot.Services
{
    /// <summary>
    /// Routes user-invoked LocalPilot commands into a single execution flow.
    /// This ensures menus, inline UI, and future surfaces behave consistently.
    /// </summary>
    public sealed class LocalPilotCommandRouter
    {
        private static readonly LocalPilotCommandRouter _instance = new LocalPilotCommandRouter();
        public static LocalPilotCommandRouter Instance => _instance;

        private AsyncPackage _package;

        private LocalPilotCommandRouter() { }

        public void Initialize(AsyncPackage package)
        {
            _package = package;
        }

        public async Task OpenChatAsync()
        {
            if (_package == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await _package.ShowToolWindowAsync(typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken);
        }

        public async Task ExecuteQuickActionAsync(string action, string preCapturedSelection = null)
        {
            if (_package == null || string.IsNullOrWhiteSpace(action)) return;

            LocalPilotLogger.Log($"[Router] ExecuteQuickActionAsync started for action: {action}");
            string selectedCode = preCapturedSelection ?? string.Empty;

            if (string.IsNullOrWhiteSpace(selectedCode))
            {
                selectedCode = await CaptureSelectionAsync();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var win = await _package.ShowToolWindowAsync(typeof(LocalPilotChatWindow), 0, true, _package.DisposalToken)
                as LocalPilotChatWindow;

            if (win?.Content is LocalPilotChatControl ctrl)
            {
                await Task.Delay(50).ConfigureAwait(true);
                ctrl.FireQuickAction(action, selectedCode);
            }
        }

        private async Task<string> CaptureSelectionAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextView?.Selection != null && docView.TextView.Selection.SelectedSpans.Count > 0)
                {
                    var text = docView.TextView.Selection.SelectedSpans[0].GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        LocalPilotLogger.Log("[Router] Captured code via Toolkit TextView");
                        return text;
                    }
                }

                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.ActiveDocument?.Selection is EnvDTE.TextSelection sel && !string.IsNullOrWhiteSpace(sel.Text))
                {
                    LocalPilotLogger.Log("[Router] Captured code via DTE Selection");
                    return sel.Text;
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Router] Failed to capture code selection", ex);
            }

            return string.Empty;
        }
    }
}
