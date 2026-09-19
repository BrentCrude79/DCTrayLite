using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DCTrayLite
{
    /// <summary>
    /// Binds up to four global hotkeys. Each hotkey is modifiers
    /// (Ctrl/Alt/Shift) plus ONE main key — the shape Windows'
    /// RegisterHotKey API accepts, so bindings register reliably and
    /// conflicts surface instead of failing silently.
    ///
    /// Capture runs in ProcessCmdKey (the earliest interception point in
    /// WinForms — it sees keys before any focused control), not in KeyDown,
    /// so it works no matter which row button has focus.
    /// </summary>
    sealed class HotkeyConfigDialog : Form
    {
        public List<HotkeyBinding> Bindings { get; }

        const int MaxBindings = 4;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        readonly Label[] _keyLabels = new Label[MaxBindings];
        readonly Button[] _setButtons = new Button[MaxBindings];
        readonly Button[] _clearButtons = new Button[MaxBindings];
        readonly ComboBox[] _actionBoxes = new ComboBox[MaxBindings];
        int _capturing = -1;
        readonly HashSet<Keys> _downKeys = new HashSet<Keys>();

        static readonly string[] ActionNames =
            { "\u2014", "Push-to-talk", "Mute toggle", "Deafen toggle" };

        public HotkeyConfigDialog(List<HotkeyBinding> current)
        {
            Bindings = new List<HotkeyBinding>(current ?? new List<HotkeyBinding>());
            while (Bindings.Count < MaxBindings) Bindings.Add(new HotkeyBinding());
            for (int i = 0; i < MaxBindings; i++)
                if (Bindings[i] == null) Bindings[i] = new HotkeyBinding();

            Text = "Global Hotkeys (v1.3)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(486, 330);

            var info = new Label
            {
                Text = "Click Set\u2026, then press the combo in one motion:\r\n" +
                       "HOLD the modifiers (Ctrl / Alt / Shift) and TAP ONE KEY\r\n" +
                       "(letter, number, F-key\u2026). Example: hold Ctrl+Alt, tap M.\r\n" +
                       "Windows requires a non-modifier key \u2014 modifiers alone\r\n" +
                       "can't register. Esc cancels.\r\n\r\n" +
                       "Push-to-talk: mic open while held. Mute toggle / Deafen\r\n" +
                       "toggle: flip on/off per press (deafen also mutes speakers).\r\n" +
                       "Set Discord itself to Voice Activity (open mic).",
                Location = new Point(12, 10),
                Size = new Size(462, 148),
            };
            Controls.Add(info);

            for (int i = 0; i < MaxBindings; i++)
            {
                int y = 164 + i * 34;
                int idx = i;
                Controls.Add(new Label
                {
                    Text = "Hotkey " + (i + 1),
                    Location = new Point(12, y + 5),
                    Size = new Size(62, 20),
                });
                _keyLabels[i] = new Label
                {
                    Text = HotkeyFormat.Format(Bindings[i].Mod, Bindings[i].Vk),
                    Location = new Point(78, y + 4),
                    Size = new Size(124, 22),
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                _setButtons[i] = new Button { Text = "Set\u2026", Location = new Point(208, y), Size = new Size(64, 26) };
                _setButtons[i].Click += (s, e) => StartCapture(idx);
                _clearButtons[i] = new Button { Text = "Clear", Location = new Point(278, y), Size = new Size(64, 26) };
                _clearButtons[i].Click += (s, e) =>
                {
                    Bindings[idx] = new HotkeyBinding();
                    _keyLabels[idx].Text = HotkeyFormat.Format(0, 0);
                    _actionBoxes[idx].SelectedIndex = 0;
                };
                _actionBoxes[i] = new ComboBox
                {
                    Location = new Point(348, y + 2),
                    Size = new Size(126, 22),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                _actionBoxes[i].Items.AddRange(ActionNames);
                _actionBoxes[i].SelectedIndex = ActionToIndex(Bindings[i].Action);
                _actionBoxes[i].SelectedIndexChanged += (s, e) =>
                {
                    Bindings[idx].Action = IndexToAction(_actionBoxes[idx].SelectedIndex);
                };
                Controls.Add(_keyLabels[i]);
                Controls.Add(_setButtons[i]);
                Controls.Add(_clearButtons[i]);
                Controls.Add(_actionBoxes[i]);
            }

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(322, 300), Size = new Size(75, 26) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(403, 300), Size = new Size(75, 26) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            KeyUp += OnCaptureKeyUp;
            FormClosing += (s, e) => { if (_capturing >= 0) CancelCapture(); };
        }

        static int ActionToIndex(int action)
        {
            switch (action)
            {
                case HotkeyAction.PushToTalk: return 1;
                case HotkeyAction.ToggleMute: return 2;
                case HotkeyAction.ToggleDeafen: return 3;
                default: return 0;
            }
        }

        static int IndexToAction(int index)
        {
            switch (index)
            {
                case 1: return HotkeyAction.PushToTalk;
                case 2: return HotkeyAction.ToggleMute;
                case 3: return HotkeyAction.ToggleDeafen;
                default: return HotkeyAction.None;
            }
        }

        void StartCapture(int idx)
        {
            if (_capturing >= 0) return;
            _capturing = idx;
            _downKeys.Clear();
            _setButtons[idx].Text = "Press keys\u2026";
            _keyLabels[idx].Text = "\u2026";
            SetRowEnabled(false);
            _setButtons[idx].Enabled = true;
            _setButtons[idx].Focus();
        }

        void CancelCapture()
        {
            if (_capturing < 0) return;
            int idx = _capturing;
            _capturing = -1;
            _downKeys.Clear();
            _setButtons[idx].Text = "Set\u2026";
            _keyLabels[idx].Text = HotkeyFormat.Format(Bindings[idx].Mod, Bindings[idx].Vk);
            SetRowEnabled(true);
        }

        /// <summary>
        /// Earliest interception point: fires for every key before any
        /// focused control sees it, including Alt (WM_SYSKEYDOWN).
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capturing >= 0 && (msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN))
            {
                CaptureKeyData(keyData);
                return true; // swallow — nothing else sees keys while capturing
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void CaptureKeyData(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Escape) { CancelCapture(); return; }

            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= HotkeyFormat.MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= HotkeyFormat.MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= HotkeyFormat.MOD_SHIFT;
            // Win key intentionally not offered — Windows grabs it for the
            // Start menu, which makes it useless as a talk key.

            _downKeys.Add(key);

            if (HotkeyFormat.IsModifierKey(key))
            {
                // Still holding modifiers, waiting for the main key.
                _keyLabels[_capturing].Text =
                    HotkeyFormat.Format(mods, 0).Replace("(not set)", "").Trim()
                    + " + \u2026 (tap one key)";
                return;
            }

            int idx = _capturing;
            _capturing = -1;
            _downKeys.Clear();
            Bindings[idx].Mod = mods;
            Bindings[idx].Vk = (int)key;
            Bindings[idx].Label = HotkeyFormat.Format(mods, (int)key);
            _keyLabels[idx].Text = Bindings[idx].Label;
            _setButtons[idx].Text = "Set\u2026";
            SetRowEnabled(true);
        }

        void OnCaptureKeyUp(object sender, KeyEventArgs e)
        {
            if (_capturing < 0) return;
            _downKeys.Remove(e.KeyCode & Keys.KeyCode);
            if (_downKeys.Count == 0)
            {
                // Released everything without tapping a main key — remind
                // them what this dialog needs.
                _keyLabels[_capturing].Text = "\u2026 (hold modifiers + tap one key)";
            }
        }

        void SetRowEnabled(bool on)
        {
            for (int i = 0; i < MaxBindings; i++)
            {
                _setButtons[i].Enabled = on;
                _clearButtons[i].Enabled = on;
                _actionBoxes[i].Enabled = on;
            }
        }
    }
}
