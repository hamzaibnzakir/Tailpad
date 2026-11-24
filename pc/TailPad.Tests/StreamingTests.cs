using System.Text.Json.Nodes;
using TailPad.Core;
using TailPad.Core.Streaming;
using Xunit;

namespace TailPad.Tests;

public class StreamingTests
{
    static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("TAILPAD_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    [Fact]
    public void Access_units_split_on_delimiters_even_across_reads()
    {
        byte[] Aud() => new byte[] { 0, 0, 0, 1, 0x09, 0xF0 };
        byte[] Nal(byte type, int len) => new byte[] { 0, 0, 0, 1, type }.Concat(Enumerable.Repeat((byte)0xAB, len)).ToArray();
        var f1 = Aud().Concat(Nal(0x67, 10)).Concat(Nal(0x68, 4)).Concat(Nal(0x65, 3000)).ToArray(); // SPS PPS IDR
        var f2 = Aud().Concat(Nal(0x41, 500)).ToArray();                                            // P frame
        var f3 = Aud().Concat(Nal(0x41, 700)).ToArray();
        var stream = f1.Concat(f2).Concat(f3).Concat(Aud()).ToArray();

        var r = new AccessUnitReader();
        var aus = new List<byte[]>();
        for (int i = 0; i < stream.Length; i += 7) aus.AddRange(r.Push(stream.AsSpan(i, Math.Min(7, stream.Length - i))));

        Assert.Equal(3, aus.Count);
        Assert.Equal(f1, aus[0]); Assert.Equal(f2, aus[1]); Assert.Equal(f3, aus[2]);
        Assert.True(StreamPackets.IsKeyframe(aus[0]));
        Assert.False(StreamPackets.IsKeyframe(aus[1]));
    }

    [Fact]
    public void Fragments_fit_tailscale_mtu_and_reassemble()
    {
        var au = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var pkts = StreamPackets.Fragment(7, au, true, 123);
        Assert.Equal(5, pkts.Count);
        Assert.All(pkts, p => Assert.True(p.Length <= 1200));
        var asm = new FrameAssembler();
        (uint, byte[], bool)? done = null;
        foreach (var p in pkts.AsEnumerable().Reverse()) done = asm.Push(p) ?? done; // out of order is fine
        Assert.NotNull(done);
        Assert.Equal(au, done!.Value.Item2);
    }

    [Fact]
    public void Lost_packet_drops_frames_until_next_keyframe()
    {
        var asm = new FrameAssembler();
        byte[] Frame(uint n, bool key) => StreamPackets.Fragment(n, new byte[2500], key, 0).SelectMany(x => x).ToArray();
        Assert.NotNull(asm.Push(StreamPackets.Fragment(1, new byte[100], true, 0)[0]));
        var lost = StreamPackets.Fragment(2, new byte[2500], false, 0);
        asm.Push(lost[0]);                                                      // frame 2 never completes
        Assert.Null(asm.Push(StreamPackets.Fragment(3, new byte[100], false, 0)[0])); // P-frame after a loss → held back
        Assert.NotNull(asm.Push(StreamPackets.Fragment(4, new byte[100], true, 0)[0])); // keyframe → back on track
        Assert.NotNull(asm.Push(StreamPackets.Fragment(5, new byte[100], false, 0)[0]));
    }

    [Fact]
    public void Real_ffmpeg_encode_produces_decodable_low_latency_frames()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return; // no ffmpeg on this machine
        var enc = Environment.GetEnvironmentVariable("TAILPAD_TEST_ENCODER")
                  ?? (FfmpegVideo.Probe(ffmpeg, "libx264") ? "libx264" : FfmpegVideo.WindowsEncoders.FirstOrDefault(e => FfmpegVideo.Probe(ffmpeg, e)));
        Assert.NotNull(enc);

        var req = new StreamRequest(720, 60, 8000);
        var args = FfmpegVideo.BuildArgs("testsrc2=s=1280x720:r=60:d=2,format=nv12", enc!, req);
        var asm = new FrameAssembler();
        var frames = new List<(uint, byte[], bool)>();
        var done = new ManualResetEventSlim();
        using var v = new FfmpegVideo(ffmpeg, args, p => { lock (frames) { var f = asm.Push(p); if (f != null) frames.Add(f.Value); } });
        v.Exited += _ => done.Set();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "encoder didn't finish: " + v.LastError);

