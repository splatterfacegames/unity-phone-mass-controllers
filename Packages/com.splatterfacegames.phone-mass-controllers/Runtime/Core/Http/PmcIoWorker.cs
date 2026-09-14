using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Splatter.Pmc {
    /// <summary>
    /// Socket-I/O thread for <see cref="PmcHostCore"/>. Port of io_worker.gd.
    ///
    /// The worker owns accept, read, write and HTTP/WS frame decode for the connections it is handed
    /// via <see cref="AddConn"/>. Complete events go to <see cref="Events"/> under <see cref="Mutex"/>;
    /// the main thread drains them in <see cref="PmcHostCore.Poll"/> and runs all game logic there.
    /// Per-connection output crosses the other way through <see cref="PmcConnection.IoMutex"/>.
    /// Closed sockets are reported implicitly: the worker drops the connection and the host reaps it.
    /// </summary>
    internal sealed class PmcIoWorker {
        /// <summary>Complete events for the main thread (guarded by <see cref="Mutex"/>).</summary>
        internal readonly List<PmcIoEvent> Events = new List<PmcIoEvent>();
        /// <summary>Connections to start servicing (guarded by <see cref="Mutex"/>).</summary>
        internal readonly List<PmcConnection> Cmds = new List<PmcConnection>();
        /// <summary>Guards <see cref="Events"/>, <see cref="Cmds"/>, <see cref="Stop"/>,
        /// <see cref="BytesIn"/> and <see cref="BytesOut"/>.</summary>
        internal readonly object Mutex = new object();
        /// <summary>Set under <see cref="Mutex"/> to end <see cref="Run"/>.</summary>
        internal bool Stop;
        /// <summary>The listening socket. Written before the thread starts; read by the worker only.</summary>
        internal TcpListener Server;
        /// <summary>Bytes read since the last drain (guarded by <see cref="Mutex"/>).</summary>
        internal long BytesIn;
        /// <summary>Bytes written since the last drain (guarded by <see cref="Mutex"/>).</summary>
        internal long BytesOut;

        // Snapshots of the host limits, taken at Start — tune them before the thread runs.
        internal int MaxHeaderBytes = 16384;
        internal int MaxBodyBytes = 65536;
        internal int MaxMessageBytes = 1 << 20;

        private const int AcceptCap = 64;
        private const int HttpReqCap = 16;
        private const int WsEvCap = 256;
        private const int ReadCap = 262144;
        private const int WriteCap = 1 << 20;
        private const int StatusEveryFrames = 15;

        private readonly List<PmcConnection> _conns = new List<PmcConnection>();
        private readonly List<PmcIoEvent> _pending = new List<PmcIoEvent>();
        private long _bi;
        private long _bo;

        /// <summary>Host-side: hands an accepted connection to the worker.</summary>
        internal void AddConn(PmcConnection c) {
            lock (Mutex) {
                Cmds.Add(c);
            }
        }

        /// <summary>The thread body. Loops until <see cref="Stop"/> is set.</summary>
        internal void Run() {
            int frame = 0;
            while (true) {
                List<PmcConnection> newConns;
                bool done;
                lock (Mutex) {
                    done = Stop;
                    newConns = new List<PmcConnection>(Cmds);
                    Cmds.Clear();
                }
                if (done) return;
                foreach (var c in newConns) {
                    _conns.Add(c);
                }

                bool did = false;
                long now = PmcTime.NowMsec();
                frame += 1;
                int accepted = 0;
                while (Server != null && accepted < AcceptCap) {
                    Socket peer;
                    try {
                        if (!Server.Pending()) break;
                        peer = Server.AcceptSocket();
                    } catch (Exception) {
                        break;
                    }
                    accepted += 1;
                    did = true;
                    _pending.Add(PmcIoEvent.Accept(peer));
                }

                int i = 0;
                while (i < _conns.Count) {
                    var c = _conns[i];
                    if (c.Mode == PmcConnection.ConnMode.Closed) {
                        _conns.RemoveAt(i);
                        continue;
                    }
                    if (Service(c, now, frame)) {
                        did = true;
                    }
                    if (c.Mode == PmcConnection.ConnMode.Closed) {
                        _conns.RemoveAt(i);
                    } else {
                        i += 1;
                    }
                }

                lock (Mutex) {
                    Events.AddRange(_pending);
                    _pending.Clear();
                    BytesIn += _bi;
                    BytesOut += _bo;
                }
                _bi = 0;
                _bo = 0;
                if (!did) {
                    System.Threading.Thread.Sleep(1);
                }
            }
        }

        // Returns true when the connection did work worth another pass without sleeping.
        private bool Service(PmcConnection c, long now, int frame) {
            bool did = false;
            string closeReason;
            bool wantClose;
            lock (c.IoMutex) {
                wantClose = c.CloseRequested;
                closeReason = c.CloseReason;
                if (c.UpgradePending) {
                    c.ApplyUpgrade();
                }
            }
            if (wantClose) {
                c.CloseNow(closeReason);
                return true;
            }
            // The status check is a select() call, so it's staggered like the main-thread loop.
            if ((frame + c.Slot) % StatusEveryFrames == 0) {
                if (!c.PollStatus()) {
                    return true;
                }
            }
            int avail;
            try {
                avail = c.Peer.Available;
            } catch (Exception) {
                if (!c.PollStatus()) {
                    return true;
                }
                avail = 0;
            }
            if (avail < 0) {
                if (!c.PollStatus()) {
                    return true;
                }
                avail = 0;
            }
            if (c.Mode == PmcConnection.ConnMode.Http) {
                if (avail > 0 && !c.CloseAfterFlush && c.BufferedIn() < MaxHeaderBytes + MaxBodyBytes + 8) {
                    int n = c.Read(ReadCap, now);
                    _bi += n;
                    did = did || n > 0;
                }
                int handled = 0;
                while (c.Mode == PmcConnection.ConnMode.Http && !c.CloseAfterFlush && !c.UpgradePending
                        && !c.IsStreaming() && c.OutPending() < WriteCap && handled < HttpReqCap) {
                    var r = c.NextHttpRequest(MaxHeaderBytes, MaxBodyBytes);
                    if (r == HttpNextResult.Incomplete) break;
                    handled += 1;
                    did = true;
                    if (r.IsError) {
                        _pending.Add(PmcIoEvent.HttpError(c, r.ErrorStatus, r.ErrorReason));
                        break;
                    }
                    _pending.Add(PmcIoEvent.Http(c, r.Request));
                }
                c.IoPartialIn = c.BufferedIn() > 0 || c.AwaitingBody();
                if (handled >= HttpReqCap) {
                    did = true;
                }
            } else if (c.Mode == PmcConnection.ConnMode.Ws) {
                if (avail > 0 && c.Ws.Buffered() < MaxMessageBytes + 16) {
                    int n = c.Read(ReadCap, now);
                    _bi += n;
                    did = did || n > 0;
                }
                int count = 0;
                while (c.IsOpen()) {
                    if (count >= WsEvCap) {
                        did = true;
                        break;
                    }
                    var ev = c.Ws.Next();
                    if (ev == null) break;
                    count += 1;
                    did = true;
                    _pending.Add(PmcIoEvent.Ws(c, ev));
                }
            }
            int nOut = c.Flush(WriteCap);
            _bo += nOut;
            return did || nOut > 0;
        }
    }

    /// <summary>One worker → host event (accept, http, http_error, ws).</summary>
    internal sealed class PmcIoEvent {
        internal const int KindAccept = 1;
        internal const int KindHttp = 2;
        internal const int KindHttpError = 3;
        internal const int KindWs = 4;

        internal int Kind;
        /// <summary>KindAccept: the newly accepted socket.</summary>
        internal Socket Peer;
        /// <summary>KindHttp/HttpError/Ws: the connection.</summary>
        internal PmcConnection Conn;
        /// <summary>KindHttp: the parsed request.</summary>
        internal PmcHttpRequest Request;
        /// <summary>KindHttpError: status + reason.</summary>
        internal int Status;
        internal string Reason = "";
        /// <summary>KindWs: the decoded event.</summary>
        internal PmcWsEvent Ev;

        internal static PmcIoEvent Accept(Socket peer) {
            return new PmcIoEvent { Kind = KindAccept, Peer = peer };
        }

        internal static PmcIoEvent Http(PmcConnection c, PmcHttpRequest r) {
            return new PmcIoEvent { Kind = KindHttp, Conn = c, Request = r };
        }

        internal static PmcIoEvent HttpError(PmcConnection c, int status, string reason) {
            return new PmcIoEvent { Kind = KindHttpError, Conn = c, Status = status, Reason = reason };
        }

        internal static PmcIoEvent Ws(PmcConnection c, PmcWsEvent ev) {
            return new PmcIoEvent { Kind = KindWs, Conn = c, Ev = ev };
        }
    }
}
