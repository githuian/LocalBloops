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
                Debug.WriteLine("Unhandled: " + e.Exception);
                e.Handled = true;
            };

            using var controller = new AppController(app.Dispatcher);
            app.Startup += (_, _) => controller.Start(args);
            app.Run();
        }
    }
}
