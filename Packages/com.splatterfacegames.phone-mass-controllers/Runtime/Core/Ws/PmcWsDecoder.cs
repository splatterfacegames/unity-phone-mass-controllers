using System;
using System.Collections.Generic;
using System.Text;

namespace Splatter.Pmc {
    /// <summary>
    /// Incremental RFC 6455 frame decoder with message reassembly. Internal.
    /// Feed bytes with <see cref="Push"/>, then drain events with <see cref="Next"/> until it returns
    /// null. After a close or error event, the decoder stops producing events.
    /// Port of ws_decoder.gd.
    /// </summary>
    internal sealed class PmcWsDecoder {
        /// <summary>Largest reassembled message accepted. Larger ones produce error 1009.</summary>
        internal int MaxMessageBytes = 1 << 20;

        /// <summary>Reject unmasked frames (server side). Set false to decode server frames in test
        /// clients.</summary>
        internal bool RequireMask = true;

        private byte[] _buf = new byte[0];
        private int _off;
        private int _fragOp = -1;
        private readonly List<byte[]> _fragParts = new List<byte[]>();
        private int _fragSize;
        private bool _done;

        // Current frame (header parsed, payload arriving).
        private bool _fActive;
        private int _fOp;
        private bool _fFin;
        private long _fLen;
        private long _fGot;
        private byte[] _fKey = new byte[0];
        private byte[] _fPayload = new byte[0];

        /// <summary>Appends received bytes.</summary>
        internal void Push(byte[] data) {
            if (_done || data == null || data.Length == 0) return;
            if (_off == _buf.Length) {
                _buf = data;
                _off = 0;
            } else {
                var merged = new byte[_buf.Length - _off + data.Length];
                Buffer.BlockCopy(_buf, _off, merged, 0, _buf.Length - _off);
                Buffer.BlockCopy(data, 0, merged, _buf.Length - _off, data.Length);
                _buf = merged;
            }
        }

        /// <summary>Bytes buffered but not yet decoded.</summary>
        internal int Buffered() {
            return _buf.Length - _off;
        }

        /// <summary>Whether a close or error event was produced.</summary>
        internal bool IsDone() {
            return _done;
        }

        /// <summary>Decodes the next event, or returns null if more bytes are needed.</summary>
        internal PmcWsEvent Next() {
            while (!_done) {
                if (_fActive) {
                    // Accumulate (and unmask) whatever part of the payload has arrived, so large
                    // messages spread their unmasking cost over the frames in which the bytes arrive.
                    long take = Math.Min(_fLen - _fGot, _buf.Length - _off);
                    if (take > 0) {
                        byte[] chunk = Slice(_buf, _off, (int)take);
                        if (_fKey.Length == 4) {
                            chunk = PmcWsFrame.XorMask(chunk, _fKey, (int)_fGot);
                        }
                        if (_fGot == 0) {
                            _fPayload = chunk;
                        } else {
                            var merged = new byte[_fPayload.Length + chunk.Length];
                            Buffer.BlockCopy(_fPayload, 0, merged, 0, _fPayload.Length);
                            Buffer.BlockCopy(chunk, 0, merged, _fPayload.Length, chunk.Length);
                            _fPayload = merged;
                        }
                        _fGot += take;
                        _off += (int)take;
                        Compact();
                    }
                    if (_fGot < _fLen) return null;
                    _fActive = false;
                    var ev = Dispatch(_fOp, _fFin, _fPayload);
                    _fPayload = new byte[0];
                    if (ev != null) return ev;
                    continue;
                }
                int avail = _buf.Length - _off;
                if (avail < 2) return null;
                int b0 = _buf[_off];
                int b1 = _buf[_off + 1];
                bool fin = (b0 & 0x80) != 0;
                int op = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                int hdr = 2;
                if ((b0 & 0x70) != 0) {
                    return Fail(1002, "reserved bits set");
                }
                if ((op > 2 && op < 8) || op > 10) {
                    return Fail(1002, "unknown opcode");
                }
                if (RequireMask && !masked) {
                    return Fail(1002, "unmasked client frame");
                }
                if (len == 126) {
                    if (avail < 4) return null;
                    len = (_buf[_off + 2] << 8) | _buf[_off + 3];
                    hdr = 4;
                } else if (len == 127) {
                    if (avail < 10) return null;
                    if ((_buf[_off + 2] & 0x80) != 0) {
                        return Fail(1002, "bad payload length");
                    }
                    len = 0;
                    for (int i = 0; i < 8; i++) {
                        len = (len << 8) | _buf[_off + 2 + i];
                    }
                    hdr = 10;
                }
                bool control = op >= 8;
                if (control) {
                    if (!fin) return Fail(1002, "fragmented control frame");
                    if (len > 125) return Fail(1002, "control frame too long");
                } else {
                    if (op == 0 && _fragOp < 0) return Fail(1002, "unexpected continuation");
                    if (op != 0 && _fragOp >= 0) return Fail(1002, "expected continuation");
                    if (_fragSize + len > MaxMessageBytes) return Fail(1009, "message too big");
                }
                if (masked) hdr += 4;
                if (avail < hdr) return null;
                _fKey = masked ? Slice(_buf, _off + hdr - 4, 4) : new byte[0];
                _off += hdr;
                Compact();
                _fActive = true;
                _fOp = op;
                _fFin = fin;
                _fLen = len;
                _fGot = 0;
                _fPayload = new byte[0];
            }
            return null;
        }

