using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace ChopChop.Combat
{
    /// <summary>
    /// Decides whether a shot happened (TECH 10.2).
    ///
    /// Hitscan only — no networked projectiles in the vertical slice (TECH 10.1). The
    /// client draws its shot immediately and this re-raycasts from the server's own view
    /// of the world to decide what it hit. A rejected shot simply deals no damage; there
    /// is no rollback, because at four players nobody will notice a tracer that did
    /// nothing (TECH 10.2).
    ///
    /// **No lag compensation** (TECH 4.4). This uses current positions with a generous
    /// tolerance rather than rewinding the world, which is a deliberate simplification
    /// for four-player PvE.
    /// </summary>
    public sealed class WeaponServer : IDisposable
    {
        /// <summary>
        /// How far the claimed origin may be from where the server has the player
        /// (TECH 10.2). Generous on purpose: the client fired some milliseconds ago from
        /// a position that has since moved, and punishing that would punish ping rather
        /// than cheating.
        /// </summary>
        public const float OriginTolerance = 2f;

        private readonly NetworkManager _networkManager;
        private readonly LayerMask _hitMask;

        private readonly Dictionary<NetworkConnection, uint> _lastShotTick = new();
        private readonly Dictionary<NetworkConnection, ushort> _ammo = new();

        private bool _registered;

        /// <summary>Damage one round does.</summary>
        public ushort DamagePerShot { get; set; } = 34;

        /// <summary>Minimum server ticks between accepted shots. 30Hz, so 6 is ~5 rounds/second.</summary>
        public uint FireCooldownTicks { get; set; } = 6;

        public float Range { get; set; } = 120f;

        /// <summary>
        /// Rounds before a reload. Placeholder: real ammunition has to be a transferable
        /// item (TECH 2.3), which lands with the paperdoll.
        /// </summary>
        public ushort MagazineSize { get; set; } = 12;

        /// <summary>Raised on a confirmed hit against something with health.</summary>
        public event Action<NetworkConnection, Health, Vector3> Hit;

        public WeaponServer(NetworkManager networkManager, LayerMask hitMask)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _hitMask = hitMask;

            _networkManager.ServerManager.RegisterBroadcast<FireRequestBroadcast>(HandleFireRequest);
            _networkManager.ServerManager.OnRemoteConnectionState += HandleConnectionState;
            _registered = true;
        }

        public void Dispose()
        {
            if (!_registered)
                return;

            _registered = false;
            _networkManager.ServerManager.UnregisterBroadcast<FireRequestBroadcast>(HandleFireRequest);
            _networkManager.ServerManager.OnRemoteConnectionState -= HandleConnectionState;

            _lastShotTick.Clear();
            _ammo.Clear();
        }

        public ushort AmmoFor(NetworkConnection connection)
            => _ammo.TryGetValue(connection, out ushort ammo) ? ammo : MagazineSize;

        /// <summary>Refills a magazine. Server-only; hooked to a reload input later.</summary>
        public void Reload(NetworkConnection connection) => _ammo[connection] = MagazineSize;

        private void HandleConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
                return;

            _lastShotTick.Remove(connection);
            _ammo.Remove(connection);
        }

        private void HandleFireRequest(NetworkConnection connection, FireRequestBroadcast message, Channel channel)
        {
            uint tick = _networkManager.TimeManager.Tick;

            if (!TryValidate(connection, message, tick, out ShotRejection rejection))
            {
                Reject(connection, rejection);
                return;
            }

            _lastShotTick[connection] = tick;
            _ammo[connection] = (ushort)(AmmoFor(connection) - 1);

            /* Re-raycast from the server's own world rather than trusting what the client
             * says it hit. The direction is taken from the client because that is aim,
             * which is theirs to decide; the origin and the outcome are not. */
            Vector3 direction = message.Direction.sqrMagnitude > 0.0001f
                ? message.Direction.normalized
                : Vector3.forward;

            bool hit = Physics.Raycast(message.Origin, direction, out RaycastHit info, Range, _hitMask);
            Vector3 end = hit ? info.point : message.Origin + direction * Range;

            if (hit && info.collider.TryGetComponentInParent(out Health health) && health.IsAlive)
            {
                health.TryApplyDamage(DamagePerShot, connection);
                Hit?.Invoke(connection, health, info.point);
            }

            // Everyone sees the tracer, including the shooter, whose own optimistic one
            // has already played and will simply be replaced.
            _networkManager.ServerManager.Broadcast(new ShotFiredBroadcast
            {
                ShooterClientId = connection.ClientId,
                Origin = message.Origin,
                End = end,
                Hit = hit,
            }, true, Channel.Unreliable);
        }

        private bool TryValidate(NetworkConnection connection, FireRequestBroadcast message, uint tick,
            out ShotRejection rejection)
        {
            rejection = ShotRejection.None;

            // Server clock, never the tick the client claimed.
            if (_lastShotTick.TryGetValue(connection, out uint last) && tick - last < FireCooldownTicks)
            {
                rejection = ShotRejection.TooSoon;
                return false;
            }

            if (AmmoFor(connection) == 0)
            {
                rejection = ShotRejection.OutOfAmmo;
                return false;
            }

            if (connection?.FirstObject == null)
            {
                rejection = ShotRejection.CannotFire;
                return false;
            }

            // A dead player does not shoot.
            if (connection.FirstObject.TryGetComponent(out Health shooterHealth) && !shooterHealth.IsAlive)
            {
                rejection = ShotRejection.CannotFire;
                return false;
            }

            Vector3 serverPosition = connection.FirstObject.transform.position;

            /* Vertical slack is larger because the muzzle sits at eye height while the
             * server's position is at the player's feet. Checking a single radius would
             * reject every legitimate shot. */
            Vector3 offset = message.Origin - serverPosition;
            float horizontal = new Vector2(offset.x, offset.z).magnitude;

            if (horizontal > OriginTolerance || offset.y < -OriginTolerance || offset.y > OriginTolerance + 2f)
            {
                rejection = ShotRejection.ImplausibleOrigin;
                return false;
            }

            return true;
        }

        private void Reject(NetworkConnection connection, ShotRejection reason)
        {
            _networkManager.ServerManager.Broadcast(connection, new ShotRejectedBroadcast
            {
                Reason = reason,
                AmmoRemaining = AmmoFor(connection),
            }, true, Channel.Reliable);
        }
    }

    internal static class ComponentSearchExtensions
    {
        /// <summary>
        /// Colliders are usually on a child of the thing that owns the health, so a
        /// direct TryGetComponent would miss every hit on a rigged character.
        /// </summary>
        public static bool TryGetComponentInParent<T>(this Component component, out T found) where T : Component
        {
            found = component.GetComponentInParent<T>();
            return found != null;
        }
    }
}
