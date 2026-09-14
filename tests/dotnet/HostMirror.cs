// Test-side reflection driver for host/tunnel internals. Lets NUnit inject a fake PmcTunnel
// into _tunnel/_tunnelPending and feed it state changes through the same queued handlers the
// real implementation uses (OnTunnelStateMain/OnTunnelStatePending) — the way the Godot tests
// drive FakeTunnel via host._tunnel + state_changed.emit(). Works on any PmcTunnel that stores
// State/Url in backing fields or private fields; falls back through a few name shapes.

using System;
using System.Reflection;
using Splatter.Pmc;

internal static class HostMirror {
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo TunnelF = typeof(PmcHostCore).GetField("_tunnel", F);
    private static readonly FieldInfo TunnelPortF = typeof(PmcHostCore).GetField("_tunnelLocalPort", F);
    private static readonly FieldInfo PendingF = typeof(PmcHostCore).GetField("_tunnelPending", F);
    private static readonly FieldInfo PendingPortF = typeof(PmcHostCore).GetField("_tunnelPendingLocalPort", F);
    private static readonly MethodInfo OnMain = typeof(PmcHostCore).GetMethod("OnTunnelStateMain", F);
    private static readonly MethodInfo OnPending = typeof(PmcHostCore).GetMethod("OnTunnelStatePending", F);

    /// <summary>Injects a fake tunnel as the active one and wires its StateChanged event to the
    /// host's queued handler, like AdoptDetached does.</summary>
    public static void SetTunnel(PmcHostCore host, PmcTunnel t) {
        TunnelF.SetValue(host, t);
        TunnelPortF.SetValue(host, host.BoundPort);
        if (t != null) {
            t.StateChanged += (Action<string, string>)Delegate.CreateDelegate(
                typeof(Action<string, string>), host, OnMain);
        }
    }

    /// <summary>Injects a fake tunnel as the rolling-restart pending one.</summary>
    public static void SetPendingTunnel(PmcHostCore host, PmcTunnel t) {
        PendingF.SetValue(host, t);
        PendingPortF.SetValue(host, host.BoundPort);
        if (t != null) {
            t.StateChanged += (Action<string, string>)Delegate.CreateDelegate(
                typeof(Action<string, string>), host, OnPending);
        }
    }

    /// <summary>Sets the tunnel's advertised state and url and fires its StateChanged event (the
    /// event queues onto the host; the host applies it on its next Poll).</summary>
    public static void Emit(PmcTunnel t, string state, string url = null) {
        SetMember(t, "State", state);
        if (url != null) SetMember(t, "Url", url);
        var ev = t.GetType().GetField("StateChanged", F);
        var d = ev != null ? ev.GetValue(t) as Action<string, string> : null;
        if (d != null) d(state, url ?? "");
    }

    /// <summary>Sets one of the tunnel seam internals (LocalPort, IsProcessAlive, Detached).</summary>
    public static void SetSeam(PmcTunnel t, string name, object value) {
        SetMember(t, name, value);
    }

    private static void SetMember(object o, string name, object value) {
        var t = o.GetType();
        string lower = char.ToLowerInvariant(name[0]) + name.Substring(1);
        foreach (string n in new[] { "<" + name + ">k__BackingField", name, "_" + lower, lower }) {
            var f = t.GetField(n, F);
            if (f != null) {
                f.SetValue(o, value);
                return;
            }
            var p = t.GetProperty(n, F | BindingFlags.Public);
            if (p != null && p.CanWrite) {
                p.SetValue(o, value, null);
                return;
            }
        }
        throw new MissingMemberException(t.FullName + " has no settable member for " + name);
    }
}
