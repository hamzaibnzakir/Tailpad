using System.Net;
using System.Net.Sockets;

namespace TailPad.Tests;

/// <summary>Three consecutive ports that are free for both TCP and UDP right now.
/// (Windows reserves blocks of ports for Hyper-V/WinNAT, so fixed or random picks can fail.)</summary>
static class Ports
{
    static readonly object Gate = new();
    static readonly HashSet<int> Handed = new();

    public static int FreeBlock(int count = 3)
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                int start;
                using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); start = ((IPEndPoint)probe.LocalEndpoint).Port; }
                if (start + count > 65535 || Enumerable.Range(start, count).Any(Handed.Contains)) continue;
                if (Enumerable.Range(start, count).All(Free))
                {
                    foreach (var p in Enumerable.Range(start, count)) Handed.Add(p);
                    return start;
                }
            }
            throw new InvalidOperationException("no free ports");
        }
    }

    static bool Free(int port)
    {
        try
        {
            using var t = new TcpListener(IPAddress.Any, port); t.Start(); t.Stop();
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}
