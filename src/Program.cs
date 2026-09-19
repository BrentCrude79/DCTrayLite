using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DCTrayLite
{
    static class Program
    {
        // Show-events for the single-instance handoff, created in Main while
        // we hold the mutex. Live for the whole process lifetime. Both
        // namespaces are used: Local\ is the current one, Global\ is kept
        // so older builds (which used Global\) still hand off correctly.
        static EventWaitHandle _showEventLocal, _showEventGlobal;

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
            string localMutexName = @"Local\PwaTray_" + tag;
            string globalMutexName = @"Global\PwaTray_" + tag;

            // Single instance per user session. The Local\ mutex is the
            // current one; the Global\ mutex is also taken (best effort —
            // creating Global\ objects can fail for non-elevated callers)
            // so that older builds, which used Global\, still see us and
            // hand off instead of starting a second instance.
            bool createdNew;
            Mutex mutex;
            try
            {
                mutex = new Mutex(true, localMutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // Previous instance was killed (taskkill, crash) without
                // releasing the mutex. It's signaled now — take ownership
                // as the first instance instead of erroring out.
                mutex = new Mutex(true, localMutexName, out createdNew);
                createdNew = true;
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

            // We hold Local\. An older-version instance may hold Global\ —
            // try to take it: if it's already owned, the old instance is
            // running and we hand off to it instead of doubling up.
            Mutex globalMutex = null;
            bool globalNew = true;
            try
            {
                globalMutex = new Mutex(true, globalMutexName, out globalNew);
            }
            catch (AbandonedMutexException)
            {
                globalMutex = new Mutex(true, globalMutexName, out globalNew);
                globalNew = true; // predecessor died; we take it
            }
            catch
            {
                globalMutex = null; // can't create Global\ here — Local\ is enough
            }

            if (!globalNew)
            {
                try { SignalExistingInstance(tag, cfg.Name); }
                catch { }
                try { globalMutex.Close(); } catch { }
                try { mutex.Close(); } catch { }
                return;
            }

            // Cross-tag backstop: a sibling copy with a different config
            // name (e.g. the original Discord-Tray.exe) uses different
            // mutex names, so the mutex check above can't see it. If an
            // EARLIER sibling process exists, hand off to it instead of
            // doubling up. (StartTime comparison keeps two simultaneous
            // starters from both exiting.)
            Process sibling = FindEarlierSiblingProcess();
            if (sibling != null)
            {
                string sibPath = null;
                try { sibPath = sibling.MainModule.FileName; } catch { }
                bool signaled = SignalAllKnownEvents(tag);
                if (!signaled)
                {
                    MessageBox.Show(cfg.Name + " is already running (" +
                        (sibPath ?? "another copy") + ") — check the system tray.",
                        cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                try { if (globalMutex != null) globalMutex.Close(); } catch { }
                try { mutex.Close(); } catch { }
                return;
            }

            // Create the show-events NOW, while we hold the mutex and before
            // the main form exists — a second instance (e.g. a taskbar-pin
            // click) can then always signal us, even mid-startup. Clear any
            // stale signal left by a killed predecessor.
            _showEventLocal = CreateShowEvent(@"Local\PwaTrayShow_" + tag);
            _showEventGlobal = CreateShowEvent(@"Global\PwaTrayShow_" + tag);

            using (mutex)
            using (globalMutex)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(cfg, tag));
            }
        }

        static EventWaitHandle CreateShowEvent(string name)
        {
            try
            {
                bool evNew;
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name, out evNew);
                if (!evNew) ev.Reset();
                return ev;
            }
            catch { return null; }
        }

        /// <summary>
        /// Finds a running sibling copy from the DCTrayLite family that
        /// started before this process (different exe name / config tag, so
        /// the mutex check can't see it). Null when we're the earliest.
        /// </summary>
        static Process FindEarlierSiblingProcess()
        {
            Process self = Process.GetCurrentProcess();
            DateTime selfStart;
            try { selfStart = self.StartTime; }
            catch { selfStart = DateTime.MaxValue; }
            Process found = null;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self.Id) continue;
                    string n = p.ProcessName;
                    if (!(n.StartsWith("DCTrayLite", StringComparison.OrdinalIgnoreCase) ||
                          n.StartsWith("Discord-Tray", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    DateTime ost;
                    try { ost = p.StartTime; } catch { continue; }
                    if (ost < selfStart) { found = p; break; }
                }
                catch { }
            }
            return found;
        }

        /// <summary>
        /// Signals the show-event for every known config tag in both
        /// namespaces. Returns true if any running instance was signaled.
        /// </summary>
        static bool SignalAllKnownEvents(string ownTag)
        {
            string[] tags = { ownTag, "DCTrayLite", "Discord" };
            string[] nss = { "Local", "Global" };
            for (int i = 0; i < 20; i++)
            {
                foreach (var t in tags)
                    foreach (var ns in nss)
                    {
                        try
                        {
                            using (var ev = EventWaitHandle.OpenExisting(ns + @"\PwaTrayShow_" + t))
                            {
                                ev.Set();
                                return true;
                            }
                        }
                        catch { }
                    }
                Thread.Sleep(100);
            }
            return false;
        }

        /// <summary>
        /// Tells the running instance to show its window. Tries the Local\
        /// event first, then the Global\ one (older builds listen there).
        /// Retries briefly — the first instance may still be starting up.
        /// Falls back to a plain message instead of dying silently.
        /// </summary>
        static void SignalExistingInstance(string tag, string appName)
        {
            string[] events = { @"Local\PwaTrayShow_" + tag, @"Global\PwaTrayShow_" + tag };
            for (int i = 0; i < 20; i++)
            {
                foreach (var name in events)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(name))
                        {
                            ev.Set();
                            return;
                        }
                    }
                    catch { }
                }
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
