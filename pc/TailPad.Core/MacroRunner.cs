using System.Text.Json.Nodes;

namespace TailPad.Core;

public sealed record MacroStep(PadState Pad, int Ms);

/// <summary>Plays macros with PC-side timing so they're exact regardless of network jitter.</summary>
public sealed class MacroRunner : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new();
    readonly Dictionary<string, PadState> _overlay = new();
    readonly Action _changed;

    public MacroRunner(Action changed) => _changed = changed;

    public PadState Overlay(in PadState live)
    {
        lock (_gate)
        {
            var r = live;
            foreach (var o in _overlay.Values) r = r.Merge(o);
            return r;
        }
    }

    public bool Active { get { lock (_gate) return _overlay.Count > 0; } }

    public static List<MacroStep> ParseSteps(JsonArray? arr)
    {
        var list = new List<MacroStep>();
        if (arr == null) return list;
        foreach (var n in arr.Take(200))
        {
            if (n is not JsonObject o) continue;
            float F(string k, float lo, float hi) =>
                o[k] is JsonValue v && v.TryGetValue(out double d) ? Math.Clamp((float)d, lo, hi) : 0f;
            var pad = new PadState
            {
                Buttons = o["b"] is JsonValue bv && bv.TryGetValue(out long b) ? (uint)b : 0,
                LT = F("lt", 0, 1), RT = F("rt", 0, 1),
                LX = F("lx", -1, 1), LY = F("ly", -1, 1), RX = F("rx", -1, 1), RY = F("ry", -1, 1),
            };
            var ms = o["ms"] is JsonValue mv && mv.TryGetValue(out long m) ? (int)Math.Clamp(m, 1, 10_000) : 50;
            list.Add(new MacroStep(pad, ms));
        }
        return list;
    }

    public void Start(string id, List<MacroStep> steps, bool loop)
    {
        if (steps.Count == 0) return;
        Stop(id);
        var cts = new CancellationTokenSource();
        lock (_gate) _running[id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    foreach (var s in steps)
                    {
                        lock (_gate) _overlay[id] = s.Pad;
                        _changed();
                        await Task.Delay(s.Ms, cts.Token);
                    }
                    // short gap between loops so repeated presses register as separate presses
                    if (loop) { lock (_gate) _overlay[id] = default; _changed(); await Task.Delay(16, cts.Token); }
                } while (loop && !cts.IsCancellationRequested);
            }
            catch (TaskCanceledException) { }
            finally
            {
                lock (_gate)
                {
                    _overlay.Remove(id);
                    if (_running.TryGetValue(id, out var c) && c == cts) _running.Remove(id);
                }
                _changed();
            }
        });
    }

    public void Stop(string id)
    {
        CancellationTokenSource? cts;
        lock (_gate) _running.Remove(id, out cts);
        cts?.Cancel();
    }

    public void StopAll()
    {
        List<CancellationTokenSource> all;
        lock (_gate) { all = _running.Values.ToList(); _running.Clear(); }
        foreach (var c in all) c.Cancel();
    }

    public void Dispose() => StopAll();
}
