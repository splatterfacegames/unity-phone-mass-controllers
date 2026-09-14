// Headless echo host for the Node interop and load tests — the C# port of
// tests/lib/pmc_test_server.gd from the Godot addon.
//
// PmcHarness [--port=0] [--fps=60] [--heartbeat=15] [--grace=30] [--code=]
//            [--max-message=1048576] [--budget=8] [--per-address=0]
// Prints "PMC_READY port=<n>" when listening. Behaviour:
// - binary frames are echoed back
// - msg frames are echoed as {"echo": d}, except commands in d.cmd:
//   "stats"        -> {"stats": {...}}  (poll/frame timing percentiles since the last reset, plus host counters)
//   "reset_stats"  -> {"reset": true}
//   "broadcast_hz" (d.hz, d.bytes) -> starts/stops a periodic broadcast of a d.bytes-sized JSON state
//   "quit"         -> exits

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using Splatter.Pmc;

internal static class Program {
    private static PmcHostCore _host;
    private static readonly List<long> PollSamples = new List<long>();
    private static readonly List<long> FrameSamples = new List<long>();
    private static long _lastFrameUsec;
    private static double _broadcastHz;
    private static int _broadcastBytes = 100;
    private static double _broadcastAccum;
    private static long _broadcastSeq;

    private static int Main(string[] args) {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string a in args) {
            if (a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('=') > 2) {
                int eq = a.IndexOf('=');
                parsed[a.Substring(2, eq - 2)] = a.Substring(eq + 1);
            }
        }
        int fps = GetInt(parsed, "fps", 60);

        _host = new PmcHostCore {
            Port = GetInt(parsed, "port", 0),
            ControllerDir = "",
            HeartbeatSeconds = GetFloat(parsed, "heartbeat", 15f),
            GraceSeconds = GetFloat(parsed, "grace", 30f),
            JoinCode = GetStr(parsed, "code", ""),
            MaxMessageBytes = GetInt(parsed, "max-message", 1 << 20),
            MaxConnections = 1024,
            IoBudgetMsec = GetFloat(parsed, "budget", 8f),
            MaxConnectionsPerAddress = GetInt(parsed, "per-address", 0),
        };
        _host.MessageReceived += OnMessage;
        int err = _host.Start();
        if (err != 0) {
            Console.Error.WriteLine("PMC_FAILED " + err);
            return 1;
        }
        Console.WriteLine("PMC_READY port=" + _host.BoundPort);
        Console.Out.Flush();

        long frameTicks = (long)(Stopwatch.Frequency / Math.Max(1, fps));
        long prev = Stopwatch.GetTimestamp();
        while (true) {
            long now = Stopwatch.GetTimestamp();
            double delta = (double)(now - prev) / Stopwatch.Frequency;
            if (_lastFrameUsec > 0 && FrameSamples.Count < 2000000) {
                FrameSamples.Add(ToUsec(now - prev));
            }
            _lastFrameUsec = ToUsec(now);
            prev = now;
            long t0 = Stopwatch.GetTimestamp();
            _host.Poll();
            long pollDt = Stopwatch.GetTimestamp() - t0;
            if (_broadcastHz > 0.0) {
                _broadcastAccum += delta;
                double step = 1.0 / _broadcastHz;
                if (_broadcastAccum >= step) {
                    _broadcastAccum = _broadcastAccum % step;
                    _broadcastSeq += 1;
                    _host.Broadcast(new JObject {
                        ["state"] = _broadcastSeq,
                        ["pad"] = new string('x', Math.Max(0, _broadcastBytes - 40)),
                    });
                }
            }
            if (PollSamples.Count < 2000000) {
                PollSamples.Add(ToUsec(pollDt));
            }
            // Cap the loop at --fps like Engine.max_fps.
            long target = now + frameTicks;
            long rem = target - Stopwatch.GetTimestamp();
            while (rem > 0) {
                if (rem > Stopwatch.Frequency / 500) {
                    Thread.Sleep(1);
                } else {
                    Thread.SpinWait(64);
                }
                rem = target - Stopwatch.GetTimestamp();
            }
        }
    }

    private static long ToUsec(long ticks) {
        return ticks * 1000000L / Stopwatch.Frequency;
    }

    private static void OnMessage(PmcPlayer p, object d) {
        var bytes = d as byte[];
        if (bytes != null) {
            _host.Send(p, bytes);
            return;
        }
        var obj = d as JObject;
        var cmd = obj != null ? obj["cmd"] : null;
        if (cmd != null && cmd.Type == JTokenType.String) {
            switch ((string)cmd) {
                case "stats":
                    _host.Send(p, new JObject { ["stats"] = Stats() });
                    return;
                case "reset_stats":
                    PollSamples.Clear();
                    FrameSamples.Clear();
                    _host.ResetPollStats();
                    _host.Send(p, new JObject { ["reset"] = true });
                    return;
                case "broadcast_hz":
                    _broadcastHz = ToDouble(obj["hz"], 0);
                    _broadcastBytes = ToInt(obj["bytes"], 100);
                    _host.Send(p, new JObject { ["broadcast_hz"] = _broadcastHz });
                    return;
                case "quit":
                    Environment.Exit(0);
                    return;
            }
            return;
        }
        _host.Send(p, new JObject { ["echo"] = d is JToken ? (JToken)d : JValue.CreateNull() });
    }

    private static JObject Pct(List<long> samples) {
        if (samples.Count == 0) {
            return new JObject { ["n"] = 0 };
        }
        var s = new List<long>(samples);
        s.Sort();
        long total = 0;
        foreach (long v in s) total += v;
        int n = s.Count;
        return new JObject {
            ["n"] = n,
            ["avg_ms"] = total / (double)n / 1000.0,
            ["p50_ms"] = s[n / 2] / 1000.0,
            ["p95_ms"] = s[Math.Min(n - 1, (int)(n * 0.95))] / 1000.0,
            ["p99_ms"] = s[Math.Min(n - 1, (int)(n * 0.99))] / 1000.0,
            ["max_ms"] = s[n - 1] / 1000.0,
        };
    }

    private static JObject Stats() {
        return new JObject {
            ["poll"] = Pct(PollSamples),
            ["frame"] = Pct(FrameSamples),
            ["host"] = _host.GetStats(),
        };
    }

    private static string GetStr(Dictionary<string, string> a, string k, string def) {
        return a.TryGetValue(k, out var v) ? v : def;
    }

    private static int GetInt(Dictionary<string, string> a, string k, int def) {
        return a.TryGetValue(k, out var v) && int.TryParse(v, out int r) ? r : def;
    }

    private static float GetFloat(Dictionary<string, string> a, string k, float def) {
        return a.TryGetValue(k, out var v) && float.TryParse(v, out float r) ? r : def;
    }

    private static int ToInt(JToken t, int def) {
        return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            ? (int)t : def;
    }

    private static double ToDouble(JToken t, double def) {
        return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            ? (double)t : def;
    }
}
