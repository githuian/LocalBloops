using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace LocalBloops
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            using var singleInstance = new Mutex(true, @"Local\LocalBloops.SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("LocalBloops is already running (see the tray icon).", "LocalBloops",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Error("Unhandled exception on the UI thread (ignored to keep running)", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);

            Log.Info($"LocalBloops {typeof(Program).Assembly.GetName().Version} starting from {Environment.ProcessPath}");
            Log.StartDispatcherWatchdog(app.Dispatcher);

            using var controller = new AppController(app.Dispatcher);
            app.Startup += (_, _) => controller.Start(args);
            app.Run();
        }
    }
}
