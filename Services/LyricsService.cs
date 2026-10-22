using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pebble
{
    sealed class LyricLine
    {
        public double Time;
        public string Text;
    }

    /// <summary>Time-synced lyrics from LRCLIB (free, no key).</summary>
    sealed class LyricsService
    {
        static readonly HttpClient Http = CreateClient();
        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);
        readonly Dictionary<string, List<LyricLine>> _cache = new Dictionary<string, List<LyricLine>>();
        readonly object _gate = new object();

        public volatile string Key;
        public volatile List<LyricLine> Lines;
        public volatile string Status = "";
        public int Ver;

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Pebble/1.0 (Windows Dynamic Island)");
            return c;
        }

        public void Request(string title, string artist, double duration)
        {
            string key = title + "|" + artist;
            if (key == Key) return;
            Key = key;
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var hit))
                {
                    Lines = hit; Status = hit == null ? "No synced lyrics for this song" : "";
                    Interlocked.Increment(ref Ver);
                    return;
                }
            }
            Lines = null; Status = "Finding lyrics…"; Interlocked.Increment(ref Ver);
            Task.Run(() => Fetch(key, title, artist, duration));
        }

        async Task Fetch(string key, string title, string artist, double duration)
        {
            List<LyricLine> lines = null;
            try
            {
                string clean = Clean(title);
                // the exact-match endpoint needs an artist; without one go straight to search
                if (!string.IsNullOrWhiteSpace(artist))
                {
                    lines = await Get(title, artist, duration);
                    if (lines == null && clean != title) lines = await Get(clean, artist, duration);
                }
                if (lines == null) lines = await Search(clean, artist);
                lock (_gate) _cache[key] = lines;
                if (Key == key) { Lines = lines; Status = lines == null ? "No synced lyrics for this song" : ""; Interlocked.Increment(ref Ver); }
            }
            catch (Exception e)
            {
                Log.Write("lyrics: " + e.Message);
                if (Key == key) { Status = "Lyrics unavailable right now"; Interlocked.Increment(ref Ver); }
            }
        }

        static async Task<List<LyricLine>> Get(string title, string artist, double duration)
        {
            string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
            if (duration > 0) url += "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture);
            using var resp = await Http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return FromJson(doc.RootElement);
        }

        static async Task<List<LyricLine>> Search(string title, string artist)
        {
            string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
            if (!string.IsNullOrWhiteSpace(artist)) url += "&artist_name=" + Uri.EscapeDataString(artist);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var lines = FromJson(item);
                if (lines != null) return lines;
            }
            return null;
        }

        static List<LyricLine> FromJson(JsonElement e)
        {
            if (e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var lines = Parse(s.GetString());
                if (lines.Count > 0) return lines;
            }
            return null;
        }

        /// <summary>"Song - Remastered 2011" / "Song (feat. X)" -> "Song"</summary>
        static string Clean(string title)
        {
            string t = Regex.Replace(title, @"\s*[\(\[](feat|ft|with|from|remaster)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) t = t[..dash];
            return t.Trim();
        }

        public static List<LyricLine> Parse(string lrc)
        {
            var list = new List<LyricLine>();
            foreach (var raw in (lrc ?? "").Split('\n'))
            {
                var ms = Stamp.Matches(raw);
                if (ms.Count == 0) continue;
                string text = Stamp.Replace(raw, "").Trim();
                foreach (Match m in ms)
                {
                    double t = int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    list.Add(new LyricLine { Time = t, Text = text });
                }
            }
            return list.OrderBy(l => l.Time).ToList();
        }

        /// <summary>Index of the line being sung at time t (or -1 before the first line).</summary>
        public static int IndexAt(List<LyricLine> lines, double t)
        {
            int lo = 0, hi = lines.Count - 1, ans = -1;
            t += 0.25; // tiny lead so the line appears as it starts
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid].Time <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return ans;
        }
    }
}
