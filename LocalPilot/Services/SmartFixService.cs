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
    public class SmartFixSuggestion
    {
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public string SuggestedFix { get; set; }
        public bool IsReady { get; set; }
    }

    /// <summary>
    /// The 'Smart Fix' - Proactively monitors the error list 
    /// and prepares background fixes for build errors.
    /// </summary>
    public class SmartFixService
    {
        private static readonly Lazy<SmartFixService> _instance = new Lazy<SmartFixService>(() => new SmartFixService());
        public static SmartFixService Instance => _instance.Value;

        private DTE2 _dte;
        public event Action<SmartFixSuggestion> OnFixReady;
        private volatile bool _isAnalyzing = false;

        private SmartFixService() { }

        public void Initialize(AgentOrchestrator orchestrator)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                _dte = await VS.GetRequiredServiceAsync<SDTE, DTE2>();
                if (_dte != null)
                {
                    // Monitor Build Errors via DTE (isolated with try-catch)
                    _dte.Events.BuildEvents.OnBuildDone += (scope, action) => {
                        try { _ = AnalyzeErrorsInBackgroundAsync(); }
                        catch (Exception ex) { LocalPilotLogger.LogError("[SmartFix] Build handler failed", ex); }
                    };

                    // Monitor Runtime Exceptions via DTE (with recursion guard)
                    _dte.Events.DebuggerEvents.OnExceptionThrown += (string exceptionType, string exceptionName, int code, string description, ref dbgExceptionAction action) => {
                        // Skip LocalPilot's own exceptions to prevent infinite recursion
                        if (exceptionType?.Contains("LocalPilot") == true || _isAnalyzing) return;
                        try { HandleRuntimeExceptionAsync(exceptionType, description, "Thrown").FireAndForget(); }
                        catch { }
                    };

                    _dte.Events.DebuggerEvents.OnExceptionNotHandled += (string exceptionType, string exceptionName, int code, string description, ref dbgExceptionAction action) => {
                        if (exceptionType?.Contains("LocalPilot") == true || _isAnalyzing) return;
                        try { HandleRuntimeExceptionAsync(exceptionType, description, "UnHandled").FireAndForget(); }
                        catch { }
                    };
                }

                LocalPilotLogger.Log("[SmartFix] Service Active. Monitoring builds and runtime crashes.");
            });
        }

        private async Task HandleRuntimeExceptionAsync(string type, string message, string mode)
        {
            _isAnalyzing = true;
            try
            {
                LocalPilotLogger.Log($"[SmartFix] Runtime crash detected ({mode}): {type}. Analyzing state...", category: LogCategory.Context);

                var suggestion = new SmartFixSuggestion
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
            finally { _isAnalyzing = false; }
        }

        private async Task AnalyzeErrorsInBackgroundAsync()
        {
            if (_isAnalyzing) return;
            _isAnalyzing = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_dte == null) return;

                var items = _dte.ToolWindows.ErrorList.ErrorItems;
                if (items == null) return;

                ErrorItem primaryError = null;
                int count = items.Count;
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        var item = items.Item(i);
                        if (item == null) continue;

                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            primaryError = item;
                            break;
                        }
                    }
                    catch { /* Item might have been removed during iteration */ }
                }
                
                if (primaryError == null) return;

                LocalPilotLogger.Log($"[SmartFix] Build failed with error {primaryError.Description}. Analyzing root cause...", category: LogCategory.Build);

                var suggestion = new SmartFixSuggestion
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
                LocalPilotLogger.LogError("[SmartFix] Error analysis failed", ex, category: LogCategory.Build);
            }
            finally { _isAnalyzing = false; }
        }

        public async Task<string> GenerateDraftFixAsync(AgentOrchestrator orchestrator, SmartFixSuggestion suggestion, CancellationToken ct)
        {
            if (suggestion == null) return "No active error to fix.";

            string prompt = $"I encountered a build error: {suggestion.ErrorCode}: {suggestion.ErrorMessage} in {suggestion.FilePath} around line {suggestion.Line}. " +
                            $"Analyze the surrounding files and propose a surgical fix. Focus ONLY on fixing this error.";

            await orchestrator.RunTaskAsync(prompt, new List<ChatMessage>(), ct);
            return "Smart Fix drafted.";
        }
    }
}
