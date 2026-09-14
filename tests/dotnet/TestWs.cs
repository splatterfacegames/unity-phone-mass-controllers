// Raw-socket WebSocket client + host pump for NUnit: enough wire control to send
// deliberately broken frames (unmasked, reserved bits, bad opcodes, mid-fragment
// control frames) — mirrors tests/lib/test_socket.gd + test_ws_client.gd.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

internal sealed class HostFixture : IDisposable {
    public PmcHostCore Host;
    private Thread _pump;
    private volatile bool _stop;
    public readonly List<string> Errors = new List<string>();

    public static HostFixture Start(Action<PmcHostCore> configure = null) {
        var f = new HostFixture();
        f.Host = new PmcHostCore { Port = 0, ControllerDir = "" };
        configure?.Invoke(f.Host);
        int err = f.Host.Start();
        Assert.That(err, Is.EqualTo(0), "host.Start failed");
        f._pump = new Thread(f.Loop) { IsBackground = true, Name = "pmc-test-pump" };
        f._pump.Start();
        return f;
    }

    private void Loop() {
        while (!_stop) {
            try {
                Host.Poll(32);
            } catch (Exception e) {
                lock (Errors) Errors.Add(e.ToString());
            }
            Thread.Sleep(1);
        }
        Host.Poll(32);
    }

    public int Port { get { return Host.BoundPort; } }

    /// <summary>Pump the host until cond is true or the timeout passes. Because events fire inside
    /// Poll, most test assertions just need this.</summary>
    public bool Until(Func<bool> cond, int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (cond()) return true;
            lock (Errors) {
                if (Errors.Count > 0) Assert.Fail("pump error: " + Errors[0]);
            }
            Thread.Sleep(2);
        }
        return cond();
    }

    public void Dispose() {
        _stop = true;
        _pump?.Join(2000);
        Host?.Dispose();
    }
}

/// <summary>A raw WebSocket client speaking just enough protocol to misbehave.</summary>
internal sealed class TestWs : IDisposable {
    public Socket Sock;
    public int Port;
    private readonly List<Frame> _frames = new List<Frame>();
    private byte[] _buf = new byte[0];

    public struct Frame {
        public bool Fin;
        public int Op;
        public byte[] Payload;
    }

    public TestWs(int port) {
        Port = port;
        Sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Sock.Connect(IPAddress.Loopback, port);
        Sock.NoDelay = true;
    }

    /// <summary>Sends the upgrade request and waits for a valid 101.</summary>
    public void Upgrade(string path = "/pmc/ws", string extraHeaders = "") {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        SendRaw("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\n"
            + "Connection: Upgrade\r\nSec-WebSocket-Key: " + key
            + "\r\nSec-WebSocket-Version: 13\r\n" + extraHeaders + "\r\n");
        string head = ReadHead();
        Assert.That(head.StartsWith("HTTP/1.1 101"), Is.True, "upgrade failed: " + head);
        string accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        Assert.That(head, Does.Contain("Sec-WebSocket-Accept: " + accept));
    }

    /// <summary>Sends a raw HTTP request head and returns the whole response head.</summary>
    public string ReadHead() {
        long deadline = Environment.TickCount64 + 8000;
        string head = "";
        while (Environment.TickCount64 < deadline) {
            int end = IndexOf(head, "\r\n\r\n");
            if (end >= 0) {
                // Stash any bytes after the head into the frame buffer.
                int headLen = end + 4;
                if (head.Length > headLen) {
                    AppendBuf(Encoding.Latin1.GetBytes(head.Substring(headLen)));
                    head = head.Substring(0, headLen);
                }
                return head;
            }
            head += ReadChunk();
        }
        throw new TimeoutException("no complete HTTP head");
    }

    private static int IndexOf(string hay, string needle) {
        return hay.IndexOf(needle, StringComparison.Ordinal);
    }

    public void SendRaw(string s) {
        byte[] b = Encoding.Latin1.GetBytes(s);
        Sock.Send(b);
    }

