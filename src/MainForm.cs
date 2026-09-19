using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DCTrayLite
{
    /// <summary>
    /// Discord in a tray window. The wrapper owns the mic AND speakers at
    /// the OS level: the mic starts muted and only unmutes while a
    /// push-to-talk hotkey is held or the mute toggle is on; the deafen
    /// toggle additionally mutes the speakers. Discord itself stays on
    /// open mic / voice activity — no Discord keybinds, no page scraping.
    /// </summary>
    sealed class MainForm : Form
    {
        readonly ConfigOverlay.AppConfig _cfg;
        readonly string _tag;
        readonly WebView2 _web;
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _muteItem;
        readonly ToolStripMenuItem _deafenItem;
        readonly MicController _mic;
        CoreWebView2Environment _env;
        bool _quitting;

        // Wrapper-side audio state: micLive = toggle on OR a PTT key held;
        // deafened mutes speakers AND the mic (hard block, like Discord).
        List<HotkeyBinding> _bindings = new List<HotkeyBinding>();
        bool _micLive;
        bool _deafened;
        readonly HashSet<int> _pttHeld = new HashSet<int>(); // RegisterHotKey ids
        readonly Dictionary<int, int> _registered = new Dictionary<int, int>(); // id -> binding index
        readonly System.Windows.Forms.Timer _pttTimer;
        Icon _iconMuted, _iconDeafened, _iconLive, _iconPtt;
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        // Global hotkeys, OS-managed (no hook): RegisterHotKey posts
        // WM_HOTKEY to this window. Registration failures are reported
        // instead of failing silently.
        const int WM_HOTKEY = 0x0312;
        const int HotkeyIdBase = 0xD0;
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        // Dark title bar so the native chrome melts into dark web apps.
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 1809+
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809 pre-20H1
        const int DWMWA_CAPTION_COLOR = 35;               // Win11+
        const int DWMWA_TEXT_COLOR = 36;                  // Win11+

        public MainForm(ConfigOverlay.AppConfig cfg, string tag)
        {
            _cfg = cfg;
            _tag = tag;

            Text = cfg.Name;
            ShowIcon = false; // cleaner title bar; taskbar still shows the exe icon
            BackColor = Color.FromArgb(0x1E, 0x1F, 0x22); // no white flash before the page loads
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 800);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            // Mic state icons: red slash on dark = wrapper muted; red slash on
            // dark red = deafened (mic + speakers); green = mic live;
            // bright green = push-to-talk held.
            _iconMuted = MakeStatusIcon(Color.FromArgb(0x2C, 0x2F, 0x33), 1f, true);
            _iconDeafened = MakeStatusIcon(Color.FromArgb(0x5A, 0x1F, 0x1F), 1f, true);
            _iconLive = MakeStatusIcon(Color.FromArgb(0x3B, 0xA5, 0x5D), 1f, false);
            _iconPtt = MakeStatusIcon(Color.FromArgb(0x57, 0xF2, 0x87), 1f, false);
            _tray = new NotifyIcon
            {
                Icon = _iconMuted,
                Text = cfg.Name.Length > 63 ? cfg.Name.Substring(0, 63) : cfg.Name,
                Visible = true,
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open " + cfg.Name, null, (s, e) => ShowApp());
            _muteItem = new ToolStripMenuItem("Unmute mic", null, (s, e) =>
            {
                _micLive = !_micLive;
                ApplyMicState();
            });
            menu.Items.Add(_muteItem);
            _deafenItem = new ToolStripMenuItem("Deafen", null, (s, e) =>
            {
                _deafened = !_deafened;
                ApplyMicState();
            });
            menu.Items.Add(_deafenItem);
            var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
            startup.Click += (s, e) => { SetStartup(!startup.Checked); startup.Checked = IsStartupEnabled(); };
            menu.Items.Add(startup);
            menu.Items.Add("Hotkeys\u2026", null, (s, e) =>
            {
                using (var d = new HotkeyConfigDialog(_bindings))
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        _bindings = d.Bindings;
                        SaveHotkeys(_bindings);
                        RegisterAllHotkeys();
                    }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => QuitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowApp();

            // Take control of the mic and speakers: save current state, then
            // mute the mic. On quit the saved state is restored.
            _mic = new MicController();
            if (_mic.Error != null)
            {
                MessageBox.Show(
                    "Could not take control of the microphone:\n" + _mic.Error +
                    "\n\nHotkeys will not work until this is fixed.",
                    cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                _mic.SetMicMuted(true);
            }

            _bindings = LoadHotkeys();

            // PTT release detection: RegisterHotKey only fires on key down,
            // so poll the physical key state until it comes back up.
            _pttTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _pttTimer.Tick += PttTimer_Tick;

            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += (s, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
            FormClosed += (s, e) =>
            {
                try { _pttTimer.Stop(); } catch { }
                UnregisterAllHotkeys();
                try { _mic.Dispose(); } catch { } // restores mic + speaker prior state
                _tray.Visible = false;
                foreach (var ic in new[] { _iconMuted, _iconDeafened, _iconLive, _iconPtt })
                    try { if (ic != null) DestroyIcon(ic.Handle); } catch { }
            };

            Shown += async (s, e) => await InitWebAsync();
            StartShowWatcher();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int caption = 0x00221E1E; // #1e1f22 — near-black, blends into dark apps
                    int text = 0x00999999;    // dim gray title text
                    DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                    DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                }
            }
            catch { }
            RegisterAllHotkeys();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                OnGlobalHotkey(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        void OnGlobalHotkey(int id)
        {
            int idx;
            if (!_registered.TryGetValue(id, out idx) || idx >= _bindings.Count) return;
            var b = _bindings[idx];
            if (b.Action == HotkeyAction.ToggleMute)
            {
                _micLive = !_micLive;
                ApplyMicState();
            }
            else if (b.Action == HotkeyAction.ToggleDeafen)
            {
                _deafened = !_deafened;
                ApplyMicState();
            }
            else if (b.Action == HotkeyAction.PushToTalk)
            {
                if (_pttHeld.Add(id))
                {
                    ApplyMicState();
                    _pttTimer.Start();
                }
            }
        }

        void PttTimer_Tick(object sender, EventArgs e)
        {
            bool changed = false;
            foreach (int id in new List<int>(_pttHeld))
            {
                int idx;
                if (!_registered.TryGetValue(id, out idx) || idx >= _bindings.Count)
                {
                    _pttHeld.Remove(id);
                    changed = true;
                    continue;
                }
                // High bit of GetAsyncKeyState = key physically down.
                if ((GetAsyncKeyState(_bindings[idx].Vk) & 0x8000) == 0)
                {
                    _pttHeld.Remove(id);
                    changed = true;
                }
            }
            if (_pttHeld.Count == 0) _pttTimer.Stop();
            if (changed) ApplyMicState();
        }

        void ApplyMicState()
        {
            // Deafen is a hard block: mic muted AND speakers muted, PTT
            // doesn't override it (matches Discord semantics).
            bool live = (_micLive || _pttHeld.Count > 0) && !_deafened;
            _mic.SetMicMuted(!live);
            _mic.SetSpeakersMuted(_deafened);
            _muteItem.Text = _micLive ? "Mute mic" : "Unmute mic";
            _deafenItem.Text = _deafened ? "Undeafen" : "Deafen";
            UpdateTrayIcon();
        }

        void RegisterAllHotkeys()
        {
            UnregisterAllHotkeys();
            if (!IsHandleCreated) return;
            var failed = new List<string>();
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b == null || !b.IsSet || b.Action == HotkeyAction.None) continue;
                int id = HotkeyIdBase + i;
                if (RegisterHotKey(Handle, id, b.Mod, (uint)b.Vk))
                    _registered[id] = i;
                else
                    failed.Add(b.Label ?? HotkeyFormat.Format(b.Mod, b.Vk));
            }
            if (failed.Count > 0)
                MessageBox.Show(
                    "These hotkeys could not be registered — another app is " +
                    "already using them:\n\u2022 " + string.Join("\n\u2022 ", failed),
                    _cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void UnregisterAllHotkeys()
        {
            if (!IsHandleCreated) return;
            foreach (int id in new List<int>(_registered.Keys))
            {
                try { UnregisterHotKey(Handle, id); } catch { }
            }
            _registered.Clear();
            _pttHeld.Clear();
        }

        async Task InitWebAsync()
        {
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PwaTray", _tag, "EBWebView");
                Directory.CreateDirectory(dataDir);
                _env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await _web.EnsureCoreWebView2Async(_env);
                _web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                _web.Source = new Uri(_cfg.Url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not start the WebView2 engine.\n\n" + ex.Message +
                    "\n\nThe WebView2 runtime is built into Windows 10/11 — " +
                    "installing Microsoft Edge (or the evergreen WebView2 runtime) fixes this.",
                    _cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Same-site popups (OAuth, etc.) stay in-app; everything else goes
            // to the real browser so random links don't get trapped.
            try
            {
                var req = new Uri(e.Uri);
                var home = new Uri(_cfg.Url);
                e.Handled = true;
                if (string.Equals(req.Host, home.Host, StringComparison.OrdinalIgnoreCase))
                    new PopupForm(_env, e.Uri, _cfg.Name).Show();
                else
                    System.Diagnostics.Process.Start(e.Uri);
            }
            catch { e.Handled = true; }
        }

        void ShowApp()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void QuitApp()
        {
            _quitting = true;
            Close();
        }

        void StartShowWatcher()
        {
            // The show-event is created in Program.Main before this form
            // exists, so a second instance can always signal us — even
            // mid-startup. The loop never dies: a failed invoke must not
            // break the single-instance handoff.
            var t = new Thread(() =>
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(@"Local\PwaTrayShow_" + _tag))
                    {
                        while (!IsDisposed)
                        {
                            ev.WaitOne();
                            if (IsDisposed) return;
                            try { BeginInvoke((Action)ShowApp); }
                            catch { }
                        }
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        bool IsStartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(_cfg.Name) != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(_cfg.Name, "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue(_cfg.Name, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update startup setting:\n" + ex.Message,
                    _cfg.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void UpdateTrayIcon()
        {
            try
            {
                bool ptt = _pttHeld.Count > 0 && !_deafened;
                bool live = (_micLive || ptt) && !_deafened;
                Icon icon;
                string state;
                if (_deafened) { icon = _iconDeafened; state = "deafened"; }
                else if (!live) { icon = _iconMuted; state = "mic muted"; }
                else if (ptt) { icon = _iconPtt; state = "talking (push-to-talk)"; }
                else { icon = _iconLive; state = "mic live"; }
                _tray.Icon = icon;
                string tip = _cfg.Name + " - " + state;
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            catch { }
        }

        /// <summary>Draws a 32x32 status icon: colored disc + mic glyph.</summary>
        static Icon MakeStatusIcon(Color bg, float micAlpha, bool slash)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(bg))
                    g.FillEllipse(brush, 1, 1, 30, 30);
                var mic = Color.FromArgb((int)(255 * micAlpha), Color.White);
                using (var b = new SolidBrush(mic))
                {
                    g.FillRectangle(b, 13, 7, 6, 9);   // capsule body
                    g.FillEllipse(b, 13, 4, 6, 6);     // capsule top
                    g.FillEllipse(b, 13, 13, 6, 6);    // capsule bottom
                }
                using (var p = new Pen(mic, 2))
                {
                    g.DrawArc(p, 10, 11, 12, 9, 0, 180); // cradle
                    g.DrawLine(p, 16, 20, 16, 26);      // stand
                    g.DrawLine(p, 12, 26, 20, 26);      // base
                }
                if (slash)
                    using (var p = new Pen(Color.FromArgb(0xED, 0x42, 0x45), 4))
                        g.DrawLine(p, 5, 27, 27, 5);
            }
            var icon = Icon.FromHandle(bmp.GetHicon());
            bmp.Dispose();
            return icon;
        }

        sealed class KeyDto
        {
            public int vk { get; set; }
            public int scan { get; set; }
            public bool ext { get; set; }
        }

        sealed class HotkeyDto
        {
            public uint mod { get; set; }
            public int vk { get; set; }
            public string label { get; set; }
            public int action { get; set; }
            // Legacy v1.2 chord format / v1.1 single-key format.
            public List<KeyDto> keys { get; set; }
            public int mode { get; set; }
        }

        string HotkeyPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PwaTray", _tag, "hotkeys.json");

        List<HotkeyBinding> LoadHotkeys()
        {
            var list = new List<HotkeyBinding>();
            try
            {
                string p = HotkeyPath();
                if (File.Exists(p))
                {
                    var dtos = new JavaScriptSerializer()
                        .Deserialize<List<HotkeyDto>>(File.ReadAllText(p));
                    if (dtos != null)
                        foreach (var d in dtos)
                        {
                            var b = Migrate(d);
                            if (b != null && b.IsSet) list.Add(b);
                        }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Best-effort migration from the v1.1/v1.2 binding formats.</summary>
        static HotkeyBinding Migrate(HotkeyDto d)
        {
            if (d == null) return null;
            // Current format {mod, vk, label, action} or legacy v1.1
            // single-key {vk, mode}: the two are distinguished because only
            // the new format ever writes "action" and only v1.1 writes "mode".
            if (d.vk != 0 && d.keys == null)
            {
                int action = d.action != 0 ? d.action
                    : d.mode == 1 ? HotkeyAction.PushToTalk : HotkeyAction.None;
                return new HotkeyBinding
                {
                    Mod = d.mod,
                    Vk = d.vk,
                    Action = action,
                    Label = string.IsNullOrEmpty(d.label)
                        ? HotkeyFormat.Format(d.mod, d.vk) : d.label,
                };
            }
            // Legacy v1.2 chord format {keys:[{vk,scan,ext}], mode}.
            var vks = new List<int>();
            if (d.keys != null)
                foreach (var k in d.keys)
                    if (k != null) vks.Add(k.vk);
            if (vks.Count == 0) return null;
            uint mod = 0;
            int main = 0;
            foreach (int vk in vks)
            {
                switch (vk)
                {
                    case 0x10: case 0xA0: case 0xA1: mod |= HotkeyFormat.MOD_SHIFT; break;
                    case 0x11: case 0xA2: case 0xA3: mod |= HotkeyFormat.MOD_CONTROL; break;
                    case 0x12: case 0xA4: case 0xA5: mod |= HotkeyFormat.MOD_ALT; break;
                    case 0x5B: case 0x5C: mod |= HotkeyFormat.MOD_WIN; break;
                    default: main = vk; break; // last non-modifier wins
                }
            }
            if (main == 0) return null; // modifier-only chords can't register
            return new HotkeyBinding
            {
                Mod = mod,
                Vk = main,
                Action = d.mode == 1 ? HotkeyAction.PushToTalk : HotkeyAction.None,
                Label = HotkeyFormat.Format(mod, main),
            };
        }

        void SaveHotkeys(List<HotkeyBinding> bindings)
        {
            try
            {
                var dtos = new List<HotkeyDto>();
                foreach (var b in bindings)
                {
                    if (b == null || !b.IsSet) continue;
                    dtos.Add(new HotkeyDto
                    {
                        mod = b.Mod,
                        vk = b.Vk,
                        label = b.Label ?? "",
                        action = b.Action,
                    });
                }
                string p = HotkeyPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, new JavaScriptSerializer().Serialize(dtos));
            }
            catch { }
        }
    }
}
