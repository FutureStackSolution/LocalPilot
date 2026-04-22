using LocalPilot.Chat;
using LocalPilot.Commands;
using LocalPilot.Options;
using LocalPilot.Services;
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
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(LocalPilotChatWindow),
        Style = VsDockStyle.Tabbed,
        Window      = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids80.SolutionExplorer,
        Orientation = ToolWindowOrientation.Right)]

    // Options page — accessible via Tools > Options > LocalPilot
    [ProvideOptionPage(typeof(LocalPilotOptionsPage),
      "LocalPilot", "General", 0, 0, supportsAutomation: true)]
    [ProvideOptionPage(typeof(LocalPilotAdvancedOptionsPage),
      "LocalPilot", "Advanced", 0, 0, supportsAutomation: true)]

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
            LocalPilotCommandRouter.Instance.Initialize(this);

            // Auto-Index Project Context in background (v1.3)
            _ = Task.Run(async () =>
            {
                try
                {
                    // v2.0 Enterprise Intelligence: Wait for VS Solution to fully stabilize
                    await Task.Delay(10000, cancellationToken).ConfigureAwait(false); 
                    
                    var ollama = new OllamaService(settings.OllamaBaseUrl);
                    LocalPilotLogger.Log("[Autopilot] Indexing project context in background...");
                    await ProjectContextService.Instance.IndexSolutionAsync(ollama, cancellationToken);

                    // v3.0 Nexus Intelligence: Build the Full-Stack Dependency Graph
                    string root = "";
                    await JoinableTaskFactory.RunAsync(async () => {
                         var sol = await Community.VisualStudio.Toolkit.VS.Solutions.GetCurrentSolutionAsync();
                         if (sol != null) root = System.IO.Path.GetDirectoryName(sol.FullPath);
                    });
                    if (!string.IsNullOrEmpty(root)) {
                        await NexusService.Instance.InitializeAsync(root);
                        await NexusService.Instance.RebuildGraphAsync(cancellationToken);
                    }
                }
                catch { /* Quiet skip - background indexing is best-effort */ }
            }, cancellationToken);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }
    }
}
