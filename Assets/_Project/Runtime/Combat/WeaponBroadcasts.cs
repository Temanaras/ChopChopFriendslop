using FishNet.Broadcast;
using UnityEngine;

namespace ChopChop.Combat
{
    /// <summary>
    /// A client reporting that it fired (TECH 10.2).
    ///
    /// The shot has already been drawn on that machine — muzzle flash, tracer, impact —
    /// before this was sent. The server decides whether any of it counted.
    /// </summary>
    public struct FireRequestBroadcast : IBroadcast
    {
        /// <summary>Where the client believes it fired from. Checked for plausibility, not trusted.</summary>
        public Vector3 Origin;

        public Vector3 Direction;

        /// <summary>
        /// The client's tick when it fired. Advisory only — rate limiting uses the
        /// server's own clock, because client timing is exactly what a cheater controls.
        /// </summary>
        public uint Tick;
    }

    /// <summary>
    /// A shot the server accepted, sent to everyone so other players see the tracer and
    /// the impact. The firing client has already drawn its own.
    /// </summary>
    public struct ShotFiredBroadcast : IBroadcast
    {
        public int ShooterClientId;
        public Vector3 Origin;
        public Vector3 End;
        public bool Hit;
    }

    /// <summary>
    /// Why a shot did nothing. As with a refused chop, silence would read as the game
    /// being broken rather than the gun being empty.
    /// </summary>
    public struct ShotRejectedBroadcast : IBroadcast
    {
        public ShotRejection Reason;
        public ushort AmmoRemaining;
    }

    public enum ShotRejection : byte
    {
        None = 0,

        /// <summary>Faster than the weapon's fire rate allows.</summary>
        TooSoon = 1,

        OutOfAmmo = 2,

        /// <summary>
        /// The claimed origin is nowhere near where the server has the player. Generous,
        /// because there is no lag compensation and a laggy player is not a cheat.
        /// </summary>
        ImplausibleOrigin = 3,

        /// <summary>Shooter has no weapon, or is dead.</summary>
        CannotFire = 4,
    }
}
