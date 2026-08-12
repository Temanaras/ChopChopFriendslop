using System;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.Tugboat;

namespace ChopChop.Networking
{
    public enum TransportMode : byte
    {
        /// <summary>Steam P2P via FishyFacepunch. Ship configuration.</summary>
        Steam = 0,

        /// <summary>Plain UDP via Tugboat. Required for local multi-instance testing.</summary>
        Local = 1,
    }

    /// <summary>
    /// Multipass is in the stack so the transport can be swapped without touching
    /// scenes. This is not optional convenience: FishyFacepunch cannot connect to
    /// itself over Steam P2P, so running several editor instances on one machine
    /// has to happen over Tugboat (TECH 8.1).
    /// </summary>
    public sealed class TransportRouter
    {
        private readonly Multipass _multipass;

        public TransportRouter(Multipass multipass)
            => _multipass = multipass ? multipass : throw new ArgumentNullException(nameof(multipass));

        public TransportMode Mode { get; private set; } = TransportMode.Steam;

        /// <summary>
        /// Index within Multipass of the transport the client is set to use.
        ///
        /// Starting a server starts <em>every</em> transport Multipass holds, and each
        /// reports its own connection state, so "a server started" on its own says
        /// nothing about the one we actually care about.
        /// </summary>
        public int ActiveTransportIndex => _multipass.ClientTransport.Index;

        public void Use(TransportMode mode)
        {
            Mode = mode;

            if (mode == TransportMode.Steam)
                _multipass.SetClientTransport<FishyFacepunch.FishyFacepunch>();
            else
                _multipass.SetClientTransport<Tugboat>();
        }

        /// <summary>Host SteamID64 when on Steam; an IP address when on Tugboat.</summary>
        public void SetClientAddress(string address) => _multipass.SetClientAddress(address);

        public void SetPort(ushort port) => _multipass.SetPort(port);
    }
}
