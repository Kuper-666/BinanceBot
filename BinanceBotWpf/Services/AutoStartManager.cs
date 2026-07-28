using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BinanceBotWpf.Services
{
    internal static class AutoStartManager
    {
        private const string AppName = "BinanceBotWpf";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey (@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                    if (key == null) return false;
                    var value = key.GetValue (AppName) as string;
                    return !string.IsNullOrEmpty (value) && value.Equals (GetShortcutTarget (), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        public static void Enable ()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey (@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                    key.SetValue (AppName, GetShortcutTarget ());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"AutoStart enable error: {ex.Message}");
            }
        }

        public static void Disable ()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey (@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                    key.DeleteValue (AppName, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"AutoStart disable error: {ex.Message}");
            }
        }

        private static string GetShortcutTarget ()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess ().MainModule?.FileName;
            if (!string.IsNullOrEmpty (exePath) && exePath.EndsWith (".dll", StringComparison.OrdinalIgnoreCase))
            {
                string possibleExe = Path.ChangeExtension (exePath, ".exe");
                if (File.Exists (possibleExe))
                    return possibleExe;
            }
            return exePath ?? AssemblyLocation;
        }

        private static string AssemblyLocation =>
            new Uri (typeof (AutoStartManager).Assembly.Location).LocalPath;
    }
}
