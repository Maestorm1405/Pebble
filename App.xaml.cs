using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Pebble
{
    public partial class App : Application
    {
        Mutex _mutex;
        static string CrashFile => Path.Combine(Settings.Folder, "crashes.txt");

        protected override void OnStartup(StartupEventArgs e)
        {
            bool recovering = e.Args.Contains("--restart");
            Log.Write($"start: {Environment.ProcessPath} args=[{string.Join(" ", e.Args)}] cwd={Environment.CurrentDirectory}");
            _mutex = new Mutex(true, @"Local\Pebble_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                // a restart waits for the old copy to finish exiting; a normal second launch just quits
                bool got = false;
                if (recovering)
                {
                    try { got = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { got = true; }
                }
                if (!got) { Log.Write("start: another Pebble is already running, exiting"); Shutdown(); return; }
            }

            // crash protection:
            // 1. errors on the UI thread are logged and Pebble keeps running
            DispatcherUnhandledException += (s, a) => { Log.Write("ui: " + a.Exception); a.Handled = true; };
            // 2. errors in background tasks are logged and swallowed
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Write("task: " + a.Exception); a.SetObserved(); };
            // 3. anything truly fatal restarts Pebble (unless it keeps crashing)
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                Log.Write("fatal: " + a.ExceptionObject);
                if (ShouldAutoRestart()) Relaunch();
            };

            // Windows signing out / shutting down
            SessionEnding += (s, a) => Log.Write("exit: Windows session ending (" + a.ReasonSessionEnding + ")");

            base.OnStartup(e);
            try { new MainWindow().Show(); }
            catch (Exception ex) { Log.Write("start failed: " + ex); throw; }
        }

        /// <summary>Allow at most 3 automatic restarts in 5 minutes, so a persistent bug can't cause a restart loop.</summary>
        static bool ShouldAutoRestart()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var now = DateTime.UtcNow;
                var recent = File.Exists(CrashFile)
                    ? File.ReadAllLines(CrashFile).Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                        .Where(t => (now - t).TotalMinutes < 5).ToList()
                    : new System.Collections.Generic.List<DateTime>();
                recent.Add(now);
                File.WriteAllLines(CrashFile, recent.Select(t => t.ToString("o")));
                return recent.Count <= 3;
            }
            catch { return true; }
        }

        public static void Relaunch()
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--restart") { UseShellExecute = false }); }
            catch (Exception e) { Log.Write("relaunch: " + e.Message); }
        }

        /// <summary>Used by "Restart Pebble" in the tray and Settings.</summary>
        public void Restart()
        {
            Relaunch();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Write($"exit: code {e.ApplicationExitCode}");
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
