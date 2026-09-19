using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DCTrayLite
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Must run before any WebView2 type is touched.
            AssemblyLoader.Attach();

            string exePath = Assembly.GetExecutingAssembly().Location;
            var cfg = ConfigOverlay.Read(exePath);
            if (cfg == null)
            {
                MessageBox.Show(
                    "This copy of the tray template has not been configured yet.\n\n" +
                    "Use PwaTrayGen to build a finished app from a PWA shortcut.",
                    "Not configured", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string tag = Sanitize(cfg.Name);

            // Single instance per user session (Local\, not Global\ — a per-user
            // tray app has no business in the global namespace, and creating
            // Global\ objects can fail for non-elevated callers).
            bool createdNew;
            Mutex mutex;
            try
            {
                mutex = new Mutex(true, @"Local\PwaTray_" + tag, out createdNew);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start (single-instance check failed):\n" + ex.Message,
                    cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!createdNew)
            {
                // Another instance owns the app — ask it to show its window.
                // A second instance must never show an unhandled-exception
                // dialog, no matter what goes wrong here.
                try { SignalExistingInstance(tag, cfg.Name); }
                catch { }
                try { mutex.Close(); } catch { }
                return;
            }

            using (mutex)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(cfg, tag));
            }
        }

        /// <summary>
        /// Tells the running instance to show its window. Retries briefly —
        /// the first instance may still be starting up. Falls back to a plain
        /// message instead of dying silently.
        /// </summary>
        static void SignalExistingInstance(string tag, string appName)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(@"Local\PwaTrayShow_" + tag))
                    {
                        ev.Set();
                        return;
                    }
                }
                catch { }
                Thread.Sleep(100);
            }
            MessageBox.Show(appName + " is already running — check the system tray.",
                appName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "App";
            string r = Regex.Replace(s, @"[^A-Za-z0-9]+", "");
            if (r.Length == 0) r = "App";
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }
    }
}
