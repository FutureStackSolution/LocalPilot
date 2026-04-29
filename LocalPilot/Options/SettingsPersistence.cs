using LocalPilot.Settings;
using Newtonsoft.Json;
using System;
using System.IO;

namespace LocalPilot.Options
{
    /// <summary>
    /// Saves/loads LocalPilotSettings to a JSON file in %AppData%\LocalPilot\.
    /// </summary>
    public static class SettingsPersistence
    {
        private static readonly string SettingsDir  =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "LocalPilot");
        private static readonly string SettingsFile =
            Path.Combine(SettingsDir, "settings.json");
            
        public static bool Exists => File.Exists(SettingsFile);

        public static void Save(LocalPilotSettings s)
        {

            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonConvert.SerializeObject(s, Formatting.Indented);
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalPilot] Save settings failed: {ex.Message}");
            }
        }

        public static LocalPilotSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new LocalPilotSettings();

                var json = File.ReadAllText(SettingsFile);
                return JsonConvert.DeserializeObject<LocalPilotSettings>(json)
                       ?? new LocalPilotSettings();
                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalPilot] Load settings failed: {ex.Message}");
                return new LocalPilotSettings();
            }
        }
    }
}
