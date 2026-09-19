using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DCTrayLite
{
    /// <summary>
    /// Binds up to three global hotkeys. Each hotkey is modifiers
    /// (Ctrl/Alt/Shift) plus ONE main key — the shape Windows'
    /// RegisterHotKey API accepts, so bindings register reliably and
    /// conflicts surface instead of failing silently.
    /// Capture uses KeyPreview + KeyDown/KeyUp (the form sees keys before
    /// the focused control), never the message hook.
    /// </summary>
    sealed class HotkeyConfigDialog : Form
    {
        public List<HotkeyBinding> Bindings { get; }

        const int MaxBindings = 3;

        readonly Label[] _keyLabels = new Label[MaxBindings];
        readonly Button[] _setButtons = new Button[MaxBindings];
        readonly Button[] _clearButtons = new Button[MaxBindings];
        readonly ComboBox[] _actionBoxes = new ComboBox[MaxBindings];
        int _capturing = -1;
        uint _capMod;

        static readonly string[] ActionNames = { "\u2014", "Push-to-talk", "Toggle mute" };

        public HotkeyConfigDialog(List<HotkeyBinding> current)
        {
            Bindings = new List<HotkeyBinding>(current ?? new List<HotkeyBinding>());
            while (Bindings.Count < MaxBindings) Bindings.Add(new HotkeyBinding());
            for (int i = 0; i < MaxBindings; i++)
                if (Bindings[i] == null) Bindings[i] = new HotkeyBinding();

            Text = "Global Hotkeys";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true; // capture sees keys before the focused control
            ClientSize = new Size(486, 252);

            var info = new Label
            {
                Text = "Click Set\u2026, then press the combo: hold the modifiers\r\n" +
                       "(Ctrl / Alt / Shift) and tap one key. Esc cancels.\r\n" +
                       "The wrapper mutes your mic at the system level; these\r\n" +
                       "hotkeys unmute it \u2014 Push-to-talk holds it open while\r\n" +
                       "pressed, Toggle flips it on/off. Set Discord itself to\r\n" +
                       "Voice Activity (open mic) and clear its own keybinds.\r\n" +
                       "Tip: include a modifier so the hotkey can't fire while typing.",
                Location = new Point(12, 10),
                Size = new Size(462, 100),
            };
            Controls.Add(info);

            for (int i = 0; i < MaxBindings; i++)
            {
                int y = 116 + i * 34;
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
                _actionBoxes[i].SelectedIndex =
                    Bindings[i].Action == HotkeyAction.PushToTalk ? 1 :
                    Bindings[i].Action == HotkeyAction.Toggle ? 2 : 0;
                _actionBoxes[i].SelectedIndexChanged += (s, e) =>
                {
                    Bindings[idx].Action = _actionBoxes[idx].SelectedIndex == 1
                        ? HotkeyAction.PushToTalk
                        : _actionBoxes[idx].SelectedIndex == 2
                            ? HotkeyAction.Toggle
                            : HotkeyAction.None;
                };
                Controls.Add(_keyLabels[i]);
                Controls.Add(_setButtons[i]);
                Controls.Add(_clearButtons[i]);
                Controls.Add(_actionBoxes[i]);
            }

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(322, 222), Size = new Size(75, 26) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(403, 222), Size = new Size(75, 26) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            KeyDown += OnCaptureKeyDown;
            FormClosing += (s, e) => { if (_capturing >= 0) CancelCapture(); };
        }

        void StartCapture(int idx)
        {
            if (_capturing >= 0) return;
            _capturing = idx;
            _capMod = 0;
            _setButtons[idx].Text = "Press keys\u2026";
            _keyLabels[idx].Text = "\u2026";
            SetRowEnabled(false);
            _setButtons[idx].Enabled = true;
        }

        void CancelCapture()
        {
            if (_capturing < 0) return;
            int idx = _capturing;
            _capturing = -1;
            _setButtons[idx].Text = "Set\u2026";
            _keyLabels[idx].Text = HotkeyFormat.Format(Bindings[idx].Mod, Bindings[idx].Vk);
            SetRowEnabled(true);
        }

        void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            if (_capturing < 0) return;
            e.Handled = true;
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Escape) { CancelCapture(); return; }

            uint mods = 0;
            if (e.Control) mods |= HotkeyFormat.MOD_CONTROL;
            if (e.Alt) mods |= HotkeyFormat.MOD_ALT;
            if (e.Shift) mods |= HotkeyFormat.MOD_SHIFT;
            // Win key intentionally not offered — Windows grabs it for the
            // Start menu, which makes it useless as a talk key.

            if (HotkeyFormat.IsModifierKey(e.KeyCode))
            {
                // Still holding modifiers, waiting for the main key.
                _capMod = mods;
                _keyLabels[_capturing].Text =
                    HotkeyFormat.Format(mods, 0).Replace("(not set)", "").TrimEnd() + " + \u2026";
                return;
            }

            int idx = _capturing;
            _capturing = -1;
            Bindings[idx].Mod = mods;
            Bindings[idx].Vk = (int)e.KeyCode;
            Bindings[idx].Label = HotkeyFormat.Format(mods, (int)e.KeyCode);
            _keyLabels[idx].Text = Bindings[idx].Label;
            _setButtons[idx].Text = "Set\u2026";
            SetRowEnabled(true);
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
