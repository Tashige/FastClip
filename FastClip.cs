// FastClip - lightweight replay-buffer clipper.
// GPU capture (DXGI Desktop Duplication) + hardware encode + stream-copy save, driven by ffmpeg.
// Build: build.bat (uses the C# compiler that ships with Windows, no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool selfTest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        bool startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool created;
        using (var mutex = new Mutex(true, "FastClip.SingleInstance", out created))
        {
            if (!created && !selfTest) return 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(selfTest, startHidden);
            Application.Run(form);
            return form.ExitCode;
        }
    }
}

// ---------------------------------------------------------------- settings
class Settings
{
    public int ClipSeconds = 120;
    public int Fps = 60;
    public int Bitrate = 10;
    public string Hotkey = "F24";
    public int Monitor = 0;
    public string Encoder = "auto";
    public string Audio = "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)";
    public string Mic = "";
    public string OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clips");

    public static string FilePath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.ini"); }
    }

    public static Settings Load()
    {
        var s = new Settings();
        if (!File.Exists(FilePath)) { s.Save(); return s; }
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int i = line.IndexOf('=');
            if (i < 0) continue;
            string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
            int n;
            switch (k)
            {
                case "clipseconds": if (int.TryParse(v, out n) && n >= 5) s.ClipSeconds = n; break;
                case "fps": if (int.TryParse(v, out n) && n > 0) s.Fps = n; break;
                case "bitrate": if (int.TryParse(v, out n) && n > 0) s.Bitrate = n; break;
                case "monitor": if (int.TryParse(v, out n) && n >= 0) s.Monitor = n; break;
                case "hotkey": if (v.Length > 0) s.Hotkey = v; break;
                case "encoder": if (v.Length > 0) s.Encoder = v; break;
                case "audio": s.Audio = v; break;
                case "mic": s.Mic = v; break;
                case "outdir": if (v.Length > 0) s.OutDir = v; break;
            }
        }
        return s;
    }

    public void Save()
    {
        File.WriteAllText(FilePath, string.Join("\r\n", new[] {
            "# FastClip settings (also editable from the app window)",
            "ClipSeconds=" + ClipSeconds, "Fps=" + Fps, "Bitrate=" + Bitrate, "Hotkey=" + Hotkey,
            "Monitor=" + Monitor, "Encoder=" + Encoder, "Audio=" + Audio, "Mic=" + Mic, "OutDir=" + OutDir, ""
        }));
    }
}

// ---------------------------------------------------------------- global hotkey
class HotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    public event Action Pressed;

    public HotkeyWindow(string spec)
    {
        uint mod = 0x4000, vk = 0; // MOD_NOREPEAT
        foreach (var part in spec.Split('+', ' ', ','))
        {
            var p = part.Trim().ToLowerInvariant();
            if (p == "") continue;
            if (p == "alt") mod |= 1; else if (p == "ctrl") mod |= 2; else if (p == "shift") mod |= 4; else if (p == "win") mod |= 8;
            else if (Regex.IsMatch(p, "^f([1-9]|1[0-9]|2[0-4])$")) vk = (uint)(0x6F + int.Parse(p.Substring(1)));
            else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = (uint)char.ToUpperInvariant(p[0]);
        }
        if (vk == 0) throw new Exception("Unrecognized hotkey: " + spec);
        CreateHandle(new CreateParams());
        if (!RegisterHotKey(Handle, 1, mod, vk)) { DestroyHandle(); throw new Exception("Hotkey " + spec + " is already used by another program."); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && Pressed != null) Pressed();
        base.WndProc(ref m);
    }

    public void Dispose() { UnregisterHotKey(Handle, 1); DestroyHandle(); }
}

// ---------------------------------------------------------------- WASAPI loopback (record an output device)
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropKey key);
    [PreserveSig] int GetValue(ref PropKey key, out PropVariant value);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long def, out long min);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePos, out ulong qpcPos);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

