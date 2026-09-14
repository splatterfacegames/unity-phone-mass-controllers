using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Splatter.Pmc {
    /// <summary>
    /// One accepted TCP connection: HTTP/1.1 (keep-alive), or WebSocket after an upgrade.
    /// Internal to <see cref="PmcHostCore"/>. Port of connection.gd.
    ///
    /// Keeps non-blocking read/write buffers and streams file bodies in chunks. The I/O worker thread
    /// services the socket; the host thread applies complete events. Fields that cross the boundary
    /// are guarded by <see cref="IoMutex"/> or accessed atomically — everything else is owned by
    /// whichever side services the socket.
    /// </summary>
    internal sealed class PmcConnection {
        /// <summary>Connection mode.</summary>
        internal enum ConnMode { Http = 0, Ws = 1, Closed = 2 }

        /// <summary>Largest single socket write / file chunk.</summary>
        internal const int Chunk = 65536;

        private static readonly byte[] Empty = new byte[0];

        /// <summary>The socket.</summary>
        internal readonly Socket Peer;
        private volatile int _mode = (int)ConnMode.Http;
        /// <summary>Peer IP.</summary>
        internal readonly string RemoteAddress;
        /// <summary>Peer port.</summary>
        internal readonly int RemotePort;
        /// <summary>Accept time (ticks ms).</summary>
        internal readonly long AcceptedMsec;
        /// <summary>Last time bytes arrived.</summary>
        internal long LastRxMsec;                        // worker-owned
        private long _lastTxMsec;                        // under IoMutex
        /// <summary>Close the socket once all queued output is written (under IoMutex; also read by
        /// the worker outside the lock as a gate).</summary>
        internal volatile bool CloseAfterFlush;
        /// <summary>Why the connection closed (diagnostics; under IoMutex when crossing threads).</summary>
        internal string CloseReason = "";

        // HTTP
        /// <summary>When the host started waiting for the current request head.</summary>
        internal long HeadStartedMsec;                   // host-owned
        private PmcHttpRequest _pendingReq;              // worker-owned
        private long _pendingBodyLen;                    // worker-owned

        // WebSocket
        /// <summary>Frame decoder (WS mode only; worker-owned).</summary>
        internal PmcWsDecoder Ws;
        /// <summary>Attached player id (0 = none, e.g. before hello).</summary>
        internal int PlayerId;                           // host-owned
        private long _helloDeadlineMsec;                 // Interlocked (worker writes, host reads)
        /// <summary>Heartbeat pings sent without any inbound frame since.</summary>
        internal int PingsUnanswered;                    // host-owned
        /// <summary>When the last heartbeat ping was queued (ticks ms; 0 = none in flight).</summary>
        internal long PingSentMsec;                      // host-owned
        private long _nextPingMsec;                      // Interlocked (worker writes, host r/w)
        /// <summary>Whether we've sent a close frame.</summary>
        internal bool CloseSent;                         // host-owned
        /// <summary>When to drop the socket if the peer doesn't finish the close handshake.</summary>
        internal long CloseDeadlineMsec;                 // host-owned
        /// <summary>Failed admin auth attempts on this connection.</summary>
        internal int AuthFailures;                       // host-owned
        /// <summary>Auth lockout end (ticks ms).</summary>
        internal long AuthLockedUntilMsec;               // host-owned
        /// <summary>A pmc.reject was sent. Later frames are ignored.</summary>
        internal bool Rejected;                          // host-owned
        /// <summary>Stagger slot for periodic status checks.</summary>
        internal int Slot;                               // host-owned
        /// <summary>Client address used for per-address limits: the peer IP, or CF-Connecting-IP
        /// behind the tunnel.</summary>
        internal string ClientAddress;                   // host-owned
        /// <summary>Address this connection is counted under in the host's per-address table
        /// ("" if not counted).</summary>
        internal string CountedAddress = "";             // host-owned

        /// <summary>Guards the fields that cross the thread boundary: the output queue, the streamed
        /// file, <see cref="CloseRequested"/>, <see cref="UpgradePending"/>, <see cref="CloseAfterFlush"/>,
        /// <see cref="CloseReason"/> and <see cref="Mode"/> transitions.</summary>
        internal readonly object IoMutex = new object();
        /// <summary>Worker: close the socket next pass (queued output is dropped, as with CloseNow).</summary>
        internal bool CloseRequested;
        /// <summary>Worker: apply <see cref="UpgradeToWs"/> next pass, using the _up_* parameters
        /// (written under IoMutex; also read by the worker outside the lock as a gate).</summary>
        internal volatile bool UpgradePending;
        /// <summary>Worker-maintained snapshot of "input partially received" for the host's HTTP
        /// header timeout.</summary>
        internal volatile bool IoPartialIn;
        /// <summary>A complete request was emitted to the host and its response has not been queued
        /// yet. The worker must not extract another request while this is set — responses are
        /// generated on the host thread, so without it a pipelined request's head could overtake a
        /// streamed file body (the main-thread loop's is_streaming() check can't see it).</summary>
        internal volatile bool HttpBusy;
        private int _upMaxMessageBytes;
        private long _upNowMsec;
        private int _upHelloTimeoutMsec;
        private long _upHeartbeatMsec;

        private byte[] _in = Empty;
        private int _inOff;
        private byte[] _out = Empty;
        private int _outOff;
        private Stream _file;
        private long _fileRemaining;

        internal PmcConnection(Socket peer, long nowMsec) {
            Peer = peer;
            peer.NoDelay = true;
            peer.Blocking = false;
            var ep = peer.RemoteEndPoint as IPEndPoint;
            RemoteAddress = ep != null ? NormalizeAddress(ep.Address) : "";
            RemotePort = ep != null ? ep.Port : 0;
            ClientAddress = RemoteAddress;
            AcceptedMsec = nowMsec;
            LastRxMsec = nowMsec;
            _lastTxMsec = nowMsec;
            HeadStartedMsec = nowMsec;
        }

        internal static string NormalizeAddress(System.Net.IPAddress ip) {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return ip.ToString();
        }

        /// <summary>Current mode.</summary>
        internal ConnMode Mode {
            get { return (ConnMode)_mode; }
            set { _mode = (int)value; }
        }

        /// <summary>Whether the socket is still usable.</summary>
        internal bool IsOpen() {
            return _mode != (int)ConnMode.Closed;
        }

        /// <summary>Updates the socket status. Returns false if the peer has gone away.</summary>
        internal bool PollStatus() {
            if (_mode == (int)ConnMode.Closed) return false;
            bool gone;
            try {
                gone = Peer.Poll(0, SelectMode.SelectError)
                    || (Peer.Poll(0, SelectMode.SelectRead) && Peer.Available == 0);
            } catch (Exception) {
                gone = true;
            }
            if (gone) {
                CloseNow("peer closed");
                return false;
            }
            return true;
        }

        /// <summary>Bytes queued but not yet written (excluding the rest of a streamed file).</summary>
        internal int OutPending() {
            lock (IoMutex) {
                return _out.Length - _outOff;
            }
        }

        /// <summary>Whether any output (bytes or a file) is still pending.</summary>
        internal bool HasPendingOutput() {
            lock (IoMutex) {
                return _out.Length > _outOff || _file != null;
            }
        }

        /// <summary>Whether a file body is being streamed.</summary>
        internal bool IsStreaming() {
            lock (IoMutex) {
                return _file != null;
            }
        }

        /// <summary>Bytes received but not yet consumed (worker-owned in WS mode).</summary>
        internal int BufferedIn() {
            if (_mode == (int)ConnMode.Ws && Ws != null) {
                return Ws.Buffered();
            }
            return _in.Length - _inOff;
        }

        /// <summary>Time of the last (attempted) write — for the host's stall check.</summary>
        internal long LastTxMsec() {
            lock (IoMutex) {
                return _lastTxMsec;
            }
        }

        /// <summary>Queues bytes for sending.</summary>
        internal void Queue(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) return;
            lock (IoMutex) {
                if (_mode == (int)ConnMode.Closed || CloseRequested) return;
                if (_outOff == _out.Length) {
                    _out = bytes;
                    _outOff = 0;
                    _lastTxMsec = PmcTime.NowMsec(); // output became pending
                } else {
                    var merged = new byte[_out.Length - _outOff + bytes.Length];
                    Buffer.BlockCopy(_out, _outOff, merged, 0, _out.Length - _outOff);
                    Buffer.BlockCopy(bytes, 0, merged, _out.Length - _outOff, bytes.Length);
                    _out = merged;
                }
            }
        }

        /// <summary>Streams <paramref name="length"/> bytes from the already-positioned
        /// <paramref name="file"/> after the queued bytes.</summary>
        internal void StartFile(Stream file, long length) {
            lock (IoMutex) {
                if (_file != null) {
                    _file.Dispose(); // unreachable while HttpBusy is honoured; avoids a leak
                }
                _file = file;
                _fileRemaining = length;
                _lastTxMsec = PmcTime.NowMsec();
            }
        }

        /// <summary>Ask the worker to close the socket next pass. Queued output is dropped, as with
        /// <see cref="CloseNow"/>.</summary>
        internal void RequestClose(string reason = "") {
            lock (IoMutex) {
                if (_mode != (int)ConnMode.Closed && !CloseRequested) {
                    CloseRequested = true;
                    if (CloseReason == "") CloseReason = reason;
                }
            }
        }

        /// <summary>Close once the queued output is written.</summary>
        internal void CloseWhenFlushed(string reason = "") {
            lock (IoMutex) {
                CloseAfterFlush = true;
                if (CloseReason == "") CloseReason = reason;
            }
        }

        /// <summary>Ask the worker to switch to WS mode next pass.</summary>
        internal void RequestUpgrade(int maxMessageBytes, long nowMsec, int helloTimeoutMsec, long heartbeatMsec) {
            lock (IoMutex) {
                UpgradePending = true;
                _upMaxMessageBytes = maxMessageBytes;
                _upNowMsec = nowMsec;
                _upHelloTimeoutMsec = helloTimeoutMsec;
                _upHeartbeatMsec = heartbeatMsec;
            }
        }

        /// <summary>Writes up to <paramref name="maxBytes"/>. Returns the bytes written.</summary>
        internal int Flush(int maxBytes) {
            lock (IoMutex) {
                return FlushLocked(maxBytes);
            }
        }

        private int FlushLocked(int maxBytes) {
            int written = 0;
            while (_mode != (int)ConnMode.Closed && written < maxBytes) {
                if (_outOff >= _out.Length) {
                    if (_file == null) break;
                    int n = (int)Math.Min(Chunk, _fileRemaining);
                    var chunk = new byte[n];
                    int got = _file.Read(chunk, 0, n);
                    if (got == 0 && n > 0) {
                        CloseNowLocked("file read error");
                        break;
                    }
                    _fileRemaining -= got;
                    if (_fileRemaining <= 0) {
                        _file.Dispose();
                        _file = null;
                    }
                    _out = got == chunk.Length ? chunk : Slice(chunk, 0, got);
                    _outOff = 0;
                }
                int pieceLen = Math.Min(_out.Length - _outOff, (int)Math.Min(Chunk, maxBytes - written));
                int sent = SendPiece(_out, _outOff, pieceLen);
                if (sent < 0) break; // hard error — already closed
                if (sent > 0) {
                    _lastTxMsec = PmcTime.NowMsec();
                }
                written += sent;
                _outOff += sent;
                if (_outOff >= _out.Length) {
                    _out = Empty;
                    _outOff = 0;
                } else if (_outOff > (1 << 20) && _outOff * 2 > _out.Length) {
                    _out = Slice(_out, _outOff, _out.Length - _outOff);
                    _outOff = 0;
                }
                if (sent < pieceLen) break;
            }
            if (_mode != (int)ConnMode.Closed && CloseAfterFlush && !(_out.Length > _outOff || _file != null)) {
                CloseNowLocked("done");
            }
            return written;
        }

        private int SendPiece(byte[] buf, int off, int len) {
            try {
                return Peer.Send(buf, off, len, SocketFlags.None);
            } catch (SocketException se) {
                if (se.SocketErrorCode == SocketError.WouldBlock
                    || se.SocketErrorCode == SocketError.IOPending) {
                    return 0;
                }
                CloseNowLocked("write error");
                return -1;
            } catch (Exception) {
                CloseNowLocked("write error");
                return -1;
            }
        }

        private static byte[] Slice(byte[] src, int off, int len) {
            var d = new byte[len];
            Buffer.BlockCopy(src, off, d, 0, len);
            return d;
        }

        /// <summary>Reads up to <paramref name="maxBytes"/> available bytes into the input (or WS
        /// decoder) buffer. Returns the bytes read.</summary>
        internal int Read(int maxBytes, long nowMsec) {
            if (_mode == (int)ConnMode.Closed || maxBytes <= 0) return 0;
            int avail;
            try {
                avail = Peer.Available;
            } catch (Exception) {
                CloseNow("read error");
                return 0;
            }
            if (avail <= 0) return 0;
            var data = new byte[Math.Min(avail, maxBytes)];
            int got;
            try {
                got = Peer.Receive(data, 0, data.Length, SocketFlags.None);
            } catch (SocketException se) {
                if (se.SocketErrorCode == SocketError.WouldBlock) return 0;
                CloseNow("read error");
                return 0;
            } catch (Exception) {
                CloseNow("read error");
                return 0;
            }
            if (got <= 0) {
                CloseNow("peer closed");
                return 0;
            }
            LastRxMsec = nowMsec;
            if (got != data.Length) data = Slice(data, 0, got);
            if (_mode == (int)ConnMode.Ws) {
                Ws.Push(data);
            } else if (_inOff == _in.Length) {
                _in = data;
                _inOff = 0;
            } else {
                var merged = new byte[_in.Length - _inOff + data.Length];
                Buffer.BlockCopy(_in, _inOff, merged, 0, _in.Length - _inOff);
                Buffer.BlockCopy(data, 0, merged, _in.Length - _inOff, data.Length);
                _in = merged;
            }
            return got;
        }

        /// <summary>
        /// Extracts the next complete HTTP request. Returns null if incomplete, a result with
        /// Request set, or one with Error status/reason.
        /// </summary>
        internal HttpNextResult NextHttpRequest(int maxHeaderBytes, int maxBodyBytes) {
            if (_pendingReq != null) {
                if (_in.Length - _inOff < _pendingBodyLen) return HttpNextResult.Incomplete;
                var pending = _pendingReq;
                pending.Body = Slice(_in, _inOff, (int)_pendingBodyLen);
                _inOff += (int)_pendingBodyLen;
                _pendingReq = null;
                _pendingBodyLen = 0;
                CompactIn();
                return HttpNextResult.Req(pending);
            }
            int avail = _in.Length - _inOff;
            if (avail == 0) return HttpNextResult.Incomplete;
            int headEnd = PmcHttpParser.FindHeadEnd(_in, _inOff, maxHeaderBytes + 4, out int bodyStart);
            if (headEnd < 0) {
                if (avail > maxHeaderBytes) {
                    return HttpNextResult.Fail(431, "request header too large");
                }
                return HttpNextResult.Incomplete;
            }
            if (headEnd - _inOff > maxHeaderBytes) {
                return HttpNextResult.Fail(431, "request header too large");
            }
            string head = PmcHttpParser.Latin1.GetString(_in, _inOff, headEnd - _inOff);
            _inOff = bodyStart;
            CompactIn();
            var res = PmcHttpParser.ParseHead(head);
            if (!res.IsOk) {
                return HttpNextResult.Fail(res.Status, res.Reason);
            }
            PmcHttpRequest req = res.Request;
            req.RemoteAddress = RemoteAddress;
            req.RemotePort = RemotePort;
            if (req.Headers.ContainsKey("transfer-encoding")) {
                return HttpNextResult.Fail(501, "chunked request bodies are not supported");
            }
            string cl = req.Header("content-length");
            if (cl != "") {
                bool digits = cl.Length <= 18;
                foreach (char ch in cl) {
                    if (ch < '0' || ch > '9') {
                        digits = false;
                        break;
                    }
                }
                if (!digits) {
                    return HttpNextResult.Fail(400, "bad Content-Length");
                }
                long n = long.Parse(cl);
                if (n > maxBodyBytes) {
                    return HttpNextResult.Fail(413, "request body too large");
                }
                if (n > 0) {
                    _pendingReq = req;
                    _pendingBodyLen = n;
                    return NextHttpRequest(maxHeaderBytes, maxBodyBytes);
                }
            }
            return HttpNextResult.Req(req);
        }

        /// <summary>Whether a request body is still being received.</summary>
        internal bool AwaitingBody() {
            return _pendingReq != null;
        }

        /// <summary>Switches to WebSocket mode. Bytes already buffered after the upgrade request go
        /// to the decoder. Called by the worker while holding <see cref="IoMutex"/> (or on the owning
        /// thread before the conn is handed over).</summary>
        internal void UpgradeToWs(int maxMessageBytes, long nowMsec, int helloTimeoutMsec, long heartbeatMsec) {
            _mode = (int)ConnMode.Ws;
            Ws = new PmcWsDecoder { MaxMessageBytes = maxMessageBytes };
            if (_in.Length > _inOff) {
                Ws.Push(Slice(_in, _inOff, _in.Length - _inOff));
            }
            _in = Empty;
            _inOff = 0;
            Interlocked.Exchange(ref _helloDeadlineMsec, nowMsec + helloTimeoutMsec);
            Interlocked.Exchange(ref _nextPingMsec, nowMsec + heartbeatMsec);
        }

        /// <summary>Applies a pending <see cref="RequestUpgrade"/>. Caller holds IoMutex.</summary>
        internal void ApplyUpgrade() {
            UpgradeToWs(_upMaxMessageBytes, _upNowMsec, _upHelloTimeoutMsec, _upHeartbeatMsec);
            UpgradePending = false;
        }

        internal long HelloDeadlineMsec {
            get { return Interlocked.Read(ref _helloDeadlineMsec); }
        }

        internal long NextPingMsec {
            get { return Interlocked.Read(ref _nextPingMsec); }
            set { Interlocked.Exchange(ref _nextPingMsec, value); }
        }

        /// <summary>Closes the socket immediately.</summary>
        internal void CloseNow(string reason = "") {
            lock (IoMutex) {
                CloseNowLocked(reason);
            }
        }

        private void CloseNowLocked(string reason = "") {
            if (_mode == (int)ConnMode.Closed) return;
            _mode = (int)ConnMode.Closed;
            if (CloseReason == "") CloseReason = reason;
            if (_file != null) {
                _file.Dispose();
                _file = null;
            }
            _out = Empty;
            _outOff = 0;
            _in = Empty;
            _inOff = 0;
            try {
                Peer.Close();
            } catch (Exception) {
                // already gone
            }
        }

        private void CompactIn() {
            if (_inOff >= _in.Length) {
                _in = Empty;
                _inOff = 0;
            } else if (_inOff > 32768 && _inOff * 2 > _in.Length) {
                _in = Slice(_in, _inOff, _in.Length - _inOff);
                _inOff = 0;
            }
        }
    }

    /// <summary>Result of <see cref="PmcConnection.NextHttpRequest"/>.</summary>
    internal sealed class HttpNextResult {
        internal static readonly HttpNextResult Incomplete = new HttpNextResult();
        internal PmcHttpRequest Request;
        internal int ErrorStatus;
        internal string ErrorReason = "";
        internal bool IsError;

        internal static HttpNextResult Req(PmcHttpRequest r) {
            return new HttpNextResult { Request = r };
        }

        internal static HttpNextResult Fail(int status, string reason) {
            return new HttpNextResult { IsError = true, ErrorStatus = status, ErrorReason = reason };
        }
    }

    /// <summary>Monotonic/epoch clocks used across the host (Godot Time.get_ticks_msec /
    /// get_unix_time_from_system equivalents).</summary>
    internal static class PmcTime {
        private static readonly long T0 = Environment.TickCount64;

        /// <summary>Milliseconds since an arbitrary epoch (monotonic tick).</summary>
        internal static long NowMsec() {
            return Environment.TickCount64;
        }

        /// <summary>Microseconds for poll timing.</summary>
        internal static long NowUsec() {
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000000 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Unix epoch milliseconds (UTC).</summary>
        internal static long EpochMs() {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
