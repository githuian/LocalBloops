using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using LocalBloops.Laps;

namespace LocalBloops
{
    /// <summary>
    /// Small diagnostic log at %LocalAppData%\LocalBloops\log.txt (rolled over to log.old.txt at
    /// 1 MB). Errors the app swallows to stay running are written here so they can be found later.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();

        public static string FilePath { get; } = Path.Combine(AppPaths.Root, "log.txt");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : message + ": " + ex);

        private static void Write(string level, string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}";
            Debug.WriteLine(line);
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Root);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = Path.Combine(AppPaths.Root, "log.old.txt");
                        File.Copy(FilePath, old, overwrite: true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        /// <summary>
        /// Every 5 s, queues a Background-priority no-op (the priority DispatcherTimers use) and logs
        /// when it runs late, which would starve delayed saves and other timers.
        /// </summary>
        public static void StartDispatcherWatchdog(Dispatcher ui)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(5000);
                    var sw = Stopwatch.StartNew();
                    DispatcherOperation op;
                    try
                    {
                        op = ui.BeginInvoke(DispatcherPriority.Background, new Action(() => { }));
                    }
                    catch
                    {
                        return; // dispatcher shut down
                    }
                    if (op.Wait(TimeSpan.FromSeconds(10)) != DispatcherOperationStatus.Completed)
                        Info("Watchdog: UI thread hasn't run background work for 10 s");
                    else if (sw.ElapsedMilliseconds > 1000)
                        Info($"Watchdog: background work delayed {sw.ElapsedMilliseconds} ms");
                }
            })
            { IsBackground = true, Name = "dispatcher watchdog" };
            t.Start();
        }
    }
}
