// STUB — feat/tunnel replaces this file wholesale
using System;
using System.Collections.Generic;

namespace Splatter.Pmc {
    /// <summary>
    /// Compile stub for the tunnel worker's real implementation. Implements the
    /// API_CONTRACT surface; <see cref="Start"/> always returns an error.
    /// </summary>
    public sealed class PmcTunnel {
        /// <summary>"idle"|"downloading"|"starting"|"ready"|"lost"|"failed"|"stopped".</summary>
        public string State { get; private set; } = "idle";
        /// <summary>Public join base once ready.</summary>
        public string Url { get; private set; } = "";
        /// <summary>Last failure detail.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>Child-process log lines.</summary>
        public List<string> LogLines { get; } = new List<string>();
        /// <summary>State changes. Raised on whichever thread the implementation uses — the host
        /// marshals them onto its <see cref="PmcHostCore.Poll"/> thread.</summary>
        public event Action<string, string> StateChanged;

        /// <summary>resolve→(download)→launch; returns 0 on success, else an error code.</summary>
        public int Start(int localPort) {
            LastError = "PmcTunnel stub — feat/tunnel not merged";
            State = "failed";
            StateChanged?.Invoke("failed", LastError);
            return 1;
        }

        /// <summary>Stops the child process.</summary>
        public void Stop() {
            if (State != "stopped" && State != "idle" && State != "failed") {
                State = "stopped";
                StateChanged?.Invoke("stopped", "");
            }
        }

        /// <summary>Drains process-reader queues (the host calls it inside Poll).</summary>
        public void Pump() {
        }

        // Seam internals probed by PmcTunnelSeam (the real implementation provides equivalents):
        // the local port the tunnel forwards to, whether the child process is alive even while the
        // registration dropped ("lost"), and whether the process must outlive its host object.
        internal int LocalPort { get; set; } = -1;
        internal bool IsProcessAlive { get; set; }
        internal bool Detached { get; set; }

        // options — set before Start; mirrored from host exports
        public string Mode = "quick";
        public string NamedToken = "";
        public string NamedHostname = "";
        public string NamedName = "";
        public string NamedCredentialsFile = "";
        public string BinaryPath = "";
        public bool AllowDownload;
        public bool VerifyDns = true;
        public float ReadyTimeoutSec = 60f;
        public string[] ExtraArgs = new string[0];
        public int MaxRetries = 2;
        public float RetryBackoffSec = 4f;
        public float ProtocolFallbackSec = 0f;
        public float LostGraceSec = 0f;
        public int BinaryMaxAgeDays = 30;
        public string MinimumVersion = "";
    }
}
