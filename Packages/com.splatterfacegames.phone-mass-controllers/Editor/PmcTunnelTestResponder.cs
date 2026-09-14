using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Splatter.Pmc.Editor {

    /// <summary>
    /// Tiny HTTP responder used by the dock's "Test tunnel" button — serves a
    /// static page on 127.0.0.1 so a phone can prove the tunnel path works
    /// without a running game. Port of the Godot addon's test_responder.gd:
    /// GET /pmc/healthz answers "ok", every other path gets a small HTML page,
    /// one request per connection (Connection: close). A background thread does
    /// accept/read/write so nothing needs pumping. Editor-only.
    /// </summary>
    public sealed class PmcTunnelTestResponder : IDisposable {
        const int MaxHeaderBytes = 16384;
        const int ReadTimeoutMs = 10000;

        /// <summary>The bound port, 0 when closed.</summary>
        public int Port { get; private set; }
        /// <summary>Page title shown at "/".</summary>
        public string PageTitle = "Phone Mass Controllers: tunnel test";
        /// <summary>Fired (on the accept thread) for every request served, with the path.</summary>
        public event Action<string> RequestServed;

        TcpListener _listener;
        Thread _thread;

        /// <summary>Listens on 127.0.0.1, trying <paramref name="port"/> then the next
        /// <paramref name="search"/> ports. Returns the bound port, or 0.</summary>
        public int Listen(int port = 8090, int search = 20) {
            Close();
            for (int p = port; p <= port + search; p++) {
                TcpListener l;
                try {
                    l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                } catch (SocketException) {
                    continue;
                }
                _listener = l;
                Port = p;
                _thread = new Thread(ServeLoop) { IsBackground = true, Name = "PmcTunnelTestResponder" };
                _thread.Start(l);
                return p;
            }
            return 0;
        }

        /// <summary>Stops listening and drops the accept thread.</summary>
        public void Close() {
            Port = 0;
            var l = _listener;
            _listener = null;
            if (l != null) {
                try { l.Stop(); } catch (Exception) { /* closing races the accept thread */ }
            }
            var t = _thread;
            _thread = null;
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
                t.Join(2000);
        }

        public void Dispose() {
            Close();
        }

        void ServeLoop(object arg) {
            var l = (TcpListener)arg;
            while (ReferenceEquals(_listener, l)) {
                TcpClient client;
                try {
                    client = l.AcceptTcpClient();
                } catch (Exception) {
                    break; // stopped / disposed
                }
                try {
                    Serve(client);
                } catch (Exception) {
                    // a test page must never take the editor down with it
                } finally {
                    try { client.Close(); } catch (Exception) { }
                }
            }
        }

        void Serve(TcpClient client) {
            var s = client.GetStream();
            s.ReadTimeout = ReadTimeoutMs;
            var buf = new byte[MaxHeaderBytes];
            int used = 0, end = -1;
            while (used < buf.Length) {
                int n;
                try { n = s.Read(buf, used, buf.Length - used); }
                catch (IOException) { return; }
                if (n <= 0) return;
                used += n;
                end = HeaderEnd(buf, used);
                if (end >= 0) break;
            }
            if (end < 0) return;

            var head = Encoding.ASCII.GetString(buf, 0, end);
            var requestLine = head.Split('\n')[0].TrimEnd('\r');
            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var path = parts.Length > 1 ? parts[1] : "/";
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            byte[] body;
            string ctype;
            if (path == "/pmc/healthz") {
                body = Encoding.UTF8.GetBytes("ok");
                ctype = "text/plain; charset=utf-8";
            } else {
                body = Encoding.UTF8.GetBytes(
                    "<!doctype html><meta name=viewport content='width=device-width'><title>" +
                    PageTitle + "</title><h1>" + PageTitle +
                    "</h1><p>If you can read this on your phone, the tunnel works.</p>");
                ctype = "text/html; charset=utf-8";
            }
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: " + ctype + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
            s.Write(header, 0, header.Length);
            if (method != "HEAD")
                s.Write(body, 0, body.Length);
            RequestServed?.Invoke(path);
        }

        static int HeaderEnd(byte[] buf, int used) {
            for (int i = 3; i < used; i++)
                if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                    return i - 3;
            return -1;
        }
    }
}
