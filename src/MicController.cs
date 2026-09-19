using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DCTrayLite
{
    /// <summary>
    /// Mutes/unmutes the system microphone(s) through the Core Audio API.
    /// Hand-declared COM interop — no extra packages. This is the whole
    /// point of the wrapper: Discord stays on open mic / voice activity,
    /// and this class decides whether any sound reaches it. The tray icon
    /// mirrors this wrapper-side state, never the page.
    /// Muting here is system-wide (any app using the default mic goes
    /// quiet) — that is by design and accepted.
    /// </summary>
    sealed class MicController : IDisposable
    {
        const int EDataFlow_Capture = 1;      // eCapture
        const int ERole_Console = 0;          // eConsole
        const int ERole_Communications = 2;   // eCommunications
        const int CLSCTX_ALL = 23;

        static readonly Guid IID_IAudioEndpointVolume =
            new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        readonly List<IAudioEndpointVolume> _endpoints = new List<IAudioEndpointVolume>();
        readonly List<bool> _initialMute = new List<bool>();
        bool _disposed;

        /// <summary>Null when the controller initialized cleanly.</summary>
        public string Error { get; private set; }

        public MicController()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                try
                {
                    // Mute both the default capture device and the default
                    // communications capture device — usually the same mic,
                    // but muting both covers apps (like Discord) that bind
                    // to the comms role specifically.
                    foreach (int role in new[] { ERole_Console, ERole_Communications })
                    {
                        IMMDevice device = null;
                        try
                        {
                            int hr = enumerator.GetDefaultAudioEndpoint(
                                EDataFlow_Capture, role, out device);
                            if (hr != 0 || device == null) continue;
                            object ep;
                            Guid iid = IID_IAudioEndpointVolume;
                            hr = device.Activate(ref iid,
                                CLSCTX_ALL, IntPtr.Zero, out ep);
                            var vol = ep as IAudioEndpointVolume;
                            if (hr != 0 || vol == null)
                            {
                                if (ep != null) Marshal.ReleaseComObject(ep);
                                continue;
                            }
                            bool muted;
                            if (vol.GetMute(out muted) == 0)
                            {
                                _endpoints.Add(vol);
                                _initialMute.Add(muted);
                            }
                            else
                            {
                                Marshal.ReleaseComObject(ep);
                            }
                        }
                        finally
                        {
                            if (device != null) Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }
                if (_endpoints.Count == 0)
                    Error = "No microphone capture endpoint was found.";
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        public void SetMuted(bool muted)
        {
            if (_disposed) return;
            foreach (var ep in _endpoints)
            {
                try { ep.SetMute(muted, IntPtr.Zero); }
                catch { }
            }
        }

        /// <summary>Restores the mute state from before the wrapper took over.</summary>
        public void Restore()
        {
            if (_disposed) return;
            for (int i = 0; i < _endpoints.Count; i++)
            {
                try { _endpoints[i].SetMute(_initialMute[i], IntPtr.Zero); }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Restore(); }
            catch { }
            foreach (var ep in _endpoints)
            {
                try { Marshal.ReleaseComObject(ep); }
                catch { }
            }
            _endpoints.Clear();
        }

        // ---- Core Audio COM declarations (vtable order matters) ----

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [Guid("D666063F-1587-4E43-81F1-D9502C68A1E0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, IntPtr pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, IntPtr pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }
    }
}
