using System.ComponentModel;
using System.Diagnostics;

namespace TailPad.Host;

/// <summary>
/// The ViGEmBus controller driver ships inside TailPad.exe, so there's nothing extra to download.
/// First launch: one Windows permission prompt, then it installs silently.
/// </summary>
static class DriverSetup
{
    const string Resource = "ViGEmBus_Setup.exe";

    public static bool Bundled => typeof(DriverSetup).Assembly.GetManifestResourceInfo(Resource) != null;

    public enum Result { Installed, Declined, Failed, NotBundled }

    public static Result Install()
    {
        if (!Bundled) return Result.NotBundled;
        var path = Path.Combine(Path.GetTempPath(), "TailPad_ViGEmBus_Setup.exe");
        try
        {
            using (var res = typeof(DriverSetup).Assembly.GetManifestResourceStream(Resource)!)
            using (var f = File.Create(path))
                res.CopyTo(f);

            // silent first; if that fails, show the normal installer so it can still be finished by hand
            var code = Run(path, "/exenoui /qn /norestart");
            if (code is 0 or 3010 or 1641) return Result.Installed;  // 3010/1641 = success, reboot suggested
            if (code == 1223) return Result.Declined;               // user said No to the permission prompt
            code = Run(path, "");
            return code is 0 or 3010 or 1641 ? Result.Installed : code == 1223 ? Result.Declined : Result.Failed;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return Result.Declined;
        }
        catch
        {
            return Result.Failed;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" })!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
