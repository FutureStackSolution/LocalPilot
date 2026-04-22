using LocalPilot.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    public enum LogCategory
    {
        General,
        Context,
        Agent,
        Ollama,
        UI,
        Build,
        LSP
    }

    public enum LogSeverity
    {
        Info,
        Warning,
        Error,
        Debug
    }

    public static class LocalPilotLogger
    {
        public static event Action<string, LogCategory, LogSeverity> OnLog;

        private static IVsOutputWindowPane _pane;
        private static IVsOutputWindowPane _watchdogPane;
        private static Guid _paneGuid = new Guid("A1B2C3D4-E5F6-4A5B-B9C8-D7E6F5A4B3C2");
        private static Guid _watchdogPaneGuid = new Guid("B2C3D4E5-F6A7-5B6C-C9D8-E7F6A5B4C3D2");
        private static readonly string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalPilot", "logs");
        private static readonly string _logFile = Path.Combine(_logDir, "localpilot.log");
        private static readonly object _fileLock = new object();
        private static bool _initializing = false;

        public static string GetLogPath() => _logFile;

        public static void Log(string message, LogCategory category = LogCategory.General, LogSeverity severity = LogSeverity.Info)
        {
            if (!LocalPilotSettings.Instance.EnableLogging && severity != LogSeverity.Error)
            {
                // Specialized routing: even if logging is off, we still route to Watchdog if it's a Smart Fix event during Debugging
                if (category != LogCategory.Build && category != LogCategory.Agent) return;
            }

            string sevStr = severity.ToString().ToUpper();
            string catStr = category.ToString().ToUpper();
            string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] [{sevStr}] [{catStr}] {message}";

            // 1. Notify Subscribers (UI)
            OnLog?.Invoke(message, category, severity);

            // 2. Log to Output Window (Non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_pane == null && !_initializing)
                    {
                        await InitializePanesAsync();
                    }

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Route to General vs Watchdog
                    if (category == LogCategory.Build || category == LogCategory.Context || severity == LogSeverity.Debug)
                    {
                         _watchdogPane?.OutputStringThreadSafe($"{timestamped}{Environment.NewLine}");
                    }
                    else
                    {
                        _pane?.OutputStringThreadSafe($"{timestamped}{Environment.NewLine}");
                    }
                }
                catch { }
            });

            // 3. Log to File
            _ = Task.Run(() =>
            {
                lock (_fileLock)
                {
                    try
                    {
                        if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                        
                        var fi = new FileInfo(_logFile);
                        if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        {
                            string backup = _logFile + ".old";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(_logFile, backup);
                        }

                        File.AppendAllText(_logFile, timestamped + Environment.NewLine);
                    }
                    catch { }
                }
            });
        }

        private static async Task InitializePanesAsync()
        {
            _initializing = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var outWindow = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outWindow != null)
                {
                    // Primary Pane
                    outWindow.CreatePane(ref _paneGuid, "LocalPilot", 1, 1);
                    outWindow.GetPane(ref _paneGuid, out _pane);

                    // Watchdog Pane (for background debugging analysis)
                    outWindow.CreatePane(ref _watchdogPaneGuid, "LocalPilot (Watchdog)", 1, 1);
                    outWindow.GetPane(ref _watchdogPaneGuid, out _watchdogPane);
                }
            }
            finally
            {
                _initializing = false;
            }
        }

        public static void LogError(string message, Exception ex = null, LogCategory category = LogCategory.General)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(message);
            if (ex != null)
            {
                sb.AppendLine();
                sb.AppendLine($"[Exception] {ex.GetType().Name}: {ex.Message}");
                sb.AppendLine($"[StackTrace] {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"[Inner] {ex.InnerException.Message}");
                }
            }
            Log(sb.ToString(), category, LogSeverity.Error);
        }

        public static void LogWarning(string message, LogCategory category = LogCategory.General)
        {
            Log(message, category, LogSeverity.Warning);
        }

        public static void LogDebug(string message, LogCategory category = LogCategory.General)
        {
            Log(message, category, LogSeverity.Debug);
        }
    }
}
