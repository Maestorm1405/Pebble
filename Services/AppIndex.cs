using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Pebble
{
    sealed class AppEntry
    {
        public string Name;
        public string ParsingName;   // shell:AppsFolder\... or a file path
        public string Kind;          // "App" or "Recent"
    }

    /// <summary>Everything in the Start menu (desktop + Store apps) plus recently opened files.</summary>
    sealed class AppIndex
    {
        public volatile List<AppEntry> Apps = new List<AppEntry>();
        public volatile List<AppEntry> Recent = new List<AppEntry>();

        public void Refresh()
        {
            var t = new Thread(() =>
            {
                try { Apps = LoadApps(); } catch (Exception e) { Log.Write("app index: " + e.Message); }
                try { Recent = LoadRecent(); } catch (Exception e) { Log.Write("recent index: " + e.Message); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // Shell.Application wants STA
            t.Start();
        }

        static List<AppEntry> LoadApps()
        {
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name, path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(name)) continue;
                list.Add(new AppEntry { Name = name, ParsingName = @"shell:AppsFolder\" + path, Kind = "App" });
            }
            return list;
        }

        static List<AppEntry> LoadRecent()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return new DirectoryInfo(dir).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTime)
                // skip app deep links Windows records as "recent" (e.g. slack--channel-id=...&team=...)
                .Where(f => f.Name.IndexOf('=') < 0 && !f.Name.Contains("--") && !f.Name.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Take(300)
                .Select(f => new AppEntry { Name = Path.GetFileNameWithoutExtension(f.Name), ParsingName = f.FullName, Kind = "Recent" })
                .ToList();
        }

        public static void Launch(AppEntry e)
        {
            if (e.Kind == "App") Process.Start(new ProcessStartInfo("explorer.exe", "\"" + e.ParsingName + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(e.ParsingName) { UseShellExecute = true });
        }

        /// <summary>Higher is better; 0 means no match.</summary>
        public static double Score(string name, string query)
        {
            if (query.Length == 0) return 0;
            string n = name.ToLowerInvariant(), q = query.ToLowerInvariant();
            if (n == q) return 100;
            if (n.StartsWith(q)) return 80 - Math.Min(20, n.Length - q.Length) * 0.2;
            // start of any word ("code" -> "Visual Studio Code")
            foreach (var w in n.Split(' ', '-', '_', '.'))
                if (w.StartsWith(q)) return 60;
            // initials ("vsc" -> "Visual Studio Code")
            var initials = new string(n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
            if (q.Length >= 2 && initials.StartsWith(q)) return 50;
            if (n.Contains(q)) return 40;
            // letters in order ("spfy" -> "Spotify")
            int i = 0;
            foreach (char c in n) if (i < q.Length && c == q[i]) i++;
            return i == q.Length && q.Length >= 3 ? 15 : 0;
        }
    }
}
