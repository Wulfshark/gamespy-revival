// GameSpy Revival - voice chat engine: microphone capture, processing, codec, mixing and playback.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
//
// Audio is 16 kHz mono. Each 40 ms of speech becomes one 324-byte frame (IMA ADPCM, 4 bits per sample,
// with its own decoder state so a lost frame never breaks the next one).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Revival
{
    // ---------------------------------------------------------------- Windows audio (winmm)

    static class WinMM
    {
        public const int CALLBACK_EVENT = 0x00050000;
        public const int WHDR_DONE = 1;
        public const uint WAVE_MAPPER = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormat
        {
            public short wFormatTag, nChannels;
            public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WaveHdr
        {
            public IntPtr lpData;
            public int dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WaveInCaps
        {
            public short wMid, wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats;
            public short wChannels, wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WaveOutCaps
        {
            public short wMid, wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats;
            public short wChannels, wReserved1;
            public int dwSupport;
        }

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveInGetDevCapsW(UIntPtr dev, ref WaveInCaps caps, int size);
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveOutGetDevCapsW(UIntPtr dev, ref WaveOutCaps caps, int size);
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr h);

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

        public static WaveFormat Mono16k()
        {
            WaveFormat f = new WaveFormat();
            f.wFormatTag = 1; f.nChannels = 1; f.nSamplesPerSec = 16000; f.wBitsPerSample = 16;
            f.nBlockAlign = 2; f.nAvgBytesPerSec = 32000; f.cbSize = 0;
            return f;
        }

        public static List<string> InputNames()
        {
            List<string> l = new List<string>();
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                WaveInCaps c = new WaveInCaps();
                if (waveInGetDevCapsW(new UIntPtr((uint)i), ref c, Marshal.SizeOf(typeof(WaveInCaps))) == 0) l.Add(c.szPname);
                else l.Add("Device " + (i + 1));
            }
            return l;
        }

        public static List<string> OutputNames()
        {
            List<string> l = new List<string>();
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                WaveOutCaps c = new WaveOutCaps();
                if (waveOutGetDevCapsW(new UIntPtr((uint)i), ref c, Marshal.SizeOf(typeof(WaveOutCaps))) == 0) l.Add(c.szPname);
                else l.Add("Device " + (i + 1));
            }
            return l;
        }

        // device saved by name so it survives devices being added or removed; "" = Windows default
        public static uint FindDevice(List<string> names, string want)
        {
            if (string.IsNullOrEmpty(want)) return WAVE_MAPPER;
            for (int i = 0; i < names.Count; i++) if (names[i] == want) return (uint)i;
            return WAVE_MAPPER;
        }
    }

    // A block of unmanaged memory holding one WAVEHDR and its sample buffer.
    sealed class WaveBuffer : IDisposable
    {
        public readonly IntPtr Hdr, Data;
        public readonly int Bytes;
        static readonly int HdrSize = Marshal.SizeOf(typeof(WinMM.WaveHdr));
        static readonly int FlagsAt = (int)Marshal.OffsetOf(typeof(WinMM.WaveHdr), "dwFlags");
        static readonly int RecordedAt = (int)Marshal.OffsetOf(typeof(WinMM.WaveHdr), "dwBytesRecorded");

        public WaveBuffer(int bytes)
        {
            Bytes = bytes;
            Data = Marshal.AllocHGlobal(bytes);
            Hdr = Marshal.AllocHGlobal(HdrSize);
            WinMM.WaveHdr h = new WinMM.WaveHdr();
            h.lpData = Data;
            h.dwBufferLength = bytes;
            Marshal.StructureToPtr(h, Hdr, false);
        }

        public static int Size { get { return HdrSize; } }
        public bool Done { get { return (Marshal.ReadInt32(Hdr, FlagsAt) & WinMM.WHDR_DONE) != 0; } }
        public int Recorded { get { return Marshal.ReadInt32(Hdr, RecordedAt); } }
        public void ClearDone() { Marshal.WriteInt32(Hdr, FlagsAt, Marshal.ReadInt32(Hdr, FlagsAt) & ~WinMM.WHDR_DONE); }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Data);
            Marshal.FreeHGlobal(Hdr);
        }
    }

    // ---------------------------------------------------------------- codec

    static class Adpcm
    {
        public const int Samples = 640;              // 40 ms at 16 kHz
        public const int Bytes = 4 + Samples / 2;    // header + 4 bits per sample

        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        static readonly int[] StepTable = {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
            22385, 24623, 27086, 29794, 32767 };

        public class State { public int Pred, Index; }

        public static byte[] Encode(short[] pcm, State st)
        {
            byte[] o = new byte[Bytes];
            o[0] = (byte)(st.Pred & 0xFF); o[1] = (byte)((st.Pred >> 8) & 0xFF); o[2] = (byte)st.Index; o[3] = 0;
            int pred = st.Pred, idx = st.Index;
            for (int i = 0; i < Samples; i++)
            {
                int diff = pcm[i] - pred, code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int step = StepTable[idx], dq = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; dq += step; }
                step >>= 1;
                if (diff >= step) { code |= 2; diff -= step; dq += step; }
                step >>= 1;
                if (diff >= step) { code |= 1; dq += step; }
                pred += (code & 8) != 0 ? -dq : dq;
                if (pred > 32767) pred = 32767; else if (pred < -32768) pred = -32768;
                idx += IndexTable[code];
                if (idx < 0) idx = 0; else if (idx > 88) idx = 88;
                if ((i & 1) == 0) o[4 + i / 2] = (byte)code;
                else o[4 + i / 2] |= (byte)(code << 4);
            }
            st.Pred = pred; st.Index = idx;
            return o;
        }

        public static short[] Decode(byte[] f)
        {
            if (f == null || f.Length != Bytes) return null;
            int pred = (short)(f[0] | (f[1] << 8)), idx = f[2];
            if (idx > 88) return null;
            short[] pcm = new short[Samples];
            for (int i = 0; i < Samples; i++)
            {
                int code = (i & 1) == 0 ? f[4 + i / 2] & 15 : f[4 + i / 2] >> 4;
                int step = StepTable[idx], dq = step >> 3;
                if ((code & 4) != 0) dq += step;
                if ((code & 2) != 0) dq += step >> 1;
                if ((code & 1) != 0) dq += step >> 2;
                pred += (code & 8) != 0 ? -dq : dq;
                if (pred > 32767) pred = 32767; else if (pred < -32768) pred = -32768;
                idx += IndexTable[code];
                if (idx < 0) idx = 0; else if (idx > 88) idx = 88;
                pcm[i] = (short)pred;
            }
            return pcm;
        }
    }

    // ---------------------------------------------------------------- settings

    class VoiceConfig
    {
        public string InputDevice = "", OutputDevice = "";
        public int InputVolume = 100, OutputVolume = 100, SoundVolume = 60;
        public bool PushToTalk;
        public int PttKey = 0x05;                // Mouse 4
        public int PttReleaseMs = 200;
        public bool AutoSensitivity = true;
        public int ThresholdDb = -50;
        public bool NoiseReduction = true, HighPass = true, Agc = true;
        public int BufferMs = 80;
        public bool SndMute = true, SndUnmute = true, SndDeafen = true, SndUndeafen = true;
        public bool SndJoin = true, SndLeave = true, SndOthers = true, SndMessage = true;

        static bool B(string k, bool d) { return Config.Get("voice." + k, d ? "1" : "0") == "1"; }
        static int I(string k, int d) { int v; return int.TryParse(Config.Get("voice." + k, d.ToString()), out v) ? v : d; }
        static void SB(string k, bool v) { Config.Set("voice." + k, v ? "1" : "0"); }
        static void SI(string k, int v) { Config.Set("voice." + k, v.ToString()); }

        public static VoiceConfig Load()
        {
            VoiceConfig c = new VoiceConfig();
            c.InputDevice = Config.Get("voice.in", "");
            c.OutputDevice = Config.Get("voice.out", "");
            c.InputVolume = Clamp(I("invol", 100), 0, 200);
            c.OutputVolume = Clamp(I("outvol", 100), 0, 200);
            c.SoundVolume = Clamp(I("sndvol", 60), 0, 100);
            c.PushToTalk = B("ptt", false);
            c.PttKey = Clamp(I("pttkey", 5), 1, 254);
            c.PttReleaseMs = Clamp(I("pttdelay", 200), 0, 2000);
            c.AutoSensitivity = B("autosens", true);
            c.ThresholdDb = Clamp(I("threshold", -50), -100, 0);
            c.NoiseReduction = B("nr", true);
            c.HighPass = B("hpf", true);
            c.Agc = B("agc", true);
            c.BufferMs = Clamp(I("buffer", 80), 40, 400);
            c.SndMute = B("snd.mute", true); c.SndUnmute = B("snd.unmute", true);
            c.SndDeafen = B("snd.deafen", true); c.SndUndeafen = B("snd.undeafen", true);
            c.SndJoin = B("snd.join", true); c.SndLeave = B("snd.leave", true);
            c.SndOthers = B("snd.others", true); c.SndMessage = B("snd.message", true);
            return c;
        }

        public void Save()
        {
            Config.Set("voice.in", InputDevice);
            Config.Set("voice.out", OutputDevice);
            SI("invol", InputVolume); SI("outvol", OutputVolume); SI("sndvol", SoundVolume);
            SB("ptt", PushToTalk); SI("pttkey", PttKey); SI("pttdelay", PttReleaseMs);
            SB("autosens", AutoSensitivity); SI("threshold", ThresholdDb);
            SB("nr", NoiseReduction); SB("hpf", HighPass); SB("agc", Agc); SI("buffer", BufferMs);
            SB("snd.mute", SndMute); SB("snd.unmute", SndUnmute); SB("snd.deafen", SndDeafen); SB("snd.undeafen", SndUndeafen);
            SB("snd.join", SndJoin); SB("snd.leave", SndLeave); SB("snd.others", SndOthers); SB("snd.message", SndMessage);
        }

        public static void Reset()
        {
            new VoiceConfig().Save();
        }

        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 1: return "Left mouse";
                case 2: return "Right mouse";
                case 4: return "Middle mouse";
                case 5: return "Mouse 4";
                case 6: return "Mouse 5";
            }
            string n = ((System.Windows.Forms.Keys)vk).ToString();
            if (n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1])) return n.Substring(1);
            if (n == "Oemtilde") return "`";
            return n;
        }
    }

    // ---------------------------------------------------------------- engine

    enum Sfx { Mute, Unmute, Deafen, Undeafen, Join, Leave, OtherJoin, OtherLeave, Message }

    class VoiceEngine
    {
        const int Block = 320;    // 20 ms

        class Stream
        {
            public short[] Ring = new short[16000 * 2];   // 2 s
            public int Read, Count;
            public bool Started;
            public long LastHeard;
            public bool NoPrebuffer;
        }

        readonly object mix = new object();
        readonly Dictionary<string, Stream> streams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> userVol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> localMuted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Stopwatch clock = Stopwatch.StartNew();

        volatile VoiceConfig cfg = VoiceConfig.Load();
        Thread capThread, playThread;
        volatile bool capRun, playRun;

        // what wants the engine on
        bool inRoom, testing, metering;

        public volatile bool Muted, Deafened;
        public volatile float LevelDb = -100f;        // after processing, for meters
        public volatile float ThresholdNow = -50f;    // the threshold actually in use
        public volatile bool Transmitting;
        public volatile string Problem = "";

        public event Action<byte[]> FrameReady;       // raised on the capture thread

        public VoiceConfig Settings { get { return cfg; } }

        public void Reload()
        {
            VoiceConfig old = cfg;
            cfg = VoiceConfig.Load();
            if (old.InputDevice != cfg.InputDevice && capRun) { StopCapture(); UpdateRunning(); }
            if (old.OutputDevice != cfg.OutputDevice && playRun) { StopPlayback(); UpdateRunning(); }
        }

        public void SetInRoom(bool on) { inRoom = on; UpdateRunning(); }
        public void SetTesting(bool on) { testing = on; UpdateRunning(); }
        public bool Testing { get { return testing; } }
        public void SetMetering(bool on) { metering = on; UpdateRunning(); }

        void UpdateRunning()
        {
            bool cap = inRoom || testing || metering;
            bool play = inRoom || testing || sfxPending;
            if (cap && !capRun) StartCapture();
            if (!cap && capRun) StopCapture();
            if (play && !playRun) StartPlayback();
            if (!play && playRun && !inRoom && !testing) { /* playback stops itself when idle */ }
        }

        public void Shutdown()
        {
            inRoom = testing = metering = false;
            StopCapture();
            StopPlayback();
        }

        // ---- per-user controls
        public int GetUserVolume(string u) { int v; lock (mix) return userVol.TryGetValue(u, out v) ? v : 100; }
        public void SetUserVolume(string u, int v)
        {
            lock (mix) userVol[u] = v;
            Revival.Config.Set("voice.uvol." + u.ToLowerInvariant(), v.ToString());
        }
        public bool IsLocalMuted(string u) { lock (mix) return localMuted.Contains(u); }
        public void SetLocalMuted(string u, bool on) { lock (mix) { if (on) localMuted.Add(u); else localMuted.Remove(u); } }

        public bool IsSpeaking(string u)
        {
            lock (mix)
            {
                Stream s;
                return streams.TryGetValue(u, out s) && clock.ElapsedMilliseconds - s.LastHeard < 300;
            }
        }

        // ---- incoming audio from the network (any thread)
        public void Feed(string user, byte[] frame)
        {
            short[] pcm = Adpcm.Decode(frame);
            if (pcm == null || !playRun) return;
            lock (mix)
            {
                if (!userVol.ContainsKey(user))
                {
                    int v;
                    userVol[user] = int.TryParse(Revival.Config.Get("voice.uvol." + user.ToLowerInvariant(), "100"), out v) ? v : 100;
                }
                Push(Get(user), pcm, pcm.Length);
            }
        }

        Stream Get(string key)
        {
            Stream s;
            if (!streams.TryGetValue(key, out s)) { s = new Stream(); streams[key] = s; }
            return s;
        }

        void Push(Stream s, short[] pcm, int n)
        {
            int cap = s.Ring.Length;
            if (s.Count + n > cap) { int drop = s.Count + n - cap; s.Read = (s.Read + drop) % cap; s.Count -= drop; }
            int w = (s.Read + s.Count) % cap;
            for (int i = 0; i < n; i++) { s.Ring[w] = pcm[i]; w = w + 1 == cap ? 0 : w + 1; }
            s.Count += n;
            s.LastHeard = clock.ElapsedMilliseconds;
        }

        // ---- sounds
        volatile bool sfxPending;

        public void Play(Sfx kind)
        {
            VoiceConfig c = cfg;
            bool want;
            switch (kind)
            {
                case Sfx.Mute: want = c.SndMute; break;
                case Sfx.Unmute: want = c.SndUnmute; break;
                case Sfx.Deafen: want = c.SndDeafen; break;
                case Sfx.Undeafen: want = c.SndUndeafen; break;
                case Sfx.Join: want = c.SndJoin; break;
                case Sfx.Leave: want = c.SndLeave; break;
                case Sfx.Message: want = c.SndMessage; break;
                default: want = c.SndOthers; break;
            }
            if (!want || c.SoundVolume == 0) return;
            short[] pcm = Tone(kind, c.SoundVolume / 100f);
            lock (mix)
            {
                Stream s = Get("\u0001sfx");
                s.NoPrebuffer = true;
                Push(s, pcm, pcm.Length);
            }
            if (!playRun) { sfxPending = true; StartPlayback(); }
        }

        static short[] Tone(Sfx kind, float vol)
        {
            float[] f; int ms;
            switch (kind)
            {
                case Sfx.Mute: f = new float[] { 600, 400 }; ms = 90; break;
                case Sfx.Unmute: f = new float[] { 400, 600 }; ms = 90; break;
                case Sfx.Deafen: f = new float[] { 500, 350, 250 }; ms = 80; break;
                case Sfx.Undeafen: f = new float[] { 250, 350, 500 }; ms = 80; break;
                case Sfx.Join: f = new float[] { 523, 659, 784 }; ms = 90; break;
                case Sfx.Leave: f = new float[] { 784, 659, 523 }; ms = 90; break;
                case Sfx.OtherJoin: f = new float[] { 659, 880 }; ms = 70; break;
                case Sfx.OtherLeave: f = new float[] { 880, 659 }; ms = 70; break;
                default: f = new float[] { 1175, 1568 }; ms = 60; break;
            }
            int per = 16 * ms;
            short[] o = new short[per * f.Length];
            double ph = 0;
            for (int t = 0; t < f.Length; t++)
                for (int i = 0; i < per; i++)
                {
                    float env = Math.Min(1f, Math.Min(i / 80f, (per - i) / 160f));
                    ph += 2 * Math.PI * f[t] / 16000.0;
                    o[t * per + i] = (short)(Math.Sin(ph) * 9000 * vol * env);
                }
            return o;
        }

        // ---------------------------------------------------------------- capture

        void StartCapture()
        {
            capRun = true;
            capThread = new Thread(CaptureLoop);
            capThread.IsBackground = true;
            capThread.Priority = ThreadPriority.AboveNormal;
            capThread.Start();
        }

        void StopCapture()
        {
            capRun = false;
            Thread t = capThread;
            if (t != null && t != Thread.CurrentThread) t.Join(1500);
            capThread = null;
            LevelDb = -100f;
            Transmitting = false;
        }

        void CaptureLoop()
        {
            IntPtr h = IntPtr.Zero;
            List<WaveBuffer> bufs = new List<WaveBuffer>();
            AutoResetEvent ev = new AutoResetEvent(false);
            try
            {
                List<string> names = WinMM.InputNames();
                WinMM.WaveFormat fmt = WinMM.Mono16k();
                uint dev = WinMM.FindDevice(names, cfg.InputDevice);
                int r = WinMM.waveInOpen(out h, dev, ref fmt, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMM.CALLBACK_EVENT);
                if (r != 0) { Problem = "Could not open the microphone (error " + r + "). Pick another one in Voice & Audio settings."; capRun = false; return; }
                Problem = "";
                for (int i = 0; i < 8; i++)
                {
                    WaveBuffer b = new WaveBuffer(Block * 2);
                    bufs.Add(b);
                    WinMM.waveInPrepareHeader(h, b.Hdr, WaveBuffer.Size);
                    WinMM.waveInAddBuffer(h, b.Hdr, WaveBuffer.Size);
                }
                WinMM.waveInStart(h);
                Processor p = new Processor();
                short[] block = new short[Block];
                int next = 0;
                while (capRun)
                {
                    ev.WaitOne(100);
                    for (int k = 0; k < bufs.Count && capRun; k++)
                    {
                        WaveBuffer b = bufs[next];
                        if (!b.Done) break;
                        int n = Math.Min(Block, b.Recorded / 2);
                        Marshal.Copy(b.Data, block, 0, n);
                        for (int i = n; i < Block; i++) block[i] = 0;
                        b.ClearDone();
                        WinMM.waveInAddBuffer(h, b.Hdr, WaveBuffer.Size);
                        next = (next + 1) % bufs.Count;
                        ProcessBlock(p, block);
                    }
                }
            }
            catch (Exception ex) { Problem = "Microphone stopped: " + ex.Message; }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    WinMM.waveInReset(h);
                    foreach (WaveBuffer b in bufs) WinMM.waveInUnprepareHeader(h, b.Hdr, WaveBuffer.Size);
                    WinMM.waveInClose(h);
                }
                foreach (WaveBuffer b in bufs) b.Dispose();
                ev.Close();
                LevelDb = -100f;
                Transmitting = false;
            }
        }

        // state for the capture chain
        class Processor
        {
            public float HpX, HpY, Agc = 1f, Floor = -60f, NrGain = 1f;
            public long OpenUntil, PttUntil;
            public bool WasOpen;
            public short[] Frame = new short[Adpcm.Samples];
            public int FrameFill;
            public Adpcm.State Enc = new Adpcm.State();
        }

        void ProcessBlock(Processor p, short[] x)
        {
            VoiceConfig c = cfg;
            long now = clock.ElapsedMilliseconds;
            float gain = c.InputVolume / 100f;
            float[] y = new float[Block];
            double sum = 0;
            for (int i = 0; i < Block; i++)
            {
                float v = x[i] * gain;
                if (c.HighPass)
                {
                    // one-pole high-pass at about 100 Hz: removes rumble, hum and desk thumps
                    float o = 0.9622f * (p.HpY + v - p.HpX);
                    p.HpX = v; p.HpY = o; v = o;
                }
                y[i] = v;
                sum += v * v;
            }
            double rms = Math.Sqrt(sum / Block);
            float preDb = rms < 1 ? -100f : (float)(20 * Math.Log10(rms / 32768.0));

            // automatic gain control: bring speech towards -18 dBFS, slowly, only when there is speech
            if (c.Agc && preDb > p.Floor + 6 && preDb > -55)
            {
                float target = (float)Math.Pow(10, (-18 - preDb) / 20.0);
                if (target > 6f) target = 6f; if (target < 0.3f) target = 0.3f;
                p.Agc += (target - p.Agc) * (target < p.Agc ? 0.15f : 0.02f);
            }
            else if (!c.Agc) p.Agc = 1f;
            float g2 = c.Agc ? p.Agc : 1f;

            float post = preDb <= -100 ? -100f : preDb + (float)(20 * Math.Log10(g2));
            LevelDb = post;

            // background noise estimate: falls quickly, rises slowly
            if (preDb < p.Floor) p.Floor += (preDb - p.Floor) * 0.3f;
            else p.Floor += 0.04f;
            if (p.Floor < -90) p.Floor = -90;

            float thr = c.AutoSensitivity ? Math.Max(-62f, Math.Min(-25f, p.Floor + (float)(20 * Math.Log10(g2)) + 9f)) : c.ThresholdDb;
            ThresholdNow = thr;

            bool open;
            if (c.PushToTalk)
            {
                bool held = (WinMM.GetAsyncKeyState(c.PttKey) & 0x8000) != 0;
                if (held) p.PttUntil = now + c.PttReleaseMs;
                open = held || now < p.PttUntil;
            }
            else
            {
                if (post > thr) p.OpenUntil = now + 400;
                open = now < p.OpenUntil;
            }

            // basic noise reduction: turn down the quiet parts between words while the mic is open
            float nrTarget = 1f;
            if (c.NoiseReduction && post < thr + 3) nrTarget = 0.3f;
            short[] outp = new short[Block];
            for (int i = 0; i < Block; i++)
            {
                p.NrGain += (nrTarget - p.NrGain) * 0.004f;
                float v = y[i] * g2 * p.NrGain;
                if (!p.WasOpen && open) v *= Math.Min(1f, i / 64f);         // fade in
                if (p.WasOpen && !open) v *= Math.Max(0f, 1f - i / 64f);    // fade out
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                outp[i] = (short)v;
            }

            bool sending = open || p.WasOpen;   // the closing block carries the fade out
            p.WasOpen = open;
            bool allowed = !Muted && !Deafened;
            Transmitting = open && allowed;

            if (testing && sending)
                lock (mix) Push(Get("\u0002self"), outp, Block);

            if (!sending || !allowed || !inRoom)
            {
                p.FrameFill = 0;
                return;
            }
            Array.Copy(outp, 0, p.Frame, p.FrameFill, Block);
            p.FrameFill += Block;
            if (p.FrameFill >= Adpcm.Samples || !open)
            {
                for (int i = p.FrameFill; i < Adpcm.Samples; i++) p.Frame[i] = 0;
                p.FrameFill = 0;
                byte[] f = Adpcm.Encode(p.Frame, p.Enc);
                Action<byte[]> h = FrameReady;
                if (h != null) h(f);
            }
        }

        // ---------------------------------------------------------------- playback

        void StartPlayback()
        {
            if (playRun) return;
            playRun = true;
            playThread = new Thread(PlayLoop);
            playThread.IsBackground = true;
            playThread.Priority = ThreadPriority.AboveNormal;
            playThread.Start();
        }

        void StopPlayback()
        {
            playRun = false;
            Thread t = playThread;
            if (t != null && t != Thread.CurrentThread) t.Join(1500);
            playThread = null;
            lock (mix) streams.Clear();
        }

        void PlayLoop()
        {
            IntPtr h = IntPtr.Zero;
            List<WaveBuffer> bufs = new List<WaveBuffer>();
            AutoResetEvent ev = new AutoResetEvent(false);
            try
            {
                List<string> names = WinMM.OutputNames();
                WinMM.WaveFormat fmt = WinMM.Mono16k();
                uint dev = WinMM.FindDevice(names, cfg.OutputDevice);
                int r = WinMM.waveOutOpen(out h, dev, ref fmt, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMM.CALLBACK_EVENT);
                if (r != 0) { Problem = "Could not open the speaker (error " + r + "). Pick another one in Voice & Audio settings."; playRun = false; return; }
                short[] block = new short[Block];
                for (int i = 0; i < 4; i++)
                {
                    WaveBuffer b = new WaveBuffer(Block * 2);
                    bufs.Add(b);
                    WinMM.waveOutPrepareHeader(h, b.Hdr, WaveBuffer.Size);
                    Mix(block);
                    Marshal.Copy(block, 0, b.Data, Block);
                    WinMM.waveOutWrite(h, b.Hdr, WaveBuffer.Size);
                }
                int next = 0;
                long idleSince = -1;
                while (playRun)
                {
                    ev.WaitOne(100);
                    for (int k = 0; k < bufs.Count; k++)
                    {
                        WaveBuffer b = bufs[next];
                        if (!b.Done) break;
                        bool any = Mix(block);
                        Marshal.Copy(block, 0, b.Data, Block);
                        b.ClearDone();
                        WinMM.waveOutWrite(h, b.Hdr, WaveBuffer.Size);
                        next = (next + 1) % bufs.Count;
                        if (any) idleSince = -1;
                        else if (idleSince < 0) idleSince = clock.ElapsedMilliseconds;
                    }
                    // nothing wants audio and nothing is playing: close the device
                    if (!inRoom && !testing && idleSince >= 0 && clock.ElapsedMilliseconds - idleSince > 1500) break;
                }
            }
            catch (Exception ex) { Problem = "Speaker stopped: " + ex.Message; }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    WinMM.waveOutReset(h);
                    foreach (WaveBuffer b in bufs) WinMM.waveOutUnprepareHeader(h, b.Hdr, WaveBuffer.Size);
                    WinMM.waveOutClose(h);
                }
                foreach (WaveBuffer b in bufs) b.Dispose();
                ev.Close();
                playRun = false;
                sfxPending = false;
            }
        }

        // mixes 20 ms of everyone into block; returns whether anything was audible
        bool Mix(short[] block)
        {
            VoiceConfig c = cfg;
            int[] acc = new int[Block];
            bool any = false;
            int pre = c.BufferMs * 16;
            int maxLag = pre + 250 * 16;
            float outVol = c.OutputVolume / 100f;
            lock (mix)
            {
                foreach (KeyValuePair<string, Stream> kv in streams)
                {
                    Stream s = kv.Value;
                    bool sfx = kv.Key == "\u0001sfx";
                    bool self = kv.Key == "\u0002self";
                    if (!s.Started)
                    {
                        if (s.Count == 0) continue;
                        if (!s.NoPrebuffer && !self && s.Count < pre) continue;
                        s.Started = true;
                    }
                    if (!sfx && !self && s.Count > maxLag)   // fell behind: skip ahead
                    {
                        int drop = s.Count - pre;
                        s.Read = (s.Read + drop) % s.Ring.Length; s.Count -= drop;
                    }
                    float g;
                    if (sfx) g = 1f;
                    else if (self) g = outVol;
                    else
                    {
                        if (Deafened || localMuted.Contains(kv.Key)) g = 0f;
                        else { int uv; g = outVol * (userVol.TryGetValue(kv.Key, out uv) ? uv : 100) / 100f; }
                    }
                    int n = Math.Min(Block, s.Count);
                    int cap = s.Ring.Length;
                    for (int i = 0; i < n; i++)
                    {
                        acc[i] += (int)(s.Ring[s.Read] * g);
                        s.Read = s.Read + 1 == cap ? 0 : s.Read + 1;
                    }
                    s.Count -= n;
                    if (s.Count == 0) s.Started = false;
                    if (n > 0 && g > 0) any = true;
                }
            }
            for (int i = 0; i < Block; i++)
            {
                int v = acc[i];
                if (v > 24000) v = 24000 + (v - 24000) / 4;          // soft knee before clipping
                else if (v < -24000) v = -24000 + (v + 24000) / 4;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                block[i] = (short)v;
            }
            return any;
        }
    }
}
