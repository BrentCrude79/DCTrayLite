# DCTrayLite

Discord in the system tray — without the Discord desktop app. A single ~1 MB
executable that wraps the Discord web app in a lightweight window with a
tray icon and global hotkeys that control your mic and speakers at the OS level.

No installer. No Electron. Windows 10/11, WebView2-based (built into Windows).

## Get it

Download `DCTrayLite.exe` from this repo (or the latest release) and run it.
That's the whole install — put it wherever you like.

> **SmartScreen note:** the exe is unsigned, so Windows will show an "Unknown
> publisher" warning on first run. That's expected for a personal build —
> click *More info → Run anyway*. It makes no network connections except to
> Discord itself.

Log in with your Discord account on first launch. Your session is kept in a
private per-app profile and won't touch your Chrome/Edge profiles.

## How it works

The wrapper **owns your mic and speakers at the OS level** (Core Audio API).
Discord itself runs on **open mic / voice activity** — the wrapper decides
whether any sound reaches it. This is system-wide: muting here mutes your
mic for every app, by design.

**One-time Discord setup:** User Settings → Voice & Video → input mode
**Voice Activity** (open mic), and clear any Discord keybinds — the wrapper
handles all of it.

The mic starts **muted**. It goes live only when *you* say so:

- **Mute toggle** — flip the mic on/off per press.
- **Deafen toggle** — mic *and* speakers off per press (like Discord deafen).
- **Push-to-talk** — mic open only while the key is held.

On quit, your mic/speaker mute state is restored to what it was.

## Global hotkeys

1. Right-click the tray icon → **Hotkeys…** (dialog title shows the version).
2. Click **Set…**, then press the combo in one motion: **hold** the
   modifiers (Ctrl / Alt / Shift) and **tap one key** — e.g. hold Ctrl+Alt,
   tap M. Windows requires a non-modifier key; modifiers alone can't register.
   Esc cancels. (Up to 4 hotkeys.)
3. Pick the action per row: **Push-to-talk**, **Mute toggle**, or
   **Deafen toggle**.
4. If a combo is rejected, another app already uses it — pick another.

Hotkeys are saved per-app (`%LOCALAPPDATA%\PwaTray\DCTrayLite\hotkeys.json`).

## Tray behavior

- Closing or minimizing the window hides it to the tray instead of quitting.
- Double-click the tray icon (or right-click → *Open DCTrayLite*) to show it.
- *Mute mic / Unmute mic* and *Deafen / Undeafen* in the menu mirror the hotkeys.
- The tray icon shows wrapper state: red slash = muted, red slash on dark
  red = deafened, green = mic live, bright green = talking (PTT held).
- *Start with Windows* toggles a startup entry (HKCU Run).
- Right-click → *Quit* to exit for real.

## Source

Full C# source is in `src/` (WinForms, .NET Framework 4.8, WebView2).
