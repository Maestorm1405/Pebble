using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Control;

namespace Pebble
{
    sealed class MediaSnapshot
    {
        public string Title = "", Artist = "", App = "", Status = "None";
        public double Pos, Dur;
        public DateTime Updated;
    }

    /// <summary>Polls Windows media controls (Spotify, browsers, Media Player...) and Bluetooth connections.</summary>
    sealed class MediaService
    {
        GlobalSystemMediaTransportControlsSessionManager _mgr;
        volatile GlobalSystemMediaTransportControlsSession _session;

        public volatile MediaSnapshot Current = new MediaSnapshot();
        public volatile byte[] Art;
        public int ArtVer;
        public readonly ConcurrentQueue<string> BtEvents = new ConcurrentQueue<string>();

        public void Start()
        {
            Task.Run(MediaLoop);
            Task.Run(BluetoothLoop);
        }

        async Task MediaLoop()
        {
            try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
            catch (Exception e) { Log.Write("media manager: " + e.Message); }

            string lastKey = null;
            bool artDone = false;
            while (true)
            {
                try
                {
                    var s = _mgr?.GetCurrentSession();
                    _session = s;
                    if (s != null)
                    {
                        var p = await s.TryGetMediaPropertiesAsync();
                        var pb = s.GetPlaybackInfo();
                        var tl = s.GetTimelineProperties();
                        string key = p.Title + "|" + p.Artist;
                        if (key != lastKey) { lastKey = key; artDone = false; Art = null; Interlocked.Increment(ref ArtVer); }
                        if (!artDone && p.Thumbnail != null)
                        {
                            try
                            {
                                using var st = await p.Thumbnail.OpenReadAsync();
                                using var ns = st.AsStreamForRead();
                                using var ms = new MemoryStream();
                                await ns.CopyToAsync(ms);
                                if (ms.Length > 0) { Art = ms.ToArray(); Interlocked.Increment(ref ArtVer); artDone = true; }
                            }
                            catch { /* thumbnail not ready yet; retry next poll */ }
                        }
                        Current = new MediaSnapshot
                        {
                            Title = p.Title ?? "", Artist = p.Artist ?? "", App = s.SourceAppUserModelId ?? "",
                            Status = pb.PlaybackStatus.ToString(),
                            Pos = tl.Position.TotalSeconds, Dur = (tl.EndTime - tl.StartTime).TotalSeconds,
                            Updated = tl.LastUpdatedTime.UtcDateTime
                        };
                    }
                    else
                    {
                        if (lastKey != null) { lastKey = null; Art = null; Interlocked.Increment(ref ArtVer); }
                        Current = new MediaSnapshot();
                    }
                }
                catch (Exception e) { Log.Write("media: " + e.Message); }
                await Task.Delay(500);
            }
        }

        async Task BluetoothLoop()
        {
            string[] selectors;
            try
            {
                selectors = new[]
                {
                    BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                    BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
                };
            }
            catch (Exception e) { Log.Write("bluetooth: " + e.Message); return; }

            HashSet<string> known = null;
            while (true)
            {
                try
                {
                    var names = new HashSet<string>();
                    foreach (var sel in selectors)
                        foreach (var d in await DeviceInformation.FindAllAsync(sel))
                            if (!string.IsNullOrEmpty(d.Name)) names.Add(d.Name);
                    if (known != null)
                    {
                        foreach (var n in names) if (!known.Contains(n)) BtEvents.Enqueue("+" + n);
                        foreach (var n in known) if (!names.Contains(n)) BtEvents.Enqueue("-" + n);
                    }
                    known = names;
                }
                catch { }
                await Task.Delay(3000);
            }
        }

        public void TogglePlayPause() { var s = _session; if (s != null) _ = s.TryTogglePlayPauseAsync(); else Native.Key(0xB3); }
        public void Next() { var s = _session; if (s != null) _ = s.TrySkipNextAsync(); else Native.Key(0xB0); }
        public void Previous() { var s = _session; if (s != null) _ = s.TrySkipPreviousAsync(); else Native.Key(0xB1); }

        public void Seek(double fraction)
        {
            var s = _session; var c = Current;
            if (s != null && c.Dur > 0) _ = s.TryChangePlaybackPositionAsync((long)(fraction * c.Dur * TimeSpan.TicksPerSecond));
        }
    }
}
