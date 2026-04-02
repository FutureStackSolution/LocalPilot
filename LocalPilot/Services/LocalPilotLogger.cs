using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
using LocalPilot.Settings;

namespace LocalPilot.Services
{
    public static class LocalPilotLogger
    {
        private static IVsOutputWindowPane _pane;
        private static Guid _paneGuid = new Guid("A1B2C3D4-E5F6-4A5B-B9C8-D7E6F5A4B3C2");
        private static readonly string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalPilot", "logs");
        private static readonly string _logFile = Path.Combine(_logDir, "localpilot.log");

        public static string GetLogPath() => _logFile;

        public static void Log(string message)
        {
            if (!LocalPilotSettings.Instance.EnableLogging) return;

            string timestamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

            // 1. Log to Output Window (for active debugging)
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_pane == null)
                {
                    var outWindow = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outWindow != null)
                    {
                        outWindow.CreatePane(ref _paneGuid, "LocalPilot", 1, 1);
                        outWindow.GetPane(ref _paneGuid, out _pane);
                    }
                }
                _pane?.OutputStringThreadSafe($"{timestamped}{Environment.NewLine}");
            }).FileAndForget("LocalPilot.Logger.Output");

            // 2. Log to File (for persistent history/sharing)
            try
            {
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                File.AppendAllText(_logFile, timestamped + Environment.NewLine);
            }
            catch { /* Best effort file writing */ }
        }

        public static void LogError(string message, Exception ex = null)
        {
            string err = $"[ERROR] {message} {(ex != null ? ex.ToString() : "")}";
            Log(err);
        }
    }
}
