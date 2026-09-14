using System;
using System.Collections.Generic;

namespace Splatter.Pmc
{
    /// <summary>
    /// Live registry of enabled <see cref="PmcHost"/> components — play mode and edit mode alike.
    /// The Phone Controllers dock reads <see cref="All"/> to show live status without a debugger
    /// channel; games can use it to find "the" host without a serialized reference.
    ///
    /// Hosts register in OnEnable and unregister in OnDisable/OnDestroy. <see cref="All"/> is a
    /// live view: don't hold it across frames, and copy it before iterating if you might change
    /// scene objects during the loop.
    /// </summary>
    public static class PmcLiveHosts
    {
        private static readonly List<PmcHost> Hosts = new List<PmcHost>();

        /// <summary>Every currently-enabled PmcHost, in registration order. Live view — see class docs.</summary>
        public static IReadOnlyList<PmcHost> All
        {
            get { return Hosts; }
        }

        /// <summary>Fired whenever a host registers or unregisters (add/remove).</summary>
        public static event Action Changed;

        internal static void Register(PmcHost host)
        {
            if (host == null || Hosts.Contains(host))
                return;
            Hosts.Add(host);
            Action c = Changed;
            if (c != null)
                c.Invoke();
        }

        internal static void Unregister(PmcHost host)
        {
            if (host == null || !Hosts.Remove(host))
                return;
            Action c = Changed;
            if (c != null)
                c.Invoke();
        }
    }
}
