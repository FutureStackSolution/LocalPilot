using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LocalPilot.Models;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using EnvDTE80;

namespace LocalPilot.Services
{
    public class SentinelFixSuggestion
    {
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public string SuggestedFix { get; set; }
        public bool IsReady { get; set; }
    }

    /// <summary>
    /// The 'Sentinel Debugger' - Proactively monitors the error list 
    /// and prepares background fixes for build errors.
    /// </summary>
    public class SentinelDebugger
    {
        private static readonly Lazy<SentinelDebugger> _instance = new Lazy<SentinelDebugger>(() => new SentinelDebugger());
        public static SentinelDebugger Instance => _instance.Value;

        private DTE2 _dte;
        public event Action<SentinelFixSuggestion> OnFixReady;

        private SentinelDebugger()
        {
        }

        public void Initialize(AgentOrchestrator orchestrator)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                _dte = await VS.GetRequiredServiceAsync<SDTE, DTE2>();
                if (_dte != null)
                {
                    // 🛡️ STATIC HEALING: Monitor Build Errors via DTE
                    _dte.Events.BuildEvents.OnBuildDone += (scope, action) => {
                        _ = AnalyzeErrorsInBackgroundAsync();
                    };

                    // 🛡️ DYNAMIC HEALING: Monitor Runtime Exceptions via DTE
                    _dte.Events.DebuggerEvents.OnExceptionThrown += (string exceptionType, string exceptionName, int code, string description, ref dbgExceptionAction action) => {
                        HandleRuntimeExceptionAsync(exceptionType, description, "Thrown").FireAndForget();
                    };

                    _dte.Events.DebuggerEvents.OnExceptionNotHandled += (string exceptionType, string exceptionName, int code, string description, ref dbgExceptionAction action) => {
                        HandleRuntimeExceptionAsync(exceptionType, description, "UnHandled").FireAndForget();
                    };
                }

                LocalPilotLogger.Log("[Sentinel] Debugger Active. Monitoring builds and runtime crashes.");
            });
        }

        private void OnExceptionThrown(string exceptionType, string exceptionMessage)
        {
            HandleRuntimeExceptionAsync(exceptionType, exceptionMessage, "Thrown").FireAndForget();
        }

        private void OnExceptionNotHandled(string exceptionType, string exceptionMessage)
        {
            HandleRuntimeExceptionAsync(exceptionType, exceptionMessage, "UnHandled").FireAndForget();
        }

        private async Task HandleRuntimeExceptionAsync(string type, string message, string mode)
        {
            LocalPilotLogger.Log($"[Sentinel] Runtime crash detected ({mode}): {type}. Analyzing state...", category: LogCategory.Context);

            var suggestion = new SentinelFixSuggestion
            {
                ErrorCode = type,
                ErrorMessage = message,
                IsReady = false
            };

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var activeDoc = await VS.Documents.GetActiveDocumentViewAsync();
            if (activeDoc != null)
            {
                suggestion.FilePath = activeDoc.FilePath;
            }

            OnFixReady?.Invoke(suggestion);
        }

        private async Task AnalyzeErrorsInBackgroundAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_dte == null) return;

                var items = _dte.ToolWindows.ErrorList.ErrorItems;
                if (items == null || items.Count == 0) return;

                ErrorItem primaryError = null;
                for (int i = 1; i <= items.Count; i++)
                {
                    var item = items.Item(i);
                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                    {
                        primaryError = item;
                        break;
                    }
                }
                
                if (primaryError == null) return;

                LocalPilotLogger.Log($"[Sentinel] Build failed with error {primaryError.Description}. Analyzing root cause...", category: LogCategory.Build);

                var suggestion = new SentinelFixSuggestion
                {
                    ErrorCode = "BuildError",
                    ErrorMessage = primaryError.Description,
                    FilePath = primaryError.FileName,
                    Line = primaryError.Line,
                    IsReady = false
                };

                OnFixReady?.Invoke(suggestion);
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Sentinel] Error analysis failed", ex, category: LogCategory.Build);
            }
        }

        public async Task<string> GenerateDraftFixAsync(AgentOrchestrator orchestrator, SentinelFixSuggestion suggestion, CancellationToken ct)
        {
            if (suggestion == null) return "No active error to fix.";

            string prompt = $"I encountered a build error: {suggestion.ErrorCode}: {suggestion.ErrorMessage} in {suggestion.FilePath} around line {suggestion.Line}. " +
                            $"Analyze the surrounding files and propose a surgical fix. Focus ONLY on fixing this error.";

            await orchestrator.RunTaskAsync(prompt, new List<ChatMessage>(), ct);
            return "Sentinel fix drafted.";
        }
    }
}
