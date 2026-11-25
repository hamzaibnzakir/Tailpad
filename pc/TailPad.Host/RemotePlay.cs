using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using TailPad.Core;
using TailPad.Core.Streaming;

namespace TailPad.Host;

/// <summary>Finds the bundled video engine (FFmpeg) and the best hardware encoder on this PC.</summary>
static class VideoEngine
{
    const string Resource = "ffmpeg.exe";
    static string? _path;
    static string? _encoder;
    static readonly object Gate = new();

    public static string Path()
    {
        lock (Gate)
        {
            if (_path != null && File.Exists(_path)) return _path;
            // a copy next to TailPad.exe wins (handy for updates); otherwise unpack the bundled one once
            var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(beside)) return _path = beside;
            var info = typeof(VideoEngine).Assembly.GetManifestResourceInfo(Resource)
                       ?? throw new InvalidOperationException("This build doesn't include the video engine.");
            using var res = typeof(VideoEngine).Assembly.GetManifestResourceStream(Resource)!;
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailPad", "engine");
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, $"ffmpeg-{res.Length}.exe");
            if (!File.Exists(target) || new FileInfo(target).Length != res.Length)
            {
                var tmp = target + ".tmp";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                File.Move(tmp, target, true);
            }
            return _path = target;
        }
    }

    /// <summary>NVIDIA → Intel → AMD → Windows Media Foundation, whichever actually works here.</summary>
    public static string Encoder()
    {
        lock (Gate)
        {
            if (_encoder != null) return _encoder;
            var ff = Path();
            foreach (var e in FfmpegVideo.WindowsEncoders)
                if (FfmpegVideo.Probe(ff, e)) return _encoder = e;
            throw new InvalidOperationException("No working H.264 encoder found on this PC.");
        }
    }

    public static string Friendly(string enc) => enc switch
    {
        "h264_nvenc" => "NVIDIA",
        "h264_qsv" => "Intel Quick Sync",
        "h264_amf" => "AMD",
        "h264_mf" => "Windows Media Foundation",
        _ => "software",
    };
}

/// <summary>Which window (and where on which monitor) we're streaming.</summary>
readonly record struct CaptureTarget(IntPtr Hwnd, int Output, int X, int Y, int W, int H, string Name, int DesktopX = 0, int DesktopY = 0, bool Hidden = false)
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    /// <summary>The focused game window, or the whole main screen if nothing suitable is focused.</summary>
    public static CaptureTarget Current()
    {
        var h = GetForegroundWindow();
        var target = FromWindow(h);
        return target ?? Whole(Screen.PrimaryScreen!);
    }

    static CaptureTarget Whole(Screen s)
    {
        var idx = Array.IndexOf(Screen.AllScreens, s);
        return new CaptureTarget(IntPtr.Zero, Math.Max(0, idx), 0, 0, Even(s.Bounds.Width), Even(s.Bounds.Height), "Desktop", s.Bounds.Left, s.Bounds.Top);
    }

    static int Even(int v) => v & ~1;

    public static CaptureTarget? FromWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return null;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return null;
        var cls = new System.Text.StringBuilder(128);
        GetClassName(h, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return null;

        if (!GetClientRect(h, out var rc)) return null;
        var p = new POINT();
        if (!ClientToScreen(h, ref p)) return null;
        var screen = Screen.FromHandle(h);
        var b = screen.Bounds;
        // clip the game's client area to its monitor
        int x = Math.Max(p.X, b.Left), y = Math.Max(p.Y, b.Top);
        int r = Math.Min(p.X + rc.R, b.Right), bot = Math.Min(p.Y + rc.B, b.Bottom);
        int w = Even(r - x), hh = Even(bot - y);
        if (w < 320 || hh < 240) return null;
        string name;
        try { using var proc = Process.GetProcessById((int)pid); name = proc.ProcessName; } catch { name = "Game"; }
        return new CaptureTarget(h, Math.Max(0, Array.IndexOf(Screen.AllScreens, screen)), x - b.Left, y - b.Top, w, hh, name, x, y);
    }

    /// <summary>The whole hidden screen (background play).</summary>
    public static CaptureTarget ForHidden(Rectangle area, string name) =>
        new(IntPtr.Zero, 0, 0, 0, area.Width & ~1, area.Height & ~1, name, area.X, area.Y, Hidden: true);

    public (string[] input, string? filter) Input(StreamRequest r, bool gpu) =>
        // the hidden screen isn't one of the GPU's own outputs, so capture it by its desktop position
        gpu && !Hidden ? Capture.Gpu(Output, X, Y, W, H, r) : Capture.Gdi(DesktopX, DesktopY, W, H, r);
}