    public void SendRaw(byte[] b) {
        Sock.Send(b);
    }

    private string ReadChunk() {
        var b = new byte[65536];
        Sock.ReceiveTimeout = 8000;
        int n = Sock.Receive(b);
        if (n <= 0) throw new EndOfStreamException("peer closed");
        return Encoding.Latin1.GetString(b, 0, n);
    }

    private void AppendBuf(byte[] b) {
        var m = new byte[_buf.Length + b.Length];
        Buffer.BlockCopy(_buf, 0, m, 0, _buf.Length);
        Buffer.BlockCopy(b, 0, m, _buf.Length, b.Length);
        _buf = m;
    }

    /// <summary>Sends one client frame (masked by default).</summary>
    public void Send(int op, byte[] payload, bool fin = true, bool mask = true, int rsv = 0) {
        payload = payload ?? new byte[0];
        var head = new List<byte> { (byte)((fin ? 0x80 : 0) | rsv | (op & 0x0F)) };
        int n = payload.Length;
        int m = mask ? 0x80 : 0;
        if (n < 126) {
            head.Add((byte)(m | n));
        } else if (n < 65536) {
            head.Add((byte)(m | 126));
            head.Add((byte)(n >> 8));
            head.Add((byte)n);
        } else {
            head.Add((byte)(m | 127));
            long big = n;
            for (int s = 7; s >= 0; s--) head.Add((byte)((big >> (s * 8)) & 0xFF));
        }
        byte[] body;
        if (mask) {
            var mk = RandomNumberGenerator.GetBytes(4);
            head.AddRange(mk);
            body = new byte[n];
            for (int i = 0; i < n; i++) body[i] = (byte)(payload[i] ^ mk[i & 3]);
        } else {
            body = payload;
        }
        var frame = new byte[head.Count + body.Length];
        head.CopyTo(frame, 0);
        Buffer.BlockCopy(body, 0, frame, head.Count, body.Length);
        Sock.Send(frame);
    }

    public void SendText(string s, bool fin = true) {
        Send(1, Encoding.UTF8.GetBytes(s), fin);
    }