        private PmcWsEvent Dispatch(int op, bool fin, byte[] payload) {
            switch (op) {
                case PmcWsFrame.OpPing:
                    return PmcWsEvent.Data(PmcWsEvent.EvPing, payload);
                case PmcWsFrame.OpPong:
                    return PmcWsEvent.Data(PmcWsEvent.EvPong, payload);
                case PmcWsFrame.OpClose:
                    return CloseEvent(payload);
            }
            if (!fin) {
                if (op != 0) _fragOp = op;
                _fragParts.Add(payload);
                _fragSize += payload.Length;
                return null;
            }
            int fullOp = op;
            if (op == 0) {
                fullOp = _fragOp;
                _fragParts.Add(payload);
                payload = Join(_fragParts);
                _fragParts.Clear();
                _fragSize = 0;
                _fragOp = -1;
            }
            if (fullOp == PmcWsFrame.OpText) {
                string s;
                try {
                    s = PmcHttpParser.StrictUtf8.GetString(payload);
                } catch (DecoderFallbackException) {
                    return Fail(1007, "invalid UTF-8");
                }
                return PmcWsEvent.TextEvent(s, payload);
            }
            return PmcWsEvent.Data(PmcWsEvent.EvBinary, payload);
        }

        private PmcWsEvent CloseEvent(byte[] payload) {
            _done = true;
            if (payload.Length == 0) {
                return PmcWsEvent.Close(1005, "");
            }
            if (payload.Length == 1) {
                return Fail(1002, "bad close payload");
            }
            int code = (payload[0] << 8) | payload[1];
            if (!PmcWsFrame.IsValidCloseCode(code)) {
                return Fail(1002, "bad close code");
            }
            string reason;
            try {
                reason = PmcHttpParser.StrictUtf8.GetString(payload, 2, payload.Length - 2);
            } catch (DecoderFallbackException) {
                return Fail(1007, "invalid UTF-8 in close reason");
            }
            return PmcWsEvent.Close(code, reason);
        }

        private PmcWsEvent Fail(int code, string reason) {
            _done = true;
            _buf = new byte[0];
            _off = 0;
            _fragParts.Clear();
            _fActive = false;
            _fPayload = new byte[0];
            return PmcWsEvent.Error(code, reason);
        }

        private void Compact() {
            if (_off == _buf.Length) {
                _buf = new byte[0];
                _off = 0;
            } else if (_off > 65536 && _off * 2 > _buf.Length) {
                _buf = Slice(_buf, _off, _buf.Length - _off);
                _off = 0;
            }
        }

        private static byte[] Slice(byte[] src, int off, int len) {
            var d = new byte[len];
            Buffer.BlockCopy(src, off, d, 0, len);
            return d;
        }

        private static byte[] Join(List<byte[]> parts) {
            if (parts.Count == 1) return parts[0];
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var outp = new byte[n];
            int o = 0;
            foreach (var p in parts) {
                Buffer.BlockCopy(p, 0, outp, o, p.Length);
                o += p.Length;
            }
            return outp;
        }
    }

    /// <summary>One decoded WebSocket event (text, binary, ping, pong, close, or error).</summary>
    internal sealed class PmcWsEvent {
        internal const int EvText = 1;
        internal const int EvBinary = 2;
        internal const int EvPing = 3;
        internal const int EvPong = 4;
        internal const int EvClose = 5;
        internal const int EvError = 6;

        internal int Type;
        /// <summary>Frame payload for ping/pong/binary; decoded payload bytes for text (echo path).</summary>
        internal byte[] Payload;
        /// <summary>Decoded text for <see cref="EvText"/>.</summary>
        internal string Text = "";
        /// <summary>Close/error status code.</summary>
        internal int Code;
        /// <summary>Close/error reason.</summary>
        internal string Reason = "";

        internal static PmcWsEvent Data(int type, byte[] payload) {
            return new PmcWsEvent { Type = type, Payload = payload };
        }

        internal static PmcWsEvent TextEvent(string s, byte[] payload) {
            return new PmcWsEvent { Type = EvText, Text = s, Payload = payload };
        }

        internal static PmcWsEvent Close(int code, string reason) {
            return new PmcWsEvent { Type = EvClose, Code = code, Reason = reason };
        }

        internal static PmcWsEvent Error(int code, string reason) {
            return new PmcWsEvent { Type = EvError, Code = code, Reason = reason };
        }
    }
}
