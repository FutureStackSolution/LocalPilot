using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LocalPilot.Commands;
using LocalPilot.Settings;

namespace LocalPilot.Services
{
    /// <summary>
    /// Canonical source of truth for user-facing LocalPilot capabilities.
    /// This keeps command bindings, quick-actions, and future lightbulb actions aligned.
    /// </summary>
    public static class CapabilityCatalog
    {
        private static readonly IReadOnlyList<LocalPilotCapability> _all = new ReadOnlyCollection<LocalPilotCapability>(
            new List<LocalPilotCapability>
            {
                new LocalPilotCapability("explain",  "Explain Code",           "/explain",  LocalPilotCommands.CmdIdExplainCode,  s => s.EnableExplain),
                new LocalPilotCapability("refactor", "Refactor Code",          "/refactor", LocalPilotCommands.CmdIdRefactorCode, s => s.EnableRefactor),
                new LocalPilotCapability("document", "Generate Documentation",  "/document", LocalPilotCommands.CmdIdGenerateDoc, s => s.EnableDocGen),
                new LocalPilotCapability("review",   "Review Code",            "/review",   LocalPilotCommands.CmdIdReviewCode,   s => s.EnableReview),
                new LocalPilotCapability("fix",      "Fix Issues",             "/fix",      LocalPilotCommands.CmdIdFixCode,      s => s.EnableFix),
                new LocalPilotCapability("test",     "Generate Unit Tests",    "/test",     LocalPilotCommands.CmdIdGenerateTest, s => s.EnableUnitTest)
            });

        public static IReadOnlyList<LocalPilotCapability> All => _all;

        public static LocalPilotCapability FromAction(string action)
        {
            return _all.FirstOrDefault(c =>
                c.Action.Equals(action ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        public static LocalPilotCapability FromCommandId(int commandId)
        {
            return _all.FirstOrDefault(c => c.CommandId == commandId);
        }

        public static bool TryResolveActionFromSlash(string slashToken, out string action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(slashToken)) return false;

            string normalized = slashToken.Trim().ToLowerInvariant();
            if (!normalized.StartsWith("/")) normalized = "/" + normalized;

            if (normalized == "/doc")
            {
                action = "document";
                return true;
            }

            var capability = _all.FirstOrDefault(c =>
                c.SlashCommand.Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (capability == null) return false;
            action = capability.Action;
            return true;
        }

        public static IReadOnlyList<LocalPilotCapability> Enabled(LocalPilotSettings settings)
        {
            var result = new List<LocalPilotCapability>();
            foreach (var capability in _all)
            {
                if (capability.IsEnabled(settings))
                {
                    result.Add(capability);
                }
            }

            return result;
        }
    }

    public sealed class LocalPilotCapability
    {
        public string Action { get; }
        public string DisplayName { get; }
        public string SlashCommand { get; }
        public int CommandId { get; }
        public Func<LocalPilotSettings, bool> IsEnabled { get; }

        public LocalPilotCapability(
            string action,
            string displayName,
            string slashCommand,
            int commandId,
            Func<LocalPilotSettings, bool> isEnabled)
        {
            Action = action ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            SlashCommand = slashCommand ?? string.Empty;
            CommandId = commandId;
            IsEnabled = isEnabled ?? (_ => true);
        }
    }
}