    /// <summary>Waits for and returns the next server frame.</summary>
    public Frame Recv(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            var f = TryParse();
            if (f.HasValue) return f.Value;
            Sock.ReceiveTimeout = Math.Max(1, (int)(deadline - Environment.TickCount64));
            var b = new byte[262144];
            int n;
            try {
                n = Sock.Receive(b);
            } catch (SocketException) {
                continue;
            }
            if (n <= 0) throw new EndOfStreamException("peer closed while waiting for frame");
            var nb = new byte[n];
            Buffer.BlockCopy(b, 0, nb, 0, n);
            AppendBuf(nb);
        }
        throw new TimeoutException("no frame within " + timeoutMs + " ms");
    }

    private Frame? TryParse() {
        if (_buf.Length < 2) return null;
        int len = _buf[1] & 0x7F;
        int off = 2;
        long n;
        if (len == 126) {
            if (_buf.Length < 4) return null;
            n = (_buf[2] << 8) | _buf[3];
            off = 4;
        } else if (len == 127) {
            if (_buf.Length < 10) return null;
            n = 0;
            for (int i = 0; i < 8; i++) n = (n << 8) | _buf[2 + i];
            off = 10;
        } else {
            n = len;
        }
        if (_buf.Length < off + n) return null;
        var f = new Frame {
            Fin = (_buf[0] & 0x80) != 0,
            Op = _buf[0] & 0x0F,
            Payload = new byte[n],
        };
        Buffer.BlockCopy(_buf, off, f.Payload, 0, (int)n);
        _buf = Slice(_buf, off + (int)n);
        return f;
    }

    private static byte[] Slice(byte[] src, int off) {
        var d = new byte[src.Length - off];
        Buffer.BlockCopy(src, off, d, 0, d.Length);
        return d;
    }

    /// <summary>The last pmc.welcome this client received (set by Hello()).</summary>
    public JObject LastWelcome;

    /// <summary>Completes pmc.hello and returns the pmc.welcome JSON.</summary>
    public JObject Hello(object extra = null) {
        var hello = new JObject { ["t"] = "pmc.hello", ["sdk"] = 1 };
        if (extra is JObject o) {
            foreach (var kv in o) hello[kv.Key] = kv.Value;
        }
        SendText(hello.ToString(Newtonsoft.Json.Formatting.None));
        var f = Recv();
        Assert.That(f.Op, Is.EqualTo(1), "expected text frame");
        var m = JObject.Parse(Encoding.UTF8.GetString(f.Payload));
        Assert.That((string)m["t"], Is.EqualTo("pmc.welcome"), "expected pmc.welcome, got " + m);
        LastWelcome = m;
        return m;
    }

    /// <summary>Waits for the next TEXT frame and parses it as JSON.</summary>
    public JObject RecvJson(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            var f = Recv((int)Math.Max(1, deadline - Environment.TickCount64));
            if (f.Op == 9) continue; // heartbeat pings are ignored here
            Assert.That(f.Op, Is.EqualTo(1), "expected text frame, got op " + f.Op);
            return JObject.Parse(Encoding.UTF8.GetString(f.Payload));
        }
        throw new TimeoutException("no text frame");
    }

    /// <summary>Reads a close frame; returns the code (1005 when the payload is empty).</summary>
    public int RecvClose(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            var f = Recv((int)Math.Max(1, deadline - Environment.TickCount64));
            if (f.Op != 8) continue;
            return f.Payload.Length >= 2 ? (f.Payload[0] << 8) | f.Payload[1] : 1005;
        }
        throw new TimeoutException("no close frame");
    }

    /// <summary>Non-blocking check: true once the peer has closed the TCP connection. Drains any
    /// pending bytes into the frame buffer first so a FIN sitting behind them is detected.</summary>
    public bool IsTcpClosed() {
        try {
            if (!Sock.Poll(0, SelectMode.SelectRead)) return false;
            if (Sock.Available == 0) return true;
            var b = new byte[65536];
            int n = Sock.Receive(b);
            if (n <= 0) return true;
            var nb = new byte[n];
            Buffer.BlockCopy(b, 0, nb, 0, n);
            AppendBuf(nb);
            return false;
        } catch (Exception) {
            return true;
        }
    }

    /// <summary>Waits until the TCP socket is closed by the peer.</summary>
    public bool WaitTcpClose(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            try {
                if (Sock.Poll(0, SelectMode.SelectRead) && Sock.Available == 0) return true;
                var b = new byte[4096];
                int n = Sock.Receive(b);
                if (n <= 0) return true;
                var got = new byte[n];
                Buffer.BlockCopy(b, 0, got, 0, n);
                AppendBuf(got);
            } catch (Exception) {
                return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }

    public void Dispose() {
        try { Sock?.Dispose(); } catch (Exception) { }
    }
}

/// <summary>Plain HTTP/1.1 fetch over a raw socket (keeps the response head + body text).</summary>
internal static class HttpGet {
    public static string Raw(int port, string request) {
        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
            s.Connect(IPAddress.Loopback, port);
            s.Send(Encoding.Latin1.GetBytes(request));
            s.Shutdown(SocketShutdown.Send);
            var outp = new StringBuilder();
            var b = new byte[65536];
            s.ReceiveTimeout = 8000;
            try {
                while (true) {
                    int n = s.Receive(b);
                    if (n <= 0) break;
                    outp.Append(Encoding.Latin1.GetString(b, 0, n));
                }
            } catch (SocketException) {
            }
            return outp.ToString();
        }
    }

    public static string Get(int port, string path, string headers = "") {
        return Raw(port, "GET " + path + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n" + headers + "\r\n");
    }
}
