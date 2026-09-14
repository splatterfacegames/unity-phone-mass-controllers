using Newtonsoft.Json.Linq;

namespace Splatter.Pmc {
    /// <summary>
    /// A connected (or reconnecting) phone controller, owned by a <see cref="PmcHostCore"/>.
    /// The same instance survives reconnects within <see cref="PmcHostCore.GraceSeconds"/>, so keep
    /// game state in <see cref="Meta"/> or in your own dictionary keyed by <see cref="Id"/>.
    /// Port of player.gd.
    /// </summary>
    public sealed class PmcPlayer {
        /// <summary>Stable player id (1, 2, 3, ...). It isn't reused by another player during the
        /// host's lifetime.</summary>
        public int Id;
        /// <summary>Secret rejoin token (128-bit hex). Don't show it to other players.</summary>
        public string Token = "";
        /// <summary>Display name (trimmed, at most 32 characters).</summary>
        public string Name = "";
        /// <summary>Free-form profile sent by the controller (e.g. avatar, colour).</summary>
        public JObject Profile = new JObject();
        /// <summary>Whether a socket is currently attached.</summary>
        public bool Connected;
        /// <summary>Whether the player authenticated with <see cref="PmcHostCore.AdminPin"/>.</summary>
        public bool IsAdmin;
        /// <summary>Game-owned data. Preserved across rejoins and restored from tombstones.</summary>
        public JObject Meta = new JObject();
        /// <summary>Host clock (ticks ms) when the player first joined.</summary>
        public long JoinedMs;
        /// <summary>Host clock (ticks ms) of the last message (or disconnect).</summary>
        public long LastSeenMs;
        /// <summary>When disconnected: the host clock at which the player is removed with reason
        /// "timeout". 0 while connected.</summary>
        public long GraceDeadlineMs;
        /// <summary>Remote IP of the current (or last) socket. Behind a tunnel this is the tunnel's
        /// local address.</summary>
        public string RemoteAddress = "";
        /// <summary>Rolling average of the WebSocket ping→pong round trip, in milliseconds. 0 until
        /// the first heartbeat ping is answered.</summary>
        public float RttMs;

        /// <summary>Internal: the attached <see cref="PmcConnection"/>, or null.</summary>
        internal PmcConnection Conn;

        /// <inheritdoc/>
        public override string ToString() {
            return "PmcPlayer(" + Id + ", " + Name + (Connected ? "" : ", disconnected") + ")";
        }
    }
}
