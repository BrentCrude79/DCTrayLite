using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace DCTrayLite
{
    /// <summary>What a hotkey does to the wrapper-controlled audio.</summary>
    static class HotkeyAction
    {
        public const int None = 0;
        public const int PushToTalk = 1; // mic open while the combo is held
        public const int ToggleMute = 2; // flips the mic on/off per press
        public const int ToggleDeafen = 3; // flips deafen (mic + speakers) per press
    }

    /// <summary>
    /// One global hotkey: RegisterHotKey-style modifiers + a single main key.
    /// Windows itself watches these (no hook), so registration is reliable
    /// and conflicts are reported instead of failing silently.
    /// </summary>
    sealed class HotkeyBinding
    {
        public uint Mod;     // MOD_* flags, 0 = plain key
        public int Vk;       // virtual-key code of the main key, 0 = not set
        public string Label; // display text, e.g. "Ctrl + Alt + M"
        public int Action;   // HotkeyAction.*

        public bool IsSet => Vk != 0;
    }

    static class HotkeyFormat
    {
        // RegisterHotKey modifier flags.
        public const uint MOD_ALT = 0x1;
        public const uint MOD_CONTROL = 0x2;
        public const uint MOD_SHIFT = 0x4;
        public const uint MOD_WIN = 0x8;

        public static string Format(uint mod, int vk)
        {
            var parts = new List<string>();
            if ((mod & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mod & MOD_ALT) != 0) parts.Add("Alt");
            if ((mod & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mod & MOD_WIN) != 0) parts.Add("Win");
            if (vk != 0) parts.Add(PrettyKey((Keys)vk));
            return parts.Count == 0 ? "(not set)" : string.Join(" + ", parts);
        }

        public static bool IsModifierKey(Keys k)
        {
            switch (k)
            {
                case Keys.ShiftKey:
                case Keys.ControlKey:
                case Keys.Menu:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.LMenu:
                case Keys.RMenu:
                case Keys.LWin:
                case Keys.RWin:
                    return true;
                default:
                    return false;
            }
        }

        static string PrettyKey(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return k.ToString();
            if (k >= Keys.D0 && k <= Keys.D9)
                return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9)
                return "Num " + (char)('0' + (k - Keys.NumPad0));
            if (k >= Keys.F1 && k <= Keys.F24)
                return "F" + ((int)(k - Keys.F1) + 1);
            switch (k)
            {
                case Keys.Space: return "Space";
                case Keys.Tab: return "Tab";
                case Keys.Enter: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "Page Up";
                case Keys.PageDown: return "Page Down";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                default: return k.ToString();
            }
        }
    }
}
