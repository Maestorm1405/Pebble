using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Pebble
{
    public class TodoItem
    {
        public string Text { get; set; }
        public bool Done { get; set; }
    }

    public class MoveRecord
    {
        public string From { get; set; }
        public string To { get; set; }
    }

    public class Settings
    {
        public string Prompt { get; set; }
        public double Speed { get; set; } = 0.35;
        public List<string> Shelf { get; set; } = new List<string>();
        public string City { get; set; } = "";
        public List<TodoItem> Todos { get; set; } = new List<TodoItem>();
        public bool NotificationsOn { get; set; } = true;
        public bool TidyOn { get; set; } = false;   // opt-in: moving someone's files should never be a surprise
        public List<MoveRecord> LastTidy { get; set; } = new List<MoveRecord>();
        public List<string> TidyIgnore { get; set; } = new List<string>();
        public DateTime? LastTidyAt { get; set; }

        // settings page
        public bool TourDone { get; set; }
        public string SearchHotkey { get; set; } = "Alt+Space";
        public bool ShowWeather { get; set; } = true;
        public string FullscreenMode { get; set; } = "Always";   // Always, Games, Never
        public int OpenDelayMs { get; set; } = 130;    // how long you hover before the island opens
        public int CloseDelayMs { get; set; } = 550;   // how long after you leave before it closes
        public string TempUnit { get; set; } = "Auto";        // Auto, F, C
        public bool SongPopups { get; set; } = true;
        public bool VolumePopups { get; set; } = true;
        public bool LyricsInIsland { get; set; } = true;
        public bool GameModeOn { get; set; } = true;
        public bool GameStats { get; set; } = true;
        public bool GameHoldNotifications { get; set; } = true;
        public bool ColorPickerOn { get; set; } = true;
        public List<string> RecentColors { get; set; } = new List<string>();
        public Dictionary<string, int> LaunchCounts { get; set; } = new Dictionary<string, int>();

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pebble");
        static string FilePath => Path.Combine(Folder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
            }
            catch (Exception e) { Log.Write("settings: " + e.Message); }

            // first run: bring over to-dos, shelf, script and city from the PowerShell prototype
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchyWin", "settings.json");
                if (File.Exists(old)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(old), Options) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch (Exception e) { Log.Write("save: " + e.Message); }
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:s}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    /// <summary>Start with Windows: a StartupTask when installed from the Store/MSIX, the Run key otherwise.</summary>
    static class Autostart
    {
        const string TaskId = "PebbleStartup";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsPackaged
        {
            get { try { return Package.Current != null; } catch { return false; } }
        }

        public static async Task<bool> IsEnabledAsync()
        {
            try
            {
                if (IsPackaged)
                {
                    var t = await StartupTask.GetAsync(TaskId);
                    return t.State == StartupTaskState.Enabled || t.State == StartupTaskState.EnabledByPolicy;
                }
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("Pebble") != null;
            }
            catch { return false; }
        }

        public static async Task<bool> SetAsync(bool on)
        {
            try
            {
                if (IsPackaged)
                {
                    var t = await StartupTask.GetAsync(TaskId);
                    if (!on) { t.Disable(); return false; }
                    var s = await t.RequestEnableAsync();
                    return s == StartupTaskState.Enabled || s == StartupTaskState.EnabledByPolicy;
                }
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue("Pebble", "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue("Pebble", false);
                return on;
            }
            catch (Exception e) { Log.Write("autostart: " + e.Message); return false; }
        }
    }
}
