using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace LocalBloops
{
    /// <summary>
    /// "Start with Windows" via the per-user Run key (HKCU\...\Run). No admin rights needed, and the
    /// registry value itself is the setting, so it stays correct if the user removes it in Task Manager.
    /// </summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LocalBloops";

        /// <summary>LocalBloops.exe, even when launched as "dotnet LocalBloops.dll".</summary>
        private static string? ExePath
        {
            get
            {
                var p = Environment.ProcessPath;
                if (p != null && !string.Equals(System.IO.Path.GetFileName(p), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    return p;
                var apphost = System.IO.Path.Combine(AppContext.BaseDirectory, "LocalBloops.exe");
                return System.IO.File.Exists(apphost) ? apphost : null;
            }
        }

        private static string? Command => ExePath != null ? "\"" + ExePath + "\"" : null;

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Startup: " + ex.Message);
                    return false;
                }
            }
        }

        /// <returns>False if the registry couldn't be written.</returns>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled)
                {
                    var cmd = Command;
                    if (cmd == null) return false;
                    key.SetValue(ValueName, cmd, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Startup: " + ex.Message);
                return false;
            }
        }

        /// <summary>If enabled but pointing at an old location (the exe was moved or rebuilt elsewhere), repoint it here.</summary>
        public static void RefreshPathIfEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key?.GetValue(ValueName) is not string current) return;
                var cmd = Command;
                if (cmd != null && !string.Equals(current, cmd, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, cmd, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Startup: " + ex.Message);
            }
        }
    }
}
