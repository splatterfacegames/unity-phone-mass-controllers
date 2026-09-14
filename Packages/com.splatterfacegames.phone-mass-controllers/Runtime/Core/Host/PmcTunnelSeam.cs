using System;
using System.Reflection;

namespace Splatter.Pmc {
    /// <summary>
    /// Internal bridge from the host to tunnel internals that aren't part of the pinned public
    /// contract (<c>IsProcessAlive</c>, <c>LocalPort</c>, <c>Detached</c>). Resolved by reflection so
    /// the tunnel stream's PmcTunnel can replace the stub without a compile-time coupling; falls back
    /// to <see cref="PmcTunnel.State"/> when the members aren't provided.
    /// </summary>
    internal static class PmcTunnelSeam {
        private static PropertyInfo _aliveProp;      // IsProcessAlive | IsRunning
        private static MethodInfo _aliveMethod;      // IsProcessAlive() | IsRunning() | is_running()
        private static PropertyInfo _portProp;       // LocalPort | _port
        private static FieldInfo _portField;
        private static PropertyInfo _detachedProp;   // Detached | detached
        private static FieldInfo _detachedField;
        private static bool _probed;

        private static void Probe(Type t) {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _aliveProp = t.GetProperty("IsProcessAlive", F) ?? t.GetProperty("IsRunning", F);
            _aliveMethod = t.GetMethod("IsProcessAlive", F, null, Type.EmptyTypes, null)
                ?? t.GetMethod("IsRunning", F, null, Type.EmptyTypes, null)
                ?? t.GetMethod("is_running", F, null, Type.EmptyTypes, null);
            _portProp = t.GetProperty("LocalPort", F);
            if (_portProp == null || !_portProp.CanRead) _portProp = null;
            _portField = _portProp == null ? (t.GetField("_port", F) ?? t.GetField("LocalPort", F)) : null;
            _detachedProp = t.GetProperty("Detached", F);
            _detachedField = _detachedProp == null ? (t.GetField("Detached", F) ?? t.GetField("detached", F)) : null;
            _probed = true;
        }

        /// <summary>Whether the tunnel's child process is alive (may differ from State == "lost",
        /// where the connection dropped but the process can still re-register).</summary>
        internal static bool IsProcessAlive(PmcTunnel t) {
            if (t == null) return false;
            if (!_probed) Probe(t.GetType());
            try {
                if (_aliveProp != null) return (bool)_aliveProp.GetValue(t, null);
                if (_aliveMethod != null) return (bool)_aliveMethod.Invoke(t, null);
            } catch (Exception) {
                // fall through to the state approximation
            }
            string s = t.State;
            return s == "downloading" || s == "starting" || s == "ready" || s == "lost";
        }

        /// <summary>The local port the tunnel forwards to (-1 when unknown).</summary>
        internal static int LocalPort(PmcTunnel t) {
            if (t == null) return -1;
            if (!_probed) Probe(t.GetType());
            try {
                if (_portProp != null) return Convert.ToInt32(_portProp.GetValue(t, null));
                if (_portField != null) return Convert.ToInt32(_portField.GetValue(t));
            } catch (Exception) {
            }
            return -1;
        }

        /// <summary>Marks the tunnel as detached (its process must outlive the host object).</summary>
        internal static void SetDetached(PmcTunnel t, bool detached) {
            if (t == null) return;
            if (!_probed) Probe(t.GetType());
            try {
                if (_detachedProp != null && _detachedProp.CanWrite) {
                    _detachedProp.SetValue(t, detached, null);
                } else if (_detachedField != null) {
                    _detachedField.SetValue(t, detached);
                }
            } catch (Exception) {
            }
        }

        /// <summary>Whether the tunnel is flagged detached.</summary>
        internal static bool IsDetached(PmcTunnel t) {
            if (t == null) return false;
            if (!_probed) Probe(t.GetType());
            try {
                if (_detachedProp != null) return (bool)_detachedProp.GetValue(t, null);
                if (_detachedField != null) return (bool)_detachedField.GetValue(t);
            } catch (Exception) {
            }
            return false;
        }
    }
}
