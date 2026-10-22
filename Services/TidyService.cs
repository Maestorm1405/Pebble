using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Pebble
{
    /// <summary>Sorts loose files in Downloads into category folders. Unknown file types are left alone.</summary>
    static class TidyService
    {
        static readonly Dictionary<string, string> Category = Build(new Dictionary<string, string[]>
        {
            ["Images"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".svg", ".tif", ".tiff", ".ico", ".avif", ".raw", ".psd" },
            ["Videos"] = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".m4v", ".flv" },
            ["Music"] = new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus" },
            ["Documents"] = new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".epub", ".pages", ".key", ".numbers" },
            ["Archives"] = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso" },
            ["Installers"] = new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" },
            ["Code"] = new[] { ".py", ".js", ".ts", ".json", ".html", ".css", ".cs", ".cpp", ".c", ".h", ".java", ".sh", ".ps1", ".bat", ".xml", ".yml", ".yaml", ".lua" },
        });

        static readonly string[] InProgress = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut" };

        static Dictionary<string, string> Build(Dictionary<string, string[]> map)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map) foreach (var ext in kv.Value) d[ext] = kv.Key;
            return d;
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string DownloadsFolder
        {
            get
            {
                try
                {
                    if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr p) == 0)
                    {
                        string s = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        return s;
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        /// <summary>Moves eligible files; returns what was moved (for undo).</summary>
        public static List<MoveRecord> Run(HashSet<string> ignore)
        {
            var moved = new List<MoveRecord>();
            string root = DownloadsFolder;
            if (!Directory.Exists(root)) return moved;

            foreach (var file in Directory.EnumerateFiles(root))
            {
                try
                {
                    if (ignore.Contains(file)) continue;   // the user undid this one before
                    var info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    string ext = info.Extension;
                    if (InProgress.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!Category.TryGetValue(ext, out string cat)) continue;
                    // leave fresh files alone: the browser may still be writing, or the user is about to open it
                    if ((DateTime.Now - info.LastWriteTime).TotalMinutes < 2 || (DateTime.Now - info.CreationTime).TotalMinutes < 2) continue;
                    if (IsLocked(info)) continue;

                    string dir = Path.Combine(root, cat);
                    Directory.CreateDirectory(dir);
                    string dest = Unique(Path.Combine(dir, info.Name));
                    File.Move(file, dest);
                    moved.Add(new MoveRecord { From = file, To = dest });
                }
                catch (Exception e) { Log.Write("tidy: " + e.Message); }
            }
            return moved;
        }

        public static readonly string[] Folders = { "Installers", "Documents", "Images", "Videos", "Music", "Archives", "Code" };

        /// <summary>Loose files that tidy would move right now, and how many files each category folder holds.</summary>
        public static (int Loose, Dictionary<string, int> PerFolder) Stats(HashSet<string> ignore)
        {
            var per = new Dictionary<string, int>();
            int loose = 0;
            string root = DownloadsFolder;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root))
                    if (!ignore.Contains(f) && Category.ContainsKey(Path.GetExtension(f)) && !InProgress.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) loose++;
                foreach (var cat in Folders)
                {
                    string d = Path.Combine(root, cat);
                    per[cat] = Directory.Exists(d) ? Directory.EnumerateFiles(d).Count() : 0;
                }
            }
            catch { }
            return (loose, per);
        }

        public static int Undo(List<MoveRecord> moves)
        {
            int n = 0;
            foreach (var m in moves)
            {
                try
                {
                    if (!File.Exists(m.To)) continue;
                    File.Move(m.To, Unique(m.From));
                    n++;
                }
                catch (Exception e) { Log.Write("undo: " + e.Message); }
            }
            return n;
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        static bool IsLocked(FileInfo f)
        {
            try { using (f.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch { return true; }
        }
    }
}
