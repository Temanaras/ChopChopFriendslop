using System;
using ChopChop.Combat;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// What happens when a player dies.
    ///
    /// Deliberately minimal. Whether death costs a corpse run or is a straight loss is
    /// still open, and TECH 9.3 is explicit that the decision waits until travel times
    /// are real — so this exists to make sure dying is not a dead end, and to put the
    /// hook in the right place before anything is built on top of it.
    ///
    /// **<see cref="Died"/> is where dropping a container goes.** Keeping that a seam now
    /// is what makes corpse-run-versus-straight-loss a small change later rather than a
    /// refactor. The paperdoll is never lost either way (TECH 9.3).
    ///
    /// Server-only.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public sealed class PlayerLifecycle : NetworkBehaviour
    {
        [Tooltip("Seconds face-down before respawning.")]
        [SerializeField] private float _respawnDelay = 3f;

        [Tooltip("Where to reappear. Falls back to the world origin.")]
        [SerializeField] private Vector3 _respawnPoint = new(0f, 1f, 0f);

        private Health _health;
        private CharacterController _controller;
        private float _respawnAt;
        private bool _awaitingRespawn;

        /// <summary>
        /// Raised on the server the moment a player dies, before anything is reset.
        /// Carried inventory is lost here; the paperdoll is not.
        /// </summary>
        public event Action<PlayerLifecycle, NetworkConnection> Died;

        /// <summary>Raised on the server after a respawn completes.</summary>
        public event Action<PlayerLifecycle> Respawned;

        private void Awake()
        {
            _health = GetComponent<Health>();
            TryGetComponent(out _controller);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _health.Died += HandleDied;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            _health.Died -= HandleDied;
        }

        private void HandleDied(Health health, NetworkConnection killer)
        {
            _awaitingRespawn = true;
            _respawnAt = Time.time + _respawnDelay;

            /* Carried cargo is the cost of dying; the paperdoll is not (TECH 9.3). This
             * is the single place that happens, so making death drop a lootable container
             * instead stays a small change rather than a refactor. */
            if (TryGetComponent(out PlayerPaperdoll paperdoll))
                paperdoll.DropCarriedOnDeath();

            Died?.Invoke(this, killer);
        }

        private void Update()
        {
            if (!IsServerInitialized || !_awaitingRespawn || Time.time < _respawnAt)
                return;

            _awaitingRespawn = false;
            Respawn();
        }

        private void Respawn()
        {
            /* Same disable/enable as reconciliation needs: a CharacterController caches
             * its own position and will drag the player straight back otherwise. */
            if (_controller != null)
                _controller.enabled = false;

            transform.position = _respawnPoint;

            if (_controller != null)
                _controller.enabled = true;

            _health.ResetToFull();
            Respawned?.Invoke(this);
        }

        /// <summary>Sets where this player reappears. Called by the spawner later.</summary>
        public void SetRespawnPoint(Vector3 point) => _respawnPoint = point;
    }
}