class Loopback
{
    public const string Prefix = "Output: ";
    public int Rate, Channels, BlockAlign;
    public string Format;          // ffmpeg raw format, e.g. f32le
    Stream sink;
    volatile bool stop;
    readonly Thread thread;
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    string error;

    static IMMDevice[] Outputs(out string[] names)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        IMMDeviceCollection col; en.EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out col);
        int n; col.GetCount(out n);
        var devs = new IMMDevice[n]; names = new string[n];
        var key = new PropKey { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 }; // FriendlyName
        for (int i = 0; i < n; i++)
        {
            col.Item(i, out devs[i]);
            IPropertyStore ps; devs[i].OpenPropertyStore(0, out ps);
            PropVariant v; ps.GetValue(ref key, out v);
            names[i] = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "Output " + i;
        }
        return devs;
    }

    // List output devices; runs on its own MTA thread so it is safe to call from the UI.
    public static List<string> List()
    {
        var result = new List<string>();
        var t = new Thread(() => { try { string[] names; Outputs(out names); result.AddRange(names); } catch { } });
        t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join(5000);
        return result;
    }

    public Loopback(string deviceName)
    {
        thread = new Thread(() => Run(deviceName)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "loopback" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!ready.WaitOne(5000)) throw new Exception("Audio device did not respond.");
        if (error != null) throw new Exception(error);
    }

    // Start pushing audio into ffmpeg. Before this, captured audio is discarded.
    public void Attach(Stream s) { sink = s; }

    public void Stop() { stop = true; thread.Join(2000); }

    void Run(string name)
    {
        IAudioClient client = null;
        try
        {
            string[] names; var devs = Outputs(out names);
            int idx = Array.FindIndex(names, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception("Output device not found: " + name);

            var iidClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            object o; Marshal.ThrowExceptionForHR(devs[idx].Activate(ref iidClient, 23, IntPtr.Zero, out o));
            client = (IAudioClient)o;
            IntPtr fmt; Marshal.ThrowExceptionForHR(client.GetMixFormat(out fmt));
            int tag = Marshal.ReadInt16(fmt, 0);
            Channels = Marshal.ReadInt16(fmt, 2);
            Rate = Marshal.ReadInt32(fmt, 4);
            BlockAlign = Marshal.ReadInt16(fmt, 12);
            int bits = Marshal.ReadInt16(fmt, 14);
            bool isFloat = tag == 3 || (tag == unchecked((short)0xFFFE) && Marshal.ReadInt32(fmt, 24) == 3);
            Format = isFloat ? "f32le" : "s" + bits + "le";
            // shared mode, loopback, 200 ms buffer
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero));
            Marshal.FreeCoTaskMem(fmt);
            var iidCap = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            object c; Marshal.ThrowExceptionForHR(client.GetService(ref iidCap, out c));
            var cap = (IAudioCaptureClient)c;
            Marshal.ThrowExceptionForHR(client.Start());
            ready.Set();

            var buf = new byte[Rate * BlockAlign];   // 1 s scratch
            var clock = new Stopwatch();
            long written = 0;
            Stream attached = null;
            while (!stop)
            {
                Thread.Sleep(5);
                var s = sink;
                if (s != null && attached == null) { attached = s; clock.Start(); written = 0; }
                uint n; cap.GetNextPacketSize(out n);
                while (n > 0 && !stop)
                {
                    IntPtr data; uint frames, flags; ulong dp, qp;
                    if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                    int bytes = (int)frames * BlockAlign;
                    if (bytes > buf.Length) buf = new byte[bytes];
                    if ((flags & 2) != 0) Array.Clear(buf, 0, bytes); else Marshal.Copy(data, buf, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    if (attached != null) { attached.Write(buf, 0, bytes); written += frames; }
                    cap.GetNextPacketSize(out n);
                }
                if (attached != null)
                {
                    // Windows sends nothing while the device is silent; fill the gap with silence
                    // so the audio track keeps real time.
                    long expected = clock.ElapsedTicks * Rate / Stopwatch.Frequency;
                    long behind = expected - written - Rate / 100;
                    if (behind > Rate / 20)
                    {
                        while (behind > 0)
                        {
                            int f = (int)Math.Min(behind, buf.Length / BlockAlign);
                            Array.Clear(buf, 0, f * BlockAlign);
                            attached.Write(buf, 0, f * BlockAlign);
                            written += f; behind -= f;
                        }
                    }
                    attached.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            if (!ready.WaitOne(0)) { error = ex.Message; ready.Set(); }
        }
        finally
        {
            try { if (client != null) client.Stop(); } catch { }
        }
    }
}

// ---------------------------------------------------------------- recorder (no UI)
class Recorder
{
    public const int SegSec = 2;
    public readonly string BufDir = Path.Combine(Path.GetTempPath(), "FastClipBuffer");
    public string Ffmpeg, Encoder;
    public Settings Cfg;
    Process rec;
    Loopback loop;
    StreamWriter log;
    readonly object saveLock = new object();

    public bool Running { get { return rec != null && !rec.HasExited; } }

    // 4 = HIGH, 5 = REALTIME (REALTIME needs admin; falls back to HIGH)
    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    public static string FindFfmpeg()
    {
        string here = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ffmpeg.exe");
        if (File.Exists(here)) return here;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
        if (Directory.Exists(winget))
            try { return Directory.GetFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault(); } catch { }
        return null;
    }

    public int Run(string args, int timeoutMs, out string err)
    {
        var psi = new ProcessStartInfo(Ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi))
        {
            var e = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } err = "timeout"; return -1; }
            err = e.Result; return p.ExitCode;
        }
    }

    public string PickEncoder()
    {
        if (Cfg.Encoder != "auto") return Cfg.Encoder;
        foreach (var e in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            string err;
            if (Run("-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:r=30 -frames:v 2 -c:v " + e + " -f null -", 15000, out err) == 0) return e;
        }
        return "libx264";
    }

    public List<string> ListAudioDevices()
    {
        string err;
        Run("-hide_banner -list_devices true -f dshow -i dummy", 15000, out err);
        var inputs = Regex.Matches(err, "\"([^\"]+)\" \\(audio\\)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct();
        // output devices first (recorded via loopback), then recording devices
        return Loopback.List().Select(n => Loopback.Prefix + n).Concat(inputs).ToList();
    }

    static string Q(string s) { return s.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? "\"" + s + "\"" : s; }

    public void Start()
    {
        Stop();
        if (Directory.Exists(BufDir)) foreach (var f in Directory.GetFiles(BufDir)) { try { File.Delete(f); } catch { } }
        Directory.CreateDirectory(BufDir);
        Directory.CreateDirectory(Cfg.OutDir);

        int gop = Cfg.Fps * SegSec; string br = Cfg.Bitrate + "M";
        // ddagrab hands D3D11 frames straight to the hardware encoder (no CPU copy).
        string src = "ddagrab=output_idx=" + Cfg.Monitor + ":framerate=" + Cfg.Fps + ":draw_mouse=0";
        if (Encoder == "libx264") src += ",hwdownload,format=bgra,format=yuv420p";
        string venc;
        switch (Encoder)
        {
            case "h264_nvenc": venc = "-c:v h264_nvenc -preset p4 -tune ll -rc cbr -b:v " + br + " -maxrate " + br + " -bufsize " + (Cfg.Bitrate * 2) + "M -g " + gop + " -bf 0 -spatial-aq 0"; break;
            case "h264_amf": venc = "-c:v h264_amf -usage lowlatency -quality speed -rc cbr -b:v " + br + " -g " + gop + " -bf 0"; break;
            case "h264_qsv": venc = "-c:v h264_qsv -preset veryfast -b:v " + br + " -g " + gop + " -bf 0"; break;
            default: venc = "-c:v libx264 -preset ultrafast -tune zerolatency -b:v " + br + " -g " + gop + " -bf 0"; break;
        }

        // same device twice = doubled/echoed audio, so ignore a duplicate
        var devs = new[] { Cfg.Audio, Cfg.Mic }.Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Every input is stamped with the same wall clock and -copyts keeps those stamps,
        // so audio and video stay aligned no matter which device started first.
        var used = new List<string>();
        var sb = new StringBuilder("-hide_banner -loglevel error -nostdin -y -thread_queue_size 64 -use_wallclock_as_timestamps 1 -f lavfi -i " + Q(src));
        foreach (var d in devs)
        {
            if (d.StartsWith(Loopback.Prefix) && loop == null)
            {
                // output device (speakers/interface): captured with WASAPI loopback and piped in
                loop = new Loopback(d.Substring(Loopback.Prefix.Length));
                sb.Append(" -thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f " + loop.Format + " -ar " + loop.Rate + " -ac " + loop.Channels + " -i pipe:0");
            }
            else if (!d.StartsWith(Loopback.Prefix))
                sb.Append(" -thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f dshow -audio_buffer_size 20 -i " + Q("audio=" + d));
            else continue;   // only one output device can be looped back
            used.Add(d);
        }
        devs = used;
        sb.Append(" -copyts");
        if (devs.Count == 1) sb.Append(" -map 0:v -map 1:a -af aresample=async=1000");
        else if (devs.Count == 2) sb.Append(" -filter_complex [1:a][2:a]amix=inputs=2:duration=first:normalize=0,aresample=async=1000[a] -map 0:v -map [a]");
        else sb.Append(" -map 0:v");
        sb.Append(" " + venc);
        if (devs.Count > 0) sb.Append(" -c:a aac -b:a 160k -ar 48000");
        int segCount = (int)Math.Ceiling(Cfg.ClipSeconds / (double)SegSec) + 2;
        sb.Append(" -f segment -segment_time " + SegSec + " -segment_wrap " + segCount + " -reset_timestamps 1 -segment_format mpegts " + Q(Path.Combine(BufDir, "seg%03d.ts")));

        var psi = new ProcessStartInfo(Ffmpeg, sb.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = loop != null };
        rec = Process.Start(psi);
        if (loop != null) loop.Attach(rec.StandardInput.BaseStream);
        // A game at 100% GPU starves screen capture, which looks like stutter. Like OBS, raise the
        // recorder's GPU scheduling priority (and CPU priority) so it still gets its frames.
        try { rec.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        try { if (D3DKMTSetProcessSchedulingPriorityClass(rec.Handle, 5) != 0) D3DKMTSetProcessSchedulingPriorityClass(rec.Handle, 4); } catch { }
        log = new StreamWriter(Path.Combine(BufDir, "ffmpeg.log")) { AutoFlush = true };
        var w = log;
        rec.ErrorDataReceived += (s, e) => { if (e.Data != null) try { w.WriteLine(e.Data); } catch { } };
        rec.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (loop != null) { loop.Stop(); loop = null; }
        try { if (rec != null && !rec.HasExited) { rec.Kill(); rec.WaitForExit(3000); } } catch { }
        rec = null;
        try { if (log != null) log.Dispose(); } catch { }
        log = null;
    }

    public string LastLog()
    {
        try
        {
            using (var fs = new FileStream(Path.Combine(BufDir, "ffmpeg.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs)) return r.ReadToEnd().Trim();
        }
        catch { return ""; }
    }

    // Returns saved file path, or throws.
    public string Save()
    {
        if (!Monitor.TryEnter(saveLock)) throw new Exception("A clip is already being saved.");
        try
        {
            var segs = new DirectoryInfo(BufDir).GetFiles("seg*.ts").Where(f => f.Length > 0).OrderBy(f => f.LastWriteTime).ToList();
            if (segs.Count == 0) throw new Exception("Nothing buffered yet.");
            int want = (int)Math.Ceiling(Cfg.ClipSeconds / (double)SegSec) + 1;
            segs = segs.Skip(Math.Max(0, segs.Count - want)).ToList();
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string list = Path.Combine(BufDir, "list_" + stamp + ".txt");
            File.WriteAllLines(list, segs.Select(f => "file '" + f.FullName.Replace('\\', '/') + "'"), Encoding.ASCII);
            Directory.CreateDirectory(Cfg.OutDir);
            string outFile = Path.Combine(Cfg.OutDir, "Clip_" + stamp + ".mp4");
            string err;
            int code = Run("-hide_banner -loglevel error -y -f concat -safe 0 -i " + Q(list) + " -c copy -movflags +faststart " + Q(outFile), 120000, out err);
            try { File.Delete(list); } catch { }
            if (code != 0) throw new Exception(err.Length > 300 ? err.Substring(0, 300) : err);
            return outFile;
        }
        finally { Monitor.Exit(saveLock); }
    }
}

// ---------------------------------------------------------------- main window
class MainForm : Form
{
    public int ExitCode;
    readonly bool selfTest, startHidden;
    Settings cfg = Settings.Load();
    readonly Recorder recorder = new Recorder();
    readonly NotifyIcon tray = new NotifyIcon();
    HotkeyWindow hotkey;
    bool wantRecording, quitting;
    DateTime lastStart = DateTime.MinValue;
    int quickDeaths;
    readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer { Interval = 3000 };

    // controls
    Label statusDot, statusText, lastClip;
    Button btnSave, btnToggle, btnFolder, btnApply, btnBrowse, btnRefresh;
    NumericUpDown numClip, numFps, numBitrate, numMonitor;
    TextBox txtHotkey, txtOut;
    ComboBox cmbAudio, cmbMic, cmbEncoder;
    CheckBox chkStartup, chkMinimize;

    static readonly Color Bg = Color.FromArgb(24, 24, 28), Panel2 = Color.FromArgb(36, 36, 42),
        Fg = Color.FromArgb(235, 235, 240), Dim = Color.FromArgb(150, 150, 160),
        Red = Color.FromArgb(230, 60, 60), Green = Color.FromArgb(60, 200, 110), Amber = Color.FromArgb(230, 170, 50);

    public MainForm(bool selfTest, bool startHidden)
    {
        this.selfTest = selfTest; this.startHidden = startHidden;
        recorder.Cfg = cfg;
        BuildUi();
        BuildTray();
        watchdog.Tick += (s, e) => Watchdog();
    }

    // ---------- UI construction ----------
    void BuildUi()
    {
        Text = "FastClip";
        Icon = MakeIcon(Red);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(460, 560);

        // header / status
        var title = new Label { Text = "FastClip", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Location = new Point(20, 14) };
        statusDot = new Label { Text = "●", Font = new Font("Segoe UI", 14f), ForeColor = Amber, AutoSize = true, Location = new Point(20, 58) };
        statusText = new Label { Text = "Starting…", AutoSize = true, Location = new Point(44, 63) };
        lastClip = new Label { Text = "", ForeColor = Dim, AutoSize = false, Size = new Size(420, 20), Location = new Point(20, 88) };
        Controls.AddRange(new Control[] { title, statusDot, statusText, lastClip });

        // big buttons
        btnSave = MakeButton("Save clip", Red, new Point(20, 116), new Size(200, 50));
        btnSave.Font = new Font("Segoe UI Semibold", 12f);
        btnSave.Click += (s, e) => SaveAsync();
        btnToggle = MakeButton("Stop recording", Panel2, new Point(240, 116), new Size(200, 50));
        btnToggle.Click += (s, e) => { if (wantRecording) StopRec(); else StartRec(); };
        btnFolder = MakeButton("Open clips folder", Panel2, new Point(20, 176), new Size(420, 34));
        btnFolder.Click += (s, e) => { Directory.CreateDirectory(cfg.OutDir); Process.Start("explorer.exe", "\"" + cfg.OutDir + "\""); };
        Controls.AddRange(new Control[] { btnSave, btnToggle, btnFolder });

        // settings group
        var grp = new GroupBox { Text = "Settings", ForeColor = Fg, Location = new Point(20, 222), Size = new Size(420, 290) };
        Controls.Add(grp);
        int y = 26;
        numClip = AddNum(grp, "Clip length (seconds)", ref y, 5, 1800, cfg.ClipSeconds);
        txtHotkey = AddText(grp, "Clip hotkey", ref y, cfg.Hotkey);
        numFps = AddNum(grp, "FPS", ref y, 10, 240, cfg.Fps);
        numBitrate = AddNum(grp, "Bitrate (Mbit/s)", ref y, 1, 100, cfg.Bitrate);
        numMonitor = AddNum(grp, "Monitor #", ref y, 0, 8, cfg.Monitor);
        cmbEncoder = AddCombo(grp, "Encoder", ref y, false);
        cmbEncoder.Items.AddRange(new object[] { "auto", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" });
        cmbEncoder.Text = cfg.Encoder;
        cmbAudio = AddCombo(grp, "Game audio", ref y, true);
        cmbMic = AddCombo(grp, "Microphone", ref y, true);
        cmbAudio.Width = cmbMic.Width = 200;
        cmbAudio.DropDownWidth = cmbMic.DropDownWidth = 380;
        btnRefresh = MakeButton("↻", Panel2, new Point(cmbAudio.Right + 4, cmbAudio.Top - 1), new Size(30, 26));
        btnRefresh.Click += (s, e) => LoadDevices();
        grp.Controls.Add(btnRefresh);
        txtOut = AddText(grp, "Save to", ref y, cfg.OutDir);
        txtOut.Width = 200;
        btnBrowse = MakeButton("…", Panel2, new Point(txtOut.Right + 4, txtOut.Top - 1), new Size(30, 26));
        btnBrowse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { SelectedPath = txtOut.Text })
                if (d.ShowDialog(this) == DialogResult.OK) txtOut.Text = d.SelectedPath;
        };
        grp.Controls.Add(btnBrowse);

        chkStartup = new CheckBox { Text = "Start with Windows", Checked = IsAutostart(), AutoSize = true, Location = new Point(20, 520), ForeColor = Fg };
        chkStartup.CheckedChanged += (s, e) => SetAutostart(chkStartup.Checked);
        chkMinimize = new CheckBox { Text = "Close to tray", Checked = true, AutoSize = true, Location = new Point(180, 520), ForeColor = Fg };
        btnApply = MakeButton("Apply", Green, new Point(340, 516), new Size(100, 30));
        btnApply.ForeColor = Color.Black;
        btnApply.Click += (s, e) => ApplySettings();
        Controls.AddRange(new Control[] { chkStartup, chkMinimize, btnApply });

        cmbAudio.Text = cfg.Audio;
        cmbMic.Text = cfg.Mic;
    }

    Button MakeButton(string text, Color back, Point loc, Size size)
    {
        var b = new Button { Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Fg, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.2f);
        return b;
    }

    Label AddLabel(Control parent, string text, int y)
    {
        var l = new Label { Text = text, Location = new Point(14, y + 3), AutoSize = true, ForeColor = Dim };
        parent.Controls.Add(l); return l;
    }

    NumericUpDown AddNum(Control parent, string label, ref int y, int min, int max, int val)
    {
        AddLabel(parent, label, y);
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), Location = new Point(170, y), Width = 80, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        parent.Controls.Add(n); y += 29; return n;
    }

    TextBox AddText(Control parent, string label, ref int y, string val)
    {
        AddLabel(parent, label, y);
        var t = new TextBox { Text = val, Location = new Point(170, y), Width = 234, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        parent.Controls.Add(t); y += 29; return t;
    }

    ComboBox AddCombo(Control parent, string label, ref int y, bool editable)
    {
        AddLabel(parent, label, y);
        var c = new ComboBox { Location = new Point(170, y), Width = 234, BackColor = Panel2, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
        parent.Controls.Add(c); y += 29; return c;
    }

    void BuildTray()
    {
        tray.Icon = MakeIcon(Red);
        tray.Text = "FastClip";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Save clip", null, (s, e) => SaveAsync());
        menu.Items.Add("Show FastClip", null, (s, e) => ShowWindow());
        menu.Items.Add("Open clips folder", null, (s, e) => btnFolder.PerformClick());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (s, e) => Quit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowWindow();
        tray.Visible = true;
    }

    // ---------- lifecycle ----------
    protected override void SetVisibleCore(bool value)
    {
        // start hidden when launched with --tray (e.g. from Windows startup) or in self-test
        if ((startHidden || selfTest) && !IsHandleCreated) { CreateHandle(); value = false; OnLoad(EventArgs.Empty); }
        base.SetVisibleCore(value);
    }

    protected override void OnLoad(EventArgs e)
    {
        if (loaded) return;
        loaded = true;
        base.OnLoad(e);
        recorder.Ffmpeg = Recorder.FindFfmpeg();
        if (recorder.Ffmpeg == null)
        {
            MessageBox.Show("ffmpeg.exe not found.\n\nPut ffmpeg.exe next to FastClip.exe, or install it with:\n  winget install Gyan.FFmpeg",
                "FastClip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitCode = 2; Quit(); return;
        }
        RegisterHotkey();
        SetStatus(Amber, "Detecting GPU encoder…");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            recorder.Encoder = recorder.PickEncoder();
            var devs = recorder.ListAudioDevices();
            Ui(() => { FillDevices(devs); StartRec(); if (selfTest) RunSelfTest(); });
        });
        watchdog.Start();
    }
    bool loaded;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && chkMinimize.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide();
            tray.ShowBalloonTip(2000, "FastClip is still recording", "Press " + cfg.Hotkey + " to clip. Right-click the tray icon to quit.", ToolTipIcon.Info);
            return;
        }
        if (!quitting) { quitting = true; Cleanup(); }
        base.OnFormClosing(e);
    }

    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void Quit()
    {
        quitting = true;
        Cleanup();
        Close();
        Application.Exit();
    }

    void Cleanup()
    {
        watchdog.Stop();
        recorder.Stop();
        if (hotkey != null) { hotkey.Dispose(); hotkey = null; }
        tray.Visible = false;
    }

    void Ui(Action a) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(a); else a(); }

    // ---------- recording ----------
    void StartRec()
    {
        if (recorder.Encoder == null) return;
        if (DateTime.Now - lastStart < TimeSpan.FromSeconds(10)) quickDeaths++; else quickDeaths = 0;
        lastStart = DateTime.Now;
        if (quickDeaths >= 3)
        {
            wantRecording = false;
            var tail = recorder.LastLog();
            SetStatus(Red, "Recording failed – check audio device names");
            if (tail.Length > 0) lastClip.Text = tail.Split('\n').Last().Trim();
            UpdateToggle();
            return;
        }
        try
        {
            recorder.Start();
            wantRecording = true;
            SetStatus(Green, "Recording · keeping last " + FormatLen(cfg.ClipSeconds) + " · " + recorder.Encoder + " · " + cfg.Fps + " fps");
        }
        catch (Exception ex) { wantRecording = false; SetStatus(Red, "Could not start: " + ex.Message); }
        UpdateToggle();
    }

    void StopRec()
    {
        wantRecording = false;
        recorder.Stop();
        quickDeaths = 0;
        SetStatus(Dim, "Paused (not recording)");
        UpdateToggle();
    }

    void Watchdog()
    {
        if (quitting || !wantRecording || recorder.Encoder == null) return;
        if (!recorder.Running) StartRec();   // ffmpeg died (e.g. display mode change) -> restart
    }

    void UpdateToggle() { btnToggle.Text = wantRecording ? "Stop recording" : "Start recording"; }

    void SetStatus(Color c, string text)
    {
        statusDot.ForeColor = c; statusText.Text = text;
        var t = "FastClip – " + text;
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    static string FormatLen(int s) { return s % 60 == 0 ? (s / 60) + " min" : s + "s"; }

    void SaveAsync()
    {
        if (!wantRecording) { lastClip.Text = "Not recording."; return; }
        btnSave.Enabled = false; btnSave.Text = "Saving…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string path = null, error = null;
            try { path = recorder.Save(); } catch (Exception ex) { error = ex.Message; }
            Ui(() =>
            {
                btnSave.Enabled = true; btnSave.Text = "Save clip";
                if (path != null)
                {
                    System.Media.SystemSounds.Asterisk.Play();
                    lastClip.Text = "Saved " + Path.GetFileName(path);
                    if (!Visible) tray.ShowBalloonTip(2000, "Clip saved", Path.GetFileName(path), ToolTipIcon.Info);
                }
                else
                {
                    lastClip.Text = "Save failed: " + error;
                    if (!Visible) tray.ShowBalloonTip(3000, "Save failed", error, ToolTipIcon.Error);
                }
                if (selfTest) { ExitCode = path != null ? 0 : 1; if (path != null) Console.WriteLine(path); Quit(); }
            });
        });
    }

    void RunSelfTest()
    {
        var t = new System.Windows.Forms.Timer { Interval = 8000 };
        t.Tick += (s, e) => { t.Stop(); SaveAsync(); };
        t.Start();
    }

    // ---------- settings ----------
    void RegisterHotkey()
    {
        if (hotkey != null) { hotkey.Dispose(); hotkey = null; }
        try { hotkey = new HotkeyWindow(cfg.Hotkey); hotkey.Pressed += () => SaveAsync(); }
        catch (Exception ex) { lastClip.Text = ex.Message; }
    }

    void LoadDevices()
    {
        btnRefresh.Enabled = false;
        ThreadPool.QueueUserWorkItem(_ => { var d = recorder.ListAudioDevices(); Ui(() => { FillDevices(d); btnRefresh.Enabled = true; }); });
    }

    void FillDevices(List<string> devs)
    {
        foreach (var c in new[] { cmbAudio, cmbMic })
        {
            string keep = c.Text;
            c.Items.Clear();
            c.Items.Add("");          // blank = off
            c.Items.AddRange(devs.Where(d => c == cmbAudio || !d.StartsWith(Loopback.Prefix)).ToArray());
            c.Text = keep;
        }
    }

    void ApplySettings()
    {
        cfg.ClipSeconds = (int)numClip.Value;
        cfg.Fps = (int)numFps.Value;
        cfg.Bitrate = (int)numBitrate.Value;
        cfg.Monitor = (int)numMonitor.Value;
        cfg.Hotkey = txtHotkey.Text.Trim();
        cfg.Encoder = cmbEncoder.Text;
        cfg.Audio = cmbAudio.Text.Trim();
        cfg.Mic = cmbMic.Text.Trim();
        if (cfg.Mic.Equals(cfg.Audio, StringComparison.OrdinalIgnoreCase)) { cfg.Mic = ""; cmbMic.Text = ""; }
        cfg.OutDir = txtOut.Text.Trim();
        cfg.Save();
        RegisterHotkey();
        quickDeaths = 0;
        btnApply.Enabled = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var enc = recorder.PickEncoder();
            Ui(() => { recorder.Encoder = enc; StartRec(); btnApply.Enabled = true; });
        });
    }

    // ---------- misc ----------
    static Icon MakeIcon(Color c)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 3, 3, 26, 26);
            g.FillEllipse(Brushes.White, 11, 11, 10, 10);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool IsAutostart() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("FastClip") != null; }
    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue("FastClip", "\"" + Application.ExecutablePath + "\" --tray");
            else k.DeleteValue("FastClip", false);
        }
    }
}
