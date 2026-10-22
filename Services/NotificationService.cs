using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Pebble
{
    sealed class IncomingNotification
    {
        public string App, AppId, Title, Body;
        public byte[] Logo;
        public DateTime At;
    }

    /// <summary>
    /// Mirrors Windows toast notifications into the island.
    /// Windows only allows this for apps installed as a package (Store/MSIX), so unpackaged runs report Unavailable.
    /// </summary>
    sealed class NotificationService
    {
        public readonly ConcurrentQueue<IncomingNotification> Incoming = new ConcurrentQueue<IncomingNotification>();
        public volatile string Status = "Starting…";
        public volatile bool Enabled = true;
        readonly Dictionary<string, byte[]> _logoCache = new Dictionary<string, byte[]>();

        public void Start() => Task.Run(Loop);

        async Task Loop()
        {
            UserNotificationListener listener;
            try
            {
                listener = UserNotificationListener.Current;
                var access = await listener.RequestAccessAsync();
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    Status = access == UserNotificationListenerAccessStatus.Denied
                        ? "Blocked in Windows Settings > Privacy > Notifications"
                        : "Waiting for permission";
                    return;
                }
            }
            catch (Exception e)
            {
                Status = "Needs the installed (Store) version of Pebble";
                Log.Write("notifications: " + e.Message);
                return;
            }

            Status = "On";
            var seen = new HashSet<uint>();
            bool first = true;
            while (true)
            {
                try
                {
                    var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                    foreach (var n in list)
                    {
                        if (!seen.Add(n.Id) || first || !Enabled) continue;
                        var item = await Read(n);
                        if (item != null) Incoming.Enqueue(item);
                    }
                    // forget ids that are gone so the set doesn't grow forever
                    var live = new HashSet<uint>(list.Select(n => n.Id));
                    seen.IntersectWith(live);
                    first = false;
                }
                catch (Exception e) { Log.Write("notifications poll: " + e.Message); }
                await Task.Delay(1200);
            }
        }

        async Task<IncomingNotification> Read(UserNotification n)
        {
            try
            {
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return null;
                var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (texts.Count == 0) return null;

                string app = "", appId = "";
                try { app = n.AppInfo.DisplayInfo.DisplayName; appId = n.AppInfo.AppUserModelId; } catch { }
                if (app == "Pebble") return null;

                byte[] logo = null;
                if (appId.Length > 0 && !_logoCache.TryGetValue(appId, out logo))
                {
                    try
                    {
                        var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
                        using var s = stream.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await s.CopyToAsync(ms);
                        logo = ms.ToArray();
                    }
                    catch { }
                    _logoCache[appId] = logo;
                }

                return new IncomingNotification
                {
                    App = app,
                    AppId = appId,
                    Title = texts[0],
                    Body = string.Join("  ", texts.Skip(1)),
                    Logo = logo,
                    At = DateTime.Now
                };
            }
            catch { return null; }
        }
    }
}
