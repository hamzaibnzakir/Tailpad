using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>
/// Captures the sound of one program (and the processes it started) — Windows 10 2004+ "process loopback".
/// Used for background play so the phone gets only the game, not your music or calls.
/// </summary>
sealed class ProcessLoopbackCapture : IWaveIn
{
    readonly int _pid;
    AudioClient? _client;
    Thread? _thread;
    volatile bool _stop;
    readonly AutoResetEvent _ready = new(false);

    /// <summary>Multiplier applied to the captured sound (undoes the PC-side volume duck).</summary>
    public volatile float Gain = 1f;

    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ProcessLoopbackCapture(int processId) => _pid = processId;

    // ---- ActivateAudioInterfaceAsync plumbing ----

    const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public int TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB { public ushort vt; public ushort r1, r2, r3; public int cbSize; public IntPtr pBlobData; }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out var hr, out var obj);
                if (hr != 0) Done.TrySetException(Marshal.GetExceptionForHR(hr)!);
                else Done.TrySetResult((IAudioClient)obj);
            }
            catch (Exception e) { Done.TrySetException(e); }
        }
    }

    static IAudioClient Activate(int pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1 /* PROCESS_LOOPBACK */, TargetProcessId = pid, ProcessLoopbackMode = 0 /* include tree */ };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf(p));
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = Marshal.SizeOf(p), pBlobData = pParams }, pVariant, false);
            var h = new Handler();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, typeof(IAudioClient).GUID, pVariant, h, out _);
            if (!h.Done.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows didn't open the game's sound");
            return h.Done.Task.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    public void StartRecording()
    {
        _client = new AudioClient(Activate(_pid));
        // Process loopback has no mix format of its own: ask for 48 kHz float stereo and let Windows convert.
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            200_000 /* 20 ms */, 0, WaveFormat, Guid.Empty);
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        _client.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "TailPad game sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Loop()
    {
        Exception? error = null;
        try
        {
            var cap = _client!.AudioCaptureClient;
            var buf = new byte[48000 * 8];
            while (!_stop)
            {
                _ready.WaitOne(100);
                int packet;
                while (!_stop && (packet = cap.GetNextPacketSize()) > 0)
                {
                    var ptr = cap.GetBuffer(out int frames, out var flags);
                    int bytes = frames * 8;
                    if (bytes > buf.Length) buf = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buf, 0, bytes);
                    else Marshal.Copy(ptr, buf, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    ApplyGain(buf.AsSpan(0, bytes), Gain);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes));
                }
            }
        }
        catch (Exception e) { error = e; }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    static void ApplyGain(Span<byte> pcm, float gain)
    {
        if (gain == 1f) return;
        var f = MemoryMarshal.Cast<byte, float>(pcm);
        for (int i = 0; i < f.Length; i++) f[i] = Math.Clamp(f[i] * gain, -1f, 1f);
    }

    public void StopRecording()
    {
        _stop = true;
        _thread?.Join(500);
        try { _client?.Stop(); } catch { }
    }

    public void Dispose()
    {
        StopRecording();
        _client?.Dispose();
        _ready.Dispose();
    }
}

/// <summary>
/// Turns a game almost all the way down on the PC so you don't hear it while you work
/// (Windows applies per-app volume before capture, so a full mute would silence the phone too).
/// Remembers the original volume and puts it back afterwards — also after a crash, via the config file.
/// </summary>
sealed class GameVolumeDuck : IDisposable
{
    readonly Config _cfg;
    readonly HashSet<int> _pids;
    readonly string _exe;
    readonly Dictionary<string, float> _original = new();
    readonly System.Threading.Timer _timer;
    float _current = 1f;

    /// <summary>The game's volume on the PC right now (to undo it on the phone).</summary>
    public float Level => _current;

    public GameVolumeDuck(Config cfg, int pid, string exe)
    {
        _cfg = cfg;
        _pids = new HashSet<int> { pid };
        _exe = exe;
        lock (_cfg)
        {
            if (!_cfg.DuckedApps.Contains(exe, StringComparer.OrdinalIgnoreCase)) _cfg.DuckedApps.Add(exe);
            _cfg.Save();
        }
        Apply();
        _timer = new System.Threading.Timer(_ => Apply(), null, 1000, 1000); // games often open their sound a bit later
    }

    void Apply()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    int pid = (int)s.GetProcessID;
                    if (!_pids.Contains(pid) && !WindowTools.ExeOf(pid).Equals(_exe, StringComparison.OrdinalIgnoreCase)) continue;
                    var key = dev.ID + "|" + s.GetSessionInstanceIdentifier;
                    var vol = s.SimpleAudioVolume;
                    if (!_original.ContainsKey(key))
                    {
                        _original[key] = vol.Volume <= Placement.DuckVolume * 1.5f ? 1f : vol.Volume;
                        vol.Volume = Placement.DuckVolume;
                    }
                    _current = vol.Mute ? 0f : vol.Volume;
                }
            }
        }
        catch { /* audio devices changing — try again next second */ }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Restore(_original.Keys.ToHashSet(), _exe, _original);
        lock (_cfg) { _cfg.DuckedApps.RemoveAll(e => e.Equals(_exe, StringComparison.OrdinalIgnoreCase)); _cfg.Save(); }
    }

    /// <summary>On startup: undo a duck left behind by a crash (volume still at the duck level → back to full).</summary>
    public static void RecoverAfterCrash(Config cfg)
    {
        List<string> exes;
        lock (cfg) exes = cfg.DuckedApps.ToList();
        if (exes.Count == 0) return;
        foreach (var exe in exes) Restore(null, exe, null);
        lock (cfg) { cfg.DuckedApps.Clear(); cfg.Save(); }
    }

    static void Restore(HashSet<string>? keys, string exe, Dictionary<string, float>? original)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    var key = dev.ID + "|" + s.GetSessionInstanceIdentifier;
                    bool ours = keys?.Contains(key) == true ||
                                WindowTools.ExeOf((int)s.GetProcessID).Equals(exe, StringComparison.OrdinalIgnoreCase);
                    if (!ours) continue;
                    var vol = s.SimpleAudioVolume;
                    if (original != null && original.TryGetValue(key, out var v)) vol.Volume = v;
                    else if (vol.Volume <= Placement.DuckVolume * 1.5f) vol.Volume = 1f;
                }
            }
        }
        catch { }
    }
}
