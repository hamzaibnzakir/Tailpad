using System.Diagnostics;
using TailPad.Core;
using TailPad.Core.Streaming;

namespace TailPad.Host;

/// <summary>
/// `TailPad.exe --selftest-hidden &lt;folder&gt;` (run by CI on a real Windows machine, as administrator):
/// installs the hidden-screen driver, switches the screen on, moves a window onto it, captures it,
/// then cleans up. Writes a report and a snapshot PNG.
/// </summary>
static class SelfTest
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        void L(string s) { log.Add(s); Console.WriteLine(s); }
        bool ok = true;
        Process? child = null;
        try
        {
            L($"Driver bundled: {HiddenScreen.Bundled}");
            if (!HiddenScreen.Installed)
            {
                var code = HiddenScreen.InstallElevated();
                L($"Install exit code: {code}");
                for (int i = 0; i < 50 && !HiddenScreen.Installed; i++) Thread.Sleep(200);
            }
            L($"Installed: {HiddenScreen.Installed}");
            if (!HiddenScreen.Installed) return Finish(false);

            var primary = HiddenScreen.Primary;
            var area = HiddenScreen.On();
            L($"Primary: {primary}  Hidden screen: {area}");
            ok &= Check(area.Width == HiddenScreen.Width && area.Height == HiddenScreen.Height, "hidden screen is 1280x720");
            ok &= Check(!area.IntersectsWith(primary), "hidden screen doesn't overlap the real screen");
            ok &= Check(area.Right <= primary.Left && area.Bottom <= primary.Top, "hidden screen sits diagonally off the corner");

            // a stand-in "game": TailPad's test window in its own process
            var started = DateTime.Now;
            child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-window") { UseShellExecute = false })!;
            Placement.WindowInfo? game = null;
            for (int i = 0; i < 40 && game == null; i++)
            {
                Thread.Sleep(250);
                game = Placement.PickGameWindow(WindowTools.All(), started, "TailPad.exe");
            }
            ok &= Check(game != null, "found the new game window after launch");
            if (game == null) return Finish(false);
            var h = game.Value.Handle;
            var before = WindowTools.RectOf(h);
            var style = WindowTools.MoveTo(h, area);
            Thread.Sleep(1500); // Windows adds the taskbar to a new screen after a moment
            WindowTools.HideTaskbarsOn(area);
            Thread.Sleep(500);
            var after = WindowTools.RectOf(h);
            L($"Window before: {before}  after: {after}");
            var overlap = Rectangle.Intersect(after, area);
            ok &= Check(overlap.Width * overlap.Height >= area.Width * area.Height * 0.95, "game window fills the hidden screen");
            ok &= Check(!after.IntersectsWith(primary), "nothing of the game is left on the real screen");
            var client = WindowTools.ClientOnScreen(h);
            L($"Game picture: {client}");
            ok &= Check(client == area, "game picture is exactly the hidden screen (no title bar)");
            ok &= Check(!WindowTools.TaskbarVisibleOn(area), "no taskbar on the hidden screen");

            // capture exactly what the phone would get
            var ff = VideoEngine.Path();
            var req = new StreamRequest(720, 30, 4000);
            var target = CaptureTarget.ForHidden(area, "test");
            var (input, filter) = target.Input(req, gpu: true);
            var snap = Path.Combine(dir, "hidden-screen.png");
            var psi = new ProcessStartInfo(ff) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(input).Concat(new[] { "-frames:v", "1", snap }))
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { var err = p.StandardError.ReadToEnd(); p.WaitForExit(30000); if (err.Length > 0) L("ffmpeg: " + err.Trim()); }
            ok &= Check(File.Exists(snap) && new FileInfo(snap).Length > 1000, "captured a picture of the hidden screen");

            int frames = 0;
            var asm = new FrameAssembler();
            var done = new ManualResetEventSlim();
            var enc = FfmpegVideo.WindowsEncoders.First(e => FfmpegVideo.Probe(ff, e, 20000));
            var withTime = input.ToList(); withTime.Insert(withTime.IndexOf("-i"), "-t"); withTime.Insert(withTime.IndexOf("-i"), "2");
            using (var v = new FfmpegVideo(ff, FfmpegVideo.BuildArgs(withTime, filter, enc, req), pkt => { lock (asm) if (asm.Push(pkt) != null) frames++; }))
            {
                v.Exited += _ => done.Set();
                done.Wait(TimeSpan.FromSeconds(40));
                L($"Streamed {frames} frames from the hidden screen with {enc} ({v.LastError})");
            }
            ok &= Check(frames >= 20, "streamed the hidden screen");

            WindowTools.Restore(h, before, style);
            Thread.Sleep(300);
            ok &= Check(WindowTools.RectOf(h) == before, "game comes back to its old place with its frame");
            HiddenScreen.Off();
            Thread.Sleep(1000);
            ok &= Check(HiddenScreen.Bounds == null, "hidden screen removed afterwards");
            return Finish(ok);
        }
        catch (Exception e)
        {
            L("FAILED: " + e);
            return Finish(false);
        }
        finally
        {
            try { child?.Kill(); } catch { }
            try { HiddenScreen.Off(); } catch { }
            File.WriteAllLines(Path.Combine(dir, "hidden-screen-report.txt"), log);
        }

        bool Check(bool cond, string what) { L((cond ? "PASS  " : "FAIL  ") + what); return cond; }
        int Finish(bool pass) { L(pass ? "ALL PASSED" : "SOME CHECKS FAILED"); return pass ? 0 : 1; }
    }

    /// <summary>A colourful 960x540 window that stands in for a game.</summary>
    public static void TestWindow()
    {
        var f = new Form
        {
            Text = "TailPad test game", Width = 960, Height = 540, StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(0x1F, 0x6F, 0x8B),
        };
        f.Paint += (_, e) =>
        {
            using var b = new SolidBrush(Color.FromArgb(0x39, 0xFF, 0x88));
            e.Graphics.FillEllipse(b, f.ClientSize.Width / 2 - 120, f.ClientSize.Height / 2 - 120, 240, 240);
            using var font = new Font("Segoe UI", 28, FontStyle.Bold);
            e.Graphics.DrawString("TailPad hidden screen", font, Brushes.White, 24, 24);
        };
        f.Resize += (_, _) => f.Invalidate();
        Application.Run(f);
    }
}
