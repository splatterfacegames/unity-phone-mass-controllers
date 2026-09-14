// Raw-socket HTTP/1.1 client for NUnit: pipelining, split sends, keep-alive and a byte-level
// response parser — mirrors tests/lib/test_socket.gd.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>One parsed HTTP response: status, headers (lowercased), raw body bytes.</summary>
internal sealed class HttpResp {
    public int Status;
    public Dictionary<string, string> Headers = new Dictionary<string, string>();
    public byte[] Body = new byte[0];

    public string Header(string name) {
        string v;
        return Headers.TryGetValue(name.ToLowerInvariant(), out v) ? v : null;
    }

    public string BodyText() {
        return Encoding.UTF8.GetString(Body);
    }
}

/// <summary>A raw HTTP/1.1 client that can pipeline and split requests across writes.</summary>
internal sealed class TestHttp : IDisposable {
    public Socket Sock;
    private byte[] _buf = new byte[0];
    public bool Closed;

    // Sent bytes not yet scanned for complete request heads; methods queue in wire order so
    // responses (which arrive in order) can be paired with HEAD → no body.
    private byte[] _sent = new byte[0];
    private readonly Queue<string> _methods = new Queue<string>();

    public TestHttp(int port) {
        Sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Sock.Connect(IPAddress.Loopback, port);
        Sock.NoDelay = true;
    }

    public void Send(string s) {
        var b = Encoding.Latin1.GetBytes(s);
        Sock.Send(b);
        NoteSent(b);
    }

    public void Send(byte[] b) {
        Sock.Send(b);
        NoteSent(b);
    }

    // Extracts complete request heads from the send stream (handles split sends and skips bodies).
    private void NoteSent(byte[] b) {
        var m = new byte[_sent.Length + b.Length];
        Buffer.BlockCopy(_sent, 0, m, 0, _sent.Length);
        Buffer.BlockCopy(b, 0, m, _sent.Length, b.Length);
        _sent = m;
        while (true) {
            int headEnd = IndexOf(_sent, 0, _sent.Length, new byte[] { 13, 10, 13, 10 });
            if (headEnd < 0) return;
            string head = Encoding.Latin1.GetString(_sent, 0, headEnd);
            int bodyStart = headEnd + 4;
            long bodyLen = 0;
            foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                    long.TryParse(line.Substring(15).Trim(), out bodyLen);
                }
            }
            if (_sent.Length < bodyStart + bodyLen) {
                return; // body bytes not all sent yet — wait for more
            }
            int sp = head.IndexOf(' ');
            _methods.Enqueue(sp > 0 ? head.Substring(0, sp) : head);
            Drop(ref _sent, bodyStart + (int)bodyLen);
        }
    }

    /// <summary>Reads until at least one more byte arrives; returns false on close/timeout.</summary>
    public bool Pump(int timeoutMs = 8000) {
        if (Closed) return false;
        try {
            Sock.ReceiveTimeout = timeoutMs;
            var b = new byte[262144];
            int n = Sock.Receive(b);
            if (n <= 0) {
                Closed = true;
                return false;
            }
            var nb = new byte[n];
            Buffer.BlockCopy(b, 0, nb, 0, n);
            var m = new byte[_buf.Length + n];
            Buffer.BlockCopy(_buf, 0, m, 0, _buf.Length);
            Buffer.BlockCopy(nb, 0, m, _buf.Length, n);
            _buf = m;
            return true;
        } catch (Exception) {
            Closed = true;
            return false;
        }
    }

    /// <summary>Waits until the peer closes the socket.</summary>
    public bool WaitClosed(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!Closed && Environment.TickCount64 < deadline) {
            Pump(Math.Max(50, (int)(deadline - Environment.TickCount64)));
        }
        return Closed;
    }

    /// <summary>Parses one response from the buffered bytes; returns null while incomplete.</summary>
    public HttpResp TryParse() {
        int headEnd = IndexOf(_buf, 0, _buf.Length, new byte[] { 13, 10, 13, 10 });
        if (headEnd < 0) return null;
        string head = Encoding.Latin1.GetString(_buf, 0, headEnd + 4);
        var r = new HttpResp();
        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2 || !int.TryParse(parts[1], out r.Status)) {
            // Unparsable head — consume it so callers can move on.
            Drop(headEnd + 4);
            r.Status = -1;
            return r;
        }
        for (int i = 1; i < lines.Length; i++) {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            r.Headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
        }
        long bodyLen = 0;
        string cl = r.Header("content-length");
        if (cl != null) {
            long v;
            bodyLen = long.TryParse(cl, out v) ? v : 0;
        }
        string method = _methods.Count > 0 ? _methods.Peek() : "";
        if (r.Status == 101 || r.Status == 204 || r.Status == 304 || method == "HEAD") bodyLen = 0;
        int bodyStart = headEnd + 4;
        if (_buf.Length < bodyStart + bodyLen) return null;
        if (_methods.Count > 0) _methods.Dequeue(); // commit only once the response is complete
        r.Body = new byte[bodyLen];
        Buffer.BlockCopy(_buf, bodyStart, r.Body, 0, (int)bodyLen);
        Drop(bodyStart + (int)bodyLen);
        return r;
    }

    /// <summary>Reads the next complete response.</summary>
    public HttpResp Recv(int timeoutMs = 8000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            var r = TryParse();
            if (r != null) return r;
            Pump(Math.Max(50, (int)(deadline - Environment.TickCount64)));
        }
        return TryParse() ?? new HttpResp { Status = -1 };
    }

    /// <summary>Sends a request and reads its response.</summary>
    public HttpResp Exchange(string request, int timeoutMs = 8000) {
        Send(request);
        return Recv(timeoutMs);
    }

    private void Drop(int n) {
        Drop(ref _buf, n);
    }

    private static void Drop(ref byte[] buf, int n) {
        var d = new byte[buf.Length - n];
        Buffer.BlockCopy(buf, n, d, 0, d.Length);
        buf = d;
    }

    private static int IndexOf(byte[] hay, int off, int len, byte[] needle) {
        for (int i = off; i + needle.Length <= len; i++) {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    public void Dispose() {
        try { Sock?.Dispose(); } catch (Exception) { }
    }
}
