using LocalPilot.Chat;
using LocalPilot.Commands;
using LocalPilot.Options;
using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace LocalPilot
{
    /// <summary>
    /// LocalPilot — GitHub Copilot-style AI extension powered by Ollama.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [Microsoft.VisualStudio.Shell.ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(LocalPilotChatWindow),
        Style       = Microsoft.VisualStudio.Shell.VsDockStyle.Tabbed,
        Window      = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids80.SolutionExplorer,
        Orientation = Microsoft.VisualStudio.Shell.ToolWindowOrientation.Right)]

    // Options page — accessible via Tools > Options > LocalPilot
    [ProvideOptionPage(typeof(LocalPilotOptionsPage),
      "LocalPilot", "General", 0, 0, supportsAutomation: true)]

    // Auto-load when a solution opens
    [ProvideAutoLoad(
        Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists,
        PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(
        Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution,
        PackageAutoLoadFlags.BackgroundLoad)]

    public sealed class LocalPilotPackage : AsyncPackage
    {
        public const string PackageGuidString = "5a6b34f5-329a-497a-956a-7f055a948b94";

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            // Load persisted settings
            var settings = SettingsPersistence.Load();
            LocalPilotSettings.UpdateInstance(settings);

            // Register all commands
            await LocalPilotCommands.InitializeAsync(this);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }
    }
}
