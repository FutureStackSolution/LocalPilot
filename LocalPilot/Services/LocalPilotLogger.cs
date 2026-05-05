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
        LSP,
        Storage,
        Performance
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
        private static readonly System.Collections.Concurrent.ConcurrentQueue<(string message, bool isWatchdog)> _logQueue = new System.Collections.Concurrent.ConcurrentQueue<(string message, bool isWatchdog)>();
        private static bool _initializing = false;
        private static bool _loopStarted = false;
        private static readonly object _loopLock = new object();

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
            
            // 🚀 DEDUPLICATION: If the message already starts with the category name, strip it to avoid redundant tags
            string cleanMessage = message;
            string prefix = $"[{catStr}]";
            string prefixAlt = $"[{category}]";
            if (cleanMessage.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                cleanMessage = cleanMessage.Substring(prefix.Length).Trim();
            else if (cleanMessage.StartsWith(prefixAlt, StringComparison.OrdinalIgnoreCase))
                cleanMessage = cleanMessage.Substring(prefixAlt.Length).Trim();

            string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] [{sevStr}] [{catStr}] {cleanMessage}";

            // 1. Notify Subscribers (UI)
            OnLog?.Invoke(cleanMessage, category, severity);

            // 2. Queue for Output Window & File (High Performance Batching)
            bool isWatchdog = category == LogCategory.Build || category == LogCategory.Context || severity == LogSeverity.Debug;
            _logQueue.Enqueue((timestamped, isWatchdog));
            EnsureLogLoopRunning();
        }

        private static void EnsureLogLoopRunning()
        {
            if (_loopStarted) return;
            lock (_loopLock)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _ = Task.Run(async () => await ProcessLogQueueAsync());
            }
        }

        private static async Task ProcessLogQueueAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(300); // 🚀 Batch logs every 300ms to save UI thread and disk I/O
                    
                    if (_logQueue.IsEmpty) continue;

                    var mainBatch = new System.Text.StringBuilder();
                    var watchdogBatch = new System.Text.StringBuilder();
                    var fileBatch = new System.Text.StringBuilder();

                    while (_logQueue.TryDequeue(out var entry))
                    {
                        fileBatch.AppendLine(entry.message);
                        if (entry.isWatchdog) watchdogBatch.AppendLine(entry.message);
                        else mainBatch.AppendLine(entry.message);
                    }

                    // 1. Update Output Window (Main Thread)
                    if (mainBatch.Length > 0 || watchdogBatch.Length > 0)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (_pane == null && !_initializing) await InitializePanesAsync();
                        if (mainBatch.Length > 0) _pane?.OutputStringThreadSafe(mainBatch.ToString());
                        if (watchdogBatch.Length > 0) _watchdogPane?.OutputStringThreadSafe(watchdogBatch.ToString());
                    }

                    // 2. Update Log File (Background Thread)
                    if (fileBatch.Length > 0)
                    {
                        await Task.Run(() =>
                        {
                            lock (_fileLock)
                            {
                                try
                                {
                                    if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                                    var fi = new FileInfo(_logFile);
                                    if (fi.Exists && fi.Length > 10 * 1024 * 1024) // Raised to 10MB
                                    {
                                        string backup = _logFile + ".old";
                                        if (File.Exists(backup)) File.Delete(backup);
                                        File.Move(_logFile, backup);
                                    }
                                    File.AppendAllText(_logFile, fileBatch.ToString());
                                }
                                catch { }
                            }
                        });
                    }
                }
                catch { /* Prevent loop death */ }
            }
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