/// <summary>Captures what the PC is playing (WASAPI loopback), encodes 10 ms Opus packets.</summary>
sealed class GameAudio : IDisposable
{
    readonly IWaveIn _cap;
    readonly IOpusEncoder _enc;
    readonly Action<byte[]> _send;
    readonly Stopwatch _clock;
    readonly float[] _frame = new float[480 * 2];
    readonly byte[] _out = new byte[1500];
    int _fill;
    uint _seq;
    double _resamplePos;
    float _lastL, _lastR;

    /// <param name="source">what to capture; null = everything the PC plays</param>
    public GameAudio(Action<byte[]> send, Stopwatch clock, IWaveIn? source = null)
    {
        _send = send;
        _clock = clock;
        _enc = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _enc.Bitrate = 128_000;
        _enc.Complexity = 5;
        _enc.UseInbandFEC = false;
        _cap = source ?? new WasapiLoopbackCapture();
        _cap.DataAvailable += OnData;
        _cap.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var wf = _cap.WaveFormat;
        int ch = wf.Channels;
        if (ch < 1 || e.BytesRecorded == 0) return;
        int bytesPerSample = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * ch);
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible ext && ext.SubFormat == FloatFormat.Ieee);

        float Sample(int frame, int c)
        {
            int o = (frame * ch + Math.Min(c, ch - 1)) * bytesPerSample;
            if (isFloat && bytesPerSample == 4) return BitConverter.ToSingle(e.Buffer, o);
            if (bytesPerSample == 2) return BitConverter.ToInt16(e.Buffer, o) / 32768f;
            if (bytesPerSample == 3) return ((e.Buffer[o] << 8 | e.Buffer[o + 1] << 16 | e.Buffer[o + 2] << 24) >> 8) / 8388608f;
            if (bytesPerSample == 4) return BitConverter.ToInt32(e.Buffer, o) / 2147483648f;
            return 0;
        }

        // resample to 48 kHz stereo (linear is fine for game audio at these rates)
        double step = wf.SampleRate / 48000.0;
        while (_resamplePos < frames)
        {
            int i = (int)_resamplePos;
            double t = _resamplePos - i;
            float l0 = i == 0 ? _lastL : Sample(i - 1, 0), r0 = i == 0 ? _lastR : Sample(i - 1, 1);
            float l1 = Sample(i, 0), r1 = Sample(i, 1);
            _frame[_fill * 2] = (float)(l0 + (l1 - l0) * t);
            _frame[_fill * 2 + 1] = (float)(r0 + (r1 - r0) * t);
            if (++_fill == 480) Flush();
            _resamplePos += step;
        }
        _resamplePos -= frames;
        _lastL = Sample(frames - 1, 0);
        _lastR = Sample(frames - 1, 1);
    }

    void Flush()
    {
        _fill = 0;
        try
        {
            int n = _enc.Encode(_frame, 480, _out, _out.Length);
            if (n > 0) _send(StreamPackets.Audio(_seq++, (uint)_clock.ElapsedMilliseconds, _out.AsSpan(0, n)));
        }
        catch { }
    }

    public void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}

static class FloatFormat
{
    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    public static readonly Guid Ieee = new("00000003-0000-0010-8000-00aa00389b71");
}

/// <summary>One phone's remote-play session: video follows the focused game window, plus game audio.</summary>
sealed class RemotePlaySession : IDisposable
{
    readonly StreamRequest _req;
    readonly Action<byte[]> _send;
    readonly Action<JsonObject> _status;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly System.Threading.Timer _watch;
    readonly object _gate = new();
    readonly string _ffmpeg, _encoder;
    FfmpegVideo? _video;
    GameAudio? _audio;
    CaptureTarget _target;
    int _lastFrames;
    long _lastBytes;
    int _failures;
    bool _disposed;
    bool _gpuCapture = true;
    readonly BackgroundPlay? _bg;
    ProcessLoopbackCapture? _gameSound;
    int _audioPid = -1;

