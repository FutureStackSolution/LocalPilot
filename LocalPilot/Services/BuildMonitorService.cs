using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using LocalPilot.Chat;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;

namespace LocalPilot.Services
{
    public class BuildMonitorService
    {
        private static readonly Lazy<BuildMonitorService> _instance = new Lazy<BuildMonitorService>(() => new BuildMonitorService());
        public static BuildMonitorService Instance => _instance.Value;

        private BuildMonitorService() { }

        public void Initialize()
        {
            // Hook into Visual Studio build events via DTE for maximum compatibility across VS versions
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = await VS.GetRequiredServiceAsync<SDTE, DTE2>();
                    if (dte != null)
                    {
                        dte.Events.BuildEvents.OnBuildDone += (scope, action) =>
                        {
                            // Trigger analysis when a build or rebuild completes
                            if (action == vsBuildAction.vsBuildActionBuild || action == vsBuildAction.vsBuildActionRebuildAll)
                            {
                                _ = OnBuildCompletedAsync();
                            }
                        };
                    }
                }
                catch (Exception ex)
                {
                    LocalPilotLogger.LogError("[BuildMonitor] Failed to initialize build events", ex);
                }
            });
        }

        private async Task OnBuildCompletedAsync()
        {
            try
            {
                // Wait for Error List to populate
                await Task.Delay(1000); 
                
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                var dte = await VS.GetRequiredServiceAsync<SDTE, DTE2>();
                if (dte == null) return;

                var items = dte.ToolWindows.ErrorList.ErrorItems;
                if (items == null) return;

                ErrorItem firstError = null;
                // 🚀 EXPERT: Use try-catch inside loop and safe indexing as the list can change during iteration
                try
                {
                    int count = items.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        var item = items.Item(i);
                        if (item == null) continue;

                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            firstError = item;
                            break;
                        }
                    }
                }
                catch { /* Error list changed during scan, skip this turn */ }

                if (firstError != null)
                {
                    LocalPilotLogger.Log($"[BuildMonitor] Build failed: {firstError.Description} in {firstError.FileName}", LogCategory.General);
                    
                    await VS.Windows.ShowToolWindowAsync(new Guid(LocalPilotChatWindow.WindowGuidString));
                    
                    try
                    {
                        var win = dte.Windows.Item(LocalPilotChatWindow.WindowGuidString);
                        if (win != null && win.Object is LocalPilotChatWindow lpWindow)
                        {
                            if (lpWindow.Content is LocalPilotChatControl chatControl)
                            {
                                chatControl.NotifyBuildError(firstError.Description, firstError.FileName, firstError.Line, 0);
                            }
                        }
                    }
                    catch { /* Window might not be fully initialized yet */ }
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[BuildMonitor] Failed to process build error", ex);
            }
        }
    }
}
