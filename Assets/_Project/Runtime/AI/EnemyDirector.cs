using System;
using System.Collections.Generic;
using ChopChop.Biomes;
using ChopChop.World;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace ChopChop.AI
{
    /// <summary>
    /// Decides how much is out there (TECH 10.4).
    ///
    /// Spawn pressure comes from **local tree density** plus a **per-ring floor**. The
    /// floor is the part that matters: without it, a cleared ring becomes permanently
    /// safe, and in a persistent world that means the horror evaporates for good the
    /// first time someone clear-cuts. Density makes thick forest frightening; the floor
    /// makes cleared ground still not safe.
    ///
    /// Server-only. Enemies are spawned and despawned here and nowhere else.
    /// </summary>
    public sealed class EnemyDirector : IDisposable
    {
        /// <summary>
        /// Hard ceiling regardless of what the density maths asks for (TECH 10.4, 14).
        /// Both a performance bound and a fairness one — no situation should produce a
        /// wall of wolves.
        /// </summary>
        public int GlobalCap { get; set; } = 24;

        /// <summary>Enemies further than this from every player are despawned (TECH 10.3).</summary>
        public float CullDistance { get; set; } = 120f;

        /// <summary>Nothing spawns closer than this to a player. No ambushes from thin air.</summary>
        public float MinimumSpawnDistance { get; set; } = 28f;

        public float MaximumSpawnDistance { get; set; } = 70f;

        /// <summary>Seconds between spawn attempts.</summary>
        public float EvaluationInterval { get; set; } = 4f;

        private readonly NetworkManager _networkManager;
        private readonly NetworkObject _enemyPrefab;
        private readonly DensityField _density;
        private readonly BiomeSet _biomes;

        private readonly List<NetworkObject> _alive = new();
        private readonly List<NetworkObject> _cullScratch = new();

        private float _nextEvaluation;
        private uint _spawnSalt;

        public int AliveCount => _alive.Count;

        public EnemyDirector(NetworkManager networkManager, NetworkObject enemyPrefab,
            DensityField density, BiomeSet biomes)
        {
            _networkManager = networkManager ? networkManager : throw new ArgumentNullException(nameof(networkManager));
            _enemyPrefab = enemyPrefab;
            _density = density;
            _biomes = biomes;
        }

        public void Dispose() => _alive.Clear();

        /// <summary>Call from the server's update. Cheap: it does nothing most frames.</summary>
        public void Tick(float deltaSeconds)
        {
            if (_enemyPrefab == null)
                return;

            _nextEvaluation -= deltaSeconds;

            if (_nextEvaluation > 0f)
                return;

            _nextEvaluation = EvaluationInterval;

            CullDistant();
            TrySpawn();
        }

        /// <summary>
        /// Despawns anything nobody is near. Aggressive on purpose — an enemy no player
        /// can see is pure cost, and its state is not worth preserving (TECH 6.3 lists
        /// enemy positions as rebuilt on load, never saved).
        /// </summary>
        private void CullDistant()
        {
            _cullScratch.Clear();

            for (int i = 0; i < _alive.Count; i++)
            {
                NetworkObject enemy = _alive[i];

                if (enemy == null || !enemy.IsSpawned)
                {
                    _cullScratch.Add(enemy);
                    continue;
                }

                if (NearestPlayerDistance(enemy.transform.position) > CullDistance)
                    _cullScratch.Add(enemy);
            }

            for (int i = 0; i < _cullScratch.Count; i++)
            {
                NetworkObject enemy = _cullScratch[i];
                _alive.Remove(enemy);

                if (enemy != null && enemy.IsSpawned)
                    _networkManager.ServerManager.Despawn(enemy.gameObject);
            }
        }

        private void TrySpawn()
        {
            if (_alive.Count >= GlobalCap)
                return;

            // Around a randomly chosen player, so pressure follows the group rather than
            // always building on whoever happens to be first in the list.
            if (!TryPickPlayer(out Vector3 around))
                return;

            if (!TryFindSpawnPoint(around, out Vector3 point, out float pressure))
                return;

            // Pressure is a probability per evaluation, so thick forest produces enemies
            // often and open ground still produces them occasionally.
            if (UnityEngine.Random.value > pressure)
                return;

            NetworkObject enemy = UnityEngine.Object.Instantiate(_enemyPrefab, point, Quaternion.identity);
            _networkManager.ServerManager.Spawn(enemy.gameObject);
            _alive.Add(enemy);
        }

        private bool TryPickPlayer(out Vector3 position)
        {
            position = default;

            int count = _networkManager.ServerManager.Clients.Count;

            if (count == 0)
                return false;

            int wanted = UnityEngine.Random.Range(0, count);
            int index = 0;

            foreach (NetworkConnection connection in _networkManager.ServerManager.Clients.Values)
            {
                if (index++ != wanted)
                    continue;

                if (connection?.FirstObject == null)
                    return false;

                position = connection.FirstObject.transform.position;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds somewhere out of sight to put one, and reports how much that spot wants
        /// an enemy.
        /// </summary>
        private bool TryFindSpawnPoint(Vector3 around, out Vector3 point, out float pressure)
        {
            point = default;
            pressure = 0f;

            // A handful of tries rather than a search. Failing is fine; the next
            // evaluation is only seconds away.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = UnityEngine.Random.value * Mathf.PI * 2f;
                float distance = UnityEngine.Random.Range(MinimumSpawnDistance, MaximumSpawnDistance);

                Vector3 candidate = around + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;

                // Never on top of somebody else.
                if (NearestPlayerDistance(candidate) < MinimumSpawnDistance)
                    continue;

                point = candidate;
                pressure = PressureAt(candidate);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Local density plus the ring's floor, clamped. Blending across rings comes with
        /// the enemy tables in the biome definitions; for now the floor is the ring's
        /// contribution.
        /// </summary>
        private float PressureAt(Vector3 position)
        {
            float density = _density?.Sample(position) ?? 0f;
            float floor = 0.05f;

            if (_biomes != null && _biomes.Count > 0)
            {
                _biomes.Resolve(position.magnitude, out BiomeDefinition biome, out _, out _);

                if (biome != null)
                    floor = biome.SpawnRateFloor;
            }

            return Mathf.Clamp01(Mathf.Max(floor, density));
        }

        private float NearestPlayerDistance(Vector3 position)
        {
            float nearest = float.MaxValue;

            foreach (NetworkConnection connection in _networkManager.ServerManager.Clients.Values)
            {
                if (connection?.FirstObject == null)
                    continue;

                float distance = Vector3.Distance(connection.FirstObject.transform.position, position);

                if (distance < nearest)
                    nearest = distance;
            }

            return nearest;
        }
    }
}