        Assert.True(frames.Count >= 110, $"only {frames.Count} frames ({v.LastError})");     // 2 s @ 60 fps
        Assert.True(frames[0].Item3, "first frame must be a keyframe");
        var nals = StreamPackets.NalTypes(frames[0].Item2).ToList();
        Assert.Contains(7, nals); Assert.Contains(8, nals); Assert.Contains(5, nals);    // SPS, PPS, IDR
        Assert.All(frames.Where(f => !f.Item3), f => Assert.DoesNotContain(5, StreamPackets.NalTypes(f.Item2))); // keyframe flag is accurate
        Assert.True(frames.Count(f => f.Item3) >= 2, "expected a keyframe every second");     // periodic IDR for loss recovery
        Assert.Equal(Enumerable.Range(1, frames.Count).Select(i => (uint)i), frames.Select(f => f.Item1));
    }
}

public class StreamControlTests : IDisposable
{
    readonly Config _cfg;
    readonly FakeHost _host = new();
    readonly HostServer _server;

    public StreamControlTests()
    {
        var p = 49900 + Random.Shared.Next(0, 300) * 3;
        _cfg = new Config { ControlPort = p, InputPort = p + 1, DsuEnabled = false, PairingCode = "111222" };
        _server = new HostServer(_cfg, new FakeFactory(), _host);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Phone_starts_stream_and_receives_video_on_its_input_socket()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "streamer", code: "111222");
        phone.Input(default); // the PC learns where to send video from the phone's input packets
        await Task.Delay(100);
        await phone.Send(new JsonObject { ["t"] = "stream", ["on"] = true, ["height"] = 720, ["fps"] = 60, ["kbps"] = 9000 });
        var st = await phone.Read("stream");
        Assert.Equal("started", st["state"]!.GetValue<string>());
        Assert.Equal(new StreamRequest(720, 60, 9000, true), _host.LastStream);

        var pkt = StreamPackets.Fragment(1, new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 }, true, 0)[0];
        _host.StreamSend!(pkt);
        var got = await phone.ReceiveUdp();
        Assert.Equal(pkt, got);

        await phone.Send(new JsonObject { ["t"] = "stream", ["on"] = false });
        Assert.Equal("stopped", (await phone.Read("stream"))["state"]!.GetValue<string>());
        Assert.Null(_host.StreamSend);
    }
}

public class WindowsEncoderTests
{
    /// <summary>Real screen capture → encode → packets → frames, on an actual Windows desktop.</summary>
    [Fact]
    public void Windows_screen_capture_streams_frames()
    {
        var ff = Environment.GetEnvironmentVariable("TAILPAD_FFMPEG");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(ff)) return;
        var enc = FfmpegVideo.WindowsEncoders.First(e => FfmpegVideo.Probe(ff, e, 20000));
        var req = new StreamRequest(720, 30, 4000);
        var asm = new FrameAssembler();
        int frames = 0, keys = 0;
        var (input, filter) = Capture.Gdi(0, 0, 640, 480, req);
        var args = FfmpegVideo.BuildArgs(InsertDuration(input), filter, enc, req);
        var done = new ManualResetEventSlim();
        using var v = new FfmpegVideo(ff, args, p => { lock (asm) { var f = asm.Push(p); if (f != null) { frames++; if (f.Value.key) keys++; } } });
        v.Exited += _ => done.Set();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "capture didn't finish: " + v.LastError);
        Console.WriteLine($"GDI capture with {enc}: {frames} frames, {keys} keyframes, {v.Bytes / 1024} KB, last error '{v.LastError}'");
        Assert.True(frames >= 30, $"only {frames} frames: {v.LastError}");
        Assert.True(keys >= 1);
    }

    // put "-t 3" before "-i" so the capture stops by itself
    static string[] InsertDuration(string[] input)
    {
        var l = input.ToList();
        l.Insert(l.IndexOf("-i"), "3"); l.Insert(l.IndexOf("3"), "-t");
        return l.ToArray();
    }

    [Fact]
    public void At_least_one_windows_encoder_works()
    {
        var ff = Environment.GetEnvironmentVariable("TAILPAD_FFMPEG");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(ff)) return;
        var results = FfmpegVideo.WindowsEncoders.Select(e => (e, ok: FfmpegVideo.Probe(ff, e, 20000))).ToList();
        foreach (var (e, ok) in results) Console.WriteLine($"encoder {e}: {(ok ? "works" : "unavailable")}");
        Assert.Contains(results, r => r.ok);
    }
}
