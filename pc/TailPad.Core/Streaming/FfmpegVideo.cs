using System.Diagnostics;

namespace TailPad.Core.Streaming;

/// <summary>
/// Runs FFmpeg to capture + encode H.264 with low-latency settings, cuts its output into frames
/// and sends them as packets. One instance = one capture target; restart it to change target.
/// </summary>
public sealed class FfmpegVideo : IDisposable
{
    readonly Process _p;
    readonly Action<byte[]> _send;
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock;
    uint _frame;
    volatile bool _stopping;

    public int Frames { get; private set; }
    public long Bytes { get; private set; }
    public string LastError { get; private set; } = "";
    public uint NextFrame => _frame;
    public event Action<FfmpegVideo>? Exited;

    /// <summary>Encoders to try, best first. h264_mf (Windows Media Foundation) exists on every Windows PC.</summary>
    public static readonly string[] WindowsEncoders = { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264" };

    public static List<string> EncoderOptions(string encoder, StreamRequest r) => encoder switch
    {
        "h264_nvenc" => new() { "-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-rc", "cbr", "-delay", "0" },
        "h264_qsv" => new() { "-preset", "veryfast", "-look_ahead", "0", "-async_depth", "1", "-low_power", "1" },
        "h264_amf" => new() { "-usage", "ultralowlatency", "-rc", "cbr", "-quality", "speed" },
        "h264_mf" => new() { "-hw_encoding", "1", "-rate_control", "cbr", "-scenario", "display_remoting" },
        "libopenh264" => new() { "-allow_skip_frames", "1" },
        "libx264" => new() { "-preset", "ultrafast", "-tune", "zerolatency" },
        _ => new(),
    };

    public static List<string> BuildArgs(string inputFilter, string encoder, StreamRequest r)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32",
            "-f", "lavfi", "-i", inputFilter,
            "-an", "-c:v", encoder,
        };
        a.AddRange(EncoderOptions(encoder, r));
        int k = r.Kbps;
        a.AddRange(new[]
        {
            "-g", r.Fps.ToString(), "-bf", "0",   // keyframe every second: a lost packet freezes the picture ≤1 s
            "-b:v", $"{k}k", "-maxrate", $"{k}k", "-bufsize", $"{Math.Max(100, k * 2 / r.Fps)}k", // ~2 frames of buffer
            "-bsf:v", "h264_metadata=aud=insert,dump_extra",  // frame markers + SPS/PPS on every keyframe
            "-flush_packets", "1", "-f", "h264", "pipe:1",
        });
        return a;
    }

    /// <summary>Quick check that an encoder actually works on this PC (driver present, GPU supports it).</summary>
    public static bool Probe(string ffmpeg, string encoder, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var x in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=60,format=nv12",
                                      "-frames:v", "5", "-c:v", encoder })
                psi.ArgumentList.Add(x);
            foreach (var x in EncoderOptions(encoder, new StreamRequest())) psi.ArgumentList.Add(x);
            foreach (var x in new[] { "-bf", "0", "-f", "null", "-" }) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public FfmpegVideo(string ffmpeg, IReadOnlyList<string> args, Action<byte[]> send, uint startFrame = 1, Stopwatch? clock = null)
    {
        _send = send;
        _frame = startFrame;
        _clock = clock ?? Stopwatch.StartNew();
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start the video engine");
        try { _p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _ = Task.Run(ReadVideo);
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _p.StandardError.ReadLineAsync()) != null)
                    if (line.Length > 0) LastError = line;
            }
            catch { }
        });
    }

    async Task ReadVideo()
    {
        var reader = new AccessUnitReader();
        var buf = new byte[64 * 1024];
        var stdout = _p.StandardOutput.BaseStream;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                foreach (var au in reader.Push(buf.AsSpan(0, n)))
                {
                    bool key = StreamPackets.IsKeyframe(au);
                    var t = (uint)_clock.ElapsedMilliseconds;
                    foreach (var pkt in StreamPackets.Fragment(_frame, au, key, t)) _send(pkt);
                    _frame++;
                    Frames++;
                    Bytes += au.Length;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { LastError = e.Message; }
        finally
        {
            try { _p.WaitForExit(2000); } catch { }
            if (!_stopping) Exited?.Invoke(this);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _cts.Cancel();
        try { if (!_p.HasExited) _p.Kill(true); } catch { }
        _p.Dispose();
    }
}
