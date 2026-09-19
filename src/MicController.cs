using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DCTrayLite
{
    /// <summary>
    /// Mutes/unmutes the system microphone(s) and speakers through the
    /// Core Audio API. Hand-declared COM interop — no extra packages.
    /// This is the whole point of the wrapper: Discord stays on open mic /
    /// voice activity, and this class decides whether any sound reaches it
    /// (mic) or reaches the user (speakers, for deafen). Muting here is
    /// system-wide — that is by design and accepted.
    /// </summary>
    sealed class MicController : IDisposable
    {
        const int EDataFlow_Render = 0;
        const int EDataFlow_Capture = 1;
        const int ERole_Console = 0;          // eConsole
        const int ERole_Communications = 2;   // eCommunications
        const int CLSCTX_ALL = 23;

        static readonly Guid IID_IAudioEndpointVolume =
            new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        sealed class Endpoint
        {
            public IAudioEndpointVolume Vol;
            public bool InitialMute;
        }

        readonly List<Endpoint> _mics = new List<Endpoint>();
        readonly List<Endpoint> _speakers = new List<Endpoint>();
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
                        AddEndpoint(enumerator, EDataFlow_Capture, role, _mics);
                    // Speakers (for deafen): default render endpoint. The
                    // console role covers normal playback; most systems
                    // route comms audio to the same device.
                    AddEndpoint(enumerator, EDataFlow_Render, ERole_Console, _speakers);
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }
                if (_mics.Count == 0)
                    Error = "No microphone capture endpoint was found.";
                // No speakers is not fatal — deafen just won't mute output.
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        void AddEndpoint(IMMDeviceEnumerator enumerator, int flow, int role, List<Endpoint> list)
        {
            IMMDevice device = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
                if (hr != 0 || device == null) return;
                object ep;
                Guid iid = IID_IAudioEndpointVolume;
                hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out ep);
                var vol = ep as IAudioEndpointVolume;
                if (hr != 0 || vol == null)
                {
                    if (ep != null) Marshal.ReleaseComObject(ep);
                    return;
                }
                bool muted;
                if (vol.GetMute(out muted) == 0)
                    list.Add(new Endpoint { Vol = vol, InitialMute = muted });
                else
                    Marshal.ReleaseComObject(ep);
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
            }
        }

        public void SetMicMuted(bool muted)
        {
            if (_disposed) return;
            foreach (var e in _mics)
            {
                try { e.Vol.SetMute(muted, IntPtr.Zero); }
                catch { }
            }
        }

        public void SetSpeakersMuted(bool muted)
        {
            if (_disposed) return;
            foreach (var e in _speakers)
            {
                try { e.Vol.SetMute(muted, IntPtr.Zero); }
                catch { }
            }
        }

        /// <summary>Restores mic + speaker mute state from before the wrapper took over.</summary>
        public void Restore()
        {
            if (_disposed) return;
            foreach (var e in _mics)
            {
                try { e.Vol.SetMute(e.InitialMute, IntPtr.Zero); }
                catch { }
            }
            foreach (var e in _speakers)
            {
                try { e.Vol.SetMute(e.InitialMute, IntPtr.Zero); }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Restore(); }
            catch { }
            foreach (var e in _mics)
            {
                try { Marshal.ReleaseComObject(e.Vol); }
                catch { }
            }
            foreach (var e in _speakers)
            {
                try { Marshal.ReleaseComObject(e.Vol); }
                catch { }
            }
            _mics.Clear();
            _speakers.Clear();
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