    public RemotePlaySession(StreamRequest req, Action<byte[]> send, Action<JsonObject> status, BackgroundPlay? bg = null)
    {
        _req = req; _send = send; _status = status; _bg = bg;
        _ffmpeg = VideoEngine.Path();
        _encoder = VideoEngine.Encoder();
        _target = Pick();
        StartVideo();
        UpdateAudio();
        _watch = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>Background play → the hidden screen; otherwise the focused game window.</summary>
    CaptureTarget Pick() =>
        _bg?.Area is { } area ? CaptureTarget.ForHidden(area, _bg.GameName ?? "Game") : CaptureTarget.Current();

    /// <summary>Background play → only the game's sound (boosted back to full); otherwise everything the PC plays.</summary>
    void UpdateAudio()
    {
        if (!_req.Audio) return;
        int pid = _bg?.Active == true ? _bg.GamePid : 0;
        if (_gameSound != null) _gameSound.Gain = Placement.CompensationGain(_bg?.PcVolume ?? 1f);
        if (pid == _audioPid) return;
        lock (_gate)
        {
            if (_disposed) return;
            _audio?.Dispose(); _audio = null; _gameSound = null;
            _audioPid = pid;
            if (pid != 0)
            {
                try
                {
                    _gameSound = new ProcessLoopbackCapture(pid) { Gain = Placement.CompensationGain(_bg!.PcVolume) };
                    _audio = new GameAudio(_send, _clock, _gameSound);
                    return;
                }
                catch (Exception e)
                {
                    _gameSound = null;
                    _status(new JsonObject { ["state"] = "info", ["msg"] = "Sending all PC sound (game-only sound isn't available: " + e.Message + ")" });
                }
            }
            try { _audio = new GameAudio(_send, _clock); }
            catch (Exception e) { _status(new JsonObject { ["state"] = "info", ["msg"] = "No game audio: " + e.Message }); }
        }
    }

    void StartVideo()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var next = _video?.NextFrame ?? 1;
            _video?.Dispose();
            var (input, filter) = _target.Input(_req, _gpuCapture);
            var args = FfmpegVideo.BuildArgs(input, filter, _encoder, _req);
            _video = new FfmpegVideo(_ffmpeg, args, _send, next, _clock);
            _video.Exited += OnVideoExit;
            int outH = Math.Min(_req.Height, _target.H);
            int outW = (int)Math.Round(_target.W * (outH / (double)_target.H)) & ~1;
            _status(new JsonObject
            {
                ["state"] = "started", ["w"] = outW, ["h"] = outH, ["fps"] = _req.Fps,
                ["encoder"] = VideoEngine.Friendly(_encoder) + (_gpuCapture ? "" : " · GDI"), ["target"] = _target.Name,
            });
        }
    }

    void OnVideoExit(FfmpegVideo v)
    {
        if (_disposed || v != _video) return;
        // GPU capture unavailable on this PC (or this monitor) → fall back to GDI capture
        if (_gpuCapture && v.Frames == 0 && (v.LastError.Contains("ddagrab") || v.LastError.Contains("D3D11") || v.LastError.Contains("DXGI") || v.LastError.Length == 0))
        {
            _gpuCapture = false;
            _status(new JsonObject { ["state"] = "info", ["msg"] = "Using compatible screen capture" });
            StartVideo();
            return;
        }
        // the game went exclusive-fullscreen, changed resolution, or the window closed: re-aim and restart
        if (++_failures > 5)
        {
            _status(new JsonObject { ["state"] = "error", ["msg"] = "Video stopped: " + v.LastError });
            return;
        }
        Thread.Sleep(300);
        _target = Pick();
        StartVideo();
    }

    void Tick()
    {
        if (_disposed) return;
        // follow the game: the hidden screen during background play; otherwise re-aim when another real window
        // takes focus, or this one moves/resizes
        var now = Pick();
        if (now.Hidden || _target.Hidden)
        {
            if (now != _target) { _target = now; StartVideo(); }
        }
        else if (now != _target && (now.Hwnd != IntPtr.Zero || _target.Hwnd == IntPtr.Zero || !IsAlive(_target)))
        {
            _target = now;
            StartVideo();
        }
        UpdateAudio();
        var v = _video;
        if (v == null) return;
        int f = v.Frames; long b = v.Bytes;
        int fps = f - _lastFrames; long bytes = b - _lastBytes;
        _lastFrames = f; _lastBytes = b;
        if (fps > 0) _failures = 0;
        _status(new JsonObject { ["state"] = "stats", ["fps"] = Math.Max(0, fps), ["kbps"] = Math.Max(0, bytes * 8 / 1000) });
    }

    static bool IsAlive(CaptureTarget t) => CaptureTarget.FromWindow(t.Hwnd) != null;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watch.Dispose();
            _video?.Dispose();
            _audio?.Dispose();
        }
    }
}
