using LocalPilot.Chat;
using LocalPilot.Commands;
using LocalPilot.Options;
using LocalPilot.Services;
using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
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
            bool isFirstRun = !SettingsPersistence.Exists;
            var settings = SettingsPersistence.Load();
            LocalPilotSettings.UpdateInstance(settings);

            // 🚀 FIRST-RUN AUTO-DISCOVERY (v1.7)
            // If this is the first time the user installs LocalPilot, 
            // try to find what models they actually have in Ollama 
            // instead of failing with 404 for 'llama3'.
            if (isFirstRun || string.IsNullOrEmpty(settings.ChatModel))
            {
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    Microsoft.VisualStudio.Shell.Interop.IVsUIShell uiShell = (Microsoft.VisualStudio.Shell.Interop.IVsUIShell)await GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell));
                    Guid clsid = Guid.Empty;
                    int result;
                    uiShell.ShowMessageBox(
                        0,
                        ref clsid,
                        "LocalPilot Configuration Required",
                        "Welcome to LocalPilot! Since no models are configured, please ensure Ollama is running, then go to Tools -> Options -> LocalPilot to set your AI models.",
                        string.Empty,
                        0,
                        Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                        Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                        0,
                        out result);
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ollama = new OllamaService(settings.OllamaBaseUrl);
                        var available = await ollama.GetAvailableModelsAsync();
                        if (available.Count > 0)
                        {
                            // Pick first non-embedding model for chat
                            string chatModel = available.FirstOrDefault(m => 
                                !m.Contains("embed") && !m.Contains("nomic") && !m.Contains("bge-") && !m.Contains("e5-")) 
                                ?? available[0];

                            // Pick first embedding model for embeddings (fallback to chat model)
                            string embedModel = available.FirstOrDefault(m => 
                                m.Contains("embed") || m.Contains("nomic")) 
                                ?? chatModel;

                            settings.ChatModel = chatModel;
                            settings.CompletionModel = chatModel;
                            settings.ExplainModel = chatModel;
                            settings.RefactorModel = chatModel;
                            settings.DocModel = chatModel;
                            settings.ReviewModel = chatModel;
                            settings.EmbeddingModel = embedModel;

                            SettingsPersistence.Save(settings);
                            LocalPilotLogger.Log($"[AutoConfig] First-run detected available models. Set default to: {chatModel}", LogCategory.Ollama);
                        }
                    }
                    catch { /* Quietly skip if Ollama is not running during first-run */ }
                });
            }

            // Register all commands
            await LocalPilotCommands.InitializeAsync(this);
            LocalPilotCommandRouter.Instance.Initialize(this);

            // Auto-Index Project Context in background (v1.7)
            _ = Task.Run(async () =>
            {
                try
                {
                    // v1.7 Enterprise Intelligence: Wait for VS Solution to fully stabilize
                    await Task.Delay(10000, cancellationToken).ConfigureAwait(false); 
                    
                    var ollama = new OllamaService(settings.OllamaBaseUrl);
                    LocalPilotLogger.Log("[Autopilot] Indexing project context in background...");
                    await ProjectContextService.Instance.IndexSolutionAsync(ollama, cancellationToken);

                    // v1.7 Nexus Intelligence: Build the Full-Stack Dependency Graph
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
