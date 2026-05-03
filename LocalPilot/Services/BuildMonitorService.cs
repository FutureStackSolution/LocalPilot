using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using LocalPilot.Chat;

namespace LocalPilot.Services
{
    public class BuildMonitorService
    {
        private static readonly Lazy<BuildMonitorService> _instance = new Lazy<BuildMonitorService>(() => new BuildMonitorService());
        public static BuildMonitorService Instance => _instance.Value;

        private BuildMonitorService() { }

        public void Initialize()
        {
            // Hook into Visual Studio build events
            VS.Events.BuildEvents.SolutionBuildFinished += OnSolutionBuildFinished;
        }

        private void OnSolutionBuildFinished(bool success)
        {
            if (success) return;

            // 🚀 Build failed! Let's find the errors.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500); // Wait for Error List to populate
                    var errors = await VS.Errors.GetErrorsAsync();
                    var firstError = errors?.FirstOrDefault(e => e.Severity == Microsoft.VisualStudio.Shell.TableManager.ErrorCategory.Error);

                    if (firstError != null)
                    {
                        LocalPilotLogger.Log($"[BuildMonitor] Build failed: {firstError.Text} in {firstError.Document}", LogCategory.General);
                        
                        // Notify the UI
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var chatWindow = await LocalPilotChatWindow.GetOrShowAsync();
                        if (chatWindow?.Content is LocalPilotChatControl chatControl)
                        {
                            chatControl.NotifyBuildError(firstError.Text, firstError.Document, firstError.Line, firstError.Column);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[BuildMonitor] Failed to process build error", ex);
                }
            });
        }
    }
}
