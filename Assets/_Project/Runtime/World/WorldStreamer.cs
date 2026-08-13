using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Keeps chunks, colliders and visuals resident around whoever needs them.
    ///
    /// The asymmetry in TECH 5.4 is the whole design here: **a client keeps colliders
    /// only around itself, and the server keeps them around every player.** The server
    /// has to, because it validates what everyone chops, and a raycast cannot hit a tree
    /// that has no collider. Rendering is the opposite — only a client with a camera does
    /// any of it.
    /// </summary>
    public sealed class WorldStreamer : MonoBehaviour
    {
        [Header("World")]
        [SerializeField] private BiomeSet _biomes;
        [SerializeField] private int _worldSeed = 1337;

        [Header("Streaming")]
        [Tooltip("Chunk radius kept resident. 64m chunks, so 3 is roughly the 200m " +
                 "visual band in TECH 5.4.")]
        [Range(1, 8)][SerializeField] private int _radiusInChunks = 3;

        [Tooltip("Radius in metres where trees get real colliders. Small on purpose: " +
                 "this is the only band that supports interaction.")]
        [SerializeField] private float _colliderRadius = 48f;

        [Tooltip("Metres a centre must move before residency is recalculated.")]
        [SerializeField] private float _restreamDistance = 16f;

        [Header("Clearing")]
        [SerializeField] private float _clearingRadius = 60f;
        [SerializeField] private float _clearingRampWidth = 25f;

        private ChunkStore _store;
        private TreeRenderer _renderer;
        private TreeColliderBand _colliders;
        private TreeDiffStore _diffs;

        private TreeClient _treeClient;
        private Transform _localCentre;
        private readonly List<Vector3> _centres = new();
        private readonly List<long> _subscriptionScratch = new();
        private Vector3 _lastStreamPosition;
        private bool _hasStreamed;
        private bool _renders;

        /// <summary>Supplies every position that should keep chunks loaded.</summary>
        public delegate void CentreProvider(List<Vector3> into);

        /// <summary>
        /// Set by the server to report all player positions. Left null on a client, which
        /// falls back to its own player.
        /// </summary>
        public CentreProvider ServerCentres { get; set; }

        public ChunkStore Store => _store;
        public TreeColliderBand Colliders => _colliders;
        public int LoadedChunks => _store?.LoadedCount ?? 0;
        public int ActiveColliders => _colliders?.ActiveCount ?? 0;
        public int LastDrawCalls => _renderer?.LastDrawCallCount ?? 0;
        public int LastInstances => _renderer?.LastInstanceCount ?? 0;

        private void Awake()
        {
            if (_biomes == null)
            {
                Debug.LogError("[World] No BiomeSet assigned; nothing can generate.");
                enabled = false;
                return;
            }

            // A headless server has no camera and nothing to draw for, but it still needs
            // chunks and colliders.
            _renders = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

            Rebuild();
        }

        private void Rebuild()
        {
            WorldGenSettings settings = new()
            {
                ClearingRadius = _clearingRadius,
                ClearingRampWidth = _clearingRampWidth,
            };

            _store = new ChunkStore(_worldSeed, _biomes, settings);
            _colliders = new TreeColliderBand(transform, _colliderRadius);

            if (_renders)
                _renderer = new TreeRenderer(_biomes);
        }

        private void Start()
        {
            /* Collected rather than injected: this component lives in the world scene,
             * which the server loads after boot, so the bootstrap has no reference to it
             * and it has none back. */
            if (!Core.ServiceLocator.TryGet(out WorldStreamingContext context))
                return;

            _diffs = context.Diffs;
            ServerCentres = context.ServerCentres;
            SetWorldSeed(context.WorldSeed);

            Core.ServiceLocator.TryGet(out _treeClient);
        }

        /// <summary>The diff store deciding which trees are felled. Set once at boot.</summary>
        public void SetDiffStore(TreeDiffStore diffs) => _diffs = diffs;

        /// <summary>Point streaming at a transform, normally the local player.</summary>
        public void SetCentre(Transform centre)
        {
            _localCentre = centre;
            _hasStreamed = false;
        }

        public void SetWorldSeed(int worldSeed)
        {
            if (_worldSeed == worldSeed && _store != null)
                return;

            _worldSeed = worldSeed;
            _colliders?.Clear();
            Rebuild();
            _hasStreamed = false;
        }

        /// <summary>Drops a felled tree's collider immediately rather than next stream.</summary>
        public void OnTreeFelled(long chunkKey, ushort localIndex)
            => _colliders?.Remove(chunkKey, localIndex);

        private void Update()
        {
            if (_store == null)
                return;

            CollectCentres();

            if (_centres.Count == 0)
                return;

            /* Restream on movement rather than every frame. The threshold also stops a
             * player standing on a chunk boundary from loading and evicting the same
             * chunks forever. */
            bool moved = !_hasStreamed
                         || (_centres[0] - _lastStreamPosition).sqrMagnitude >= _restreamDistance * _restreamDistance;

            if (moved)
            {
                _store.UpdateResidency(_centres, _radiusInChunks);
                _lastStreamPosition = _centres[0];
                _hasStreamed = true;

                PublishSubscriptions();
            }

            // Colliders update every frame: the band is small, and a player walking into
            // range needs something to hit now rather than at the next restream.
            _colliders.Update(_store.Loaded, _centres, _diffs);

            if (_renders)
                _renderer.Render(_store.Loaded, null);
        }

        /// <summary>
        /// Tells the server which chunks the local player is standing in.
        ///
        /// Always sent, including from a hosted server. It is tempting to skip it there
        /// because both halves share one diff store in-process and the reply is
        /// redundant — but subscription is also what defines occupancy, and occupancy is
        /// what stops regrowth reclaiming ground players are holding (TECH 7.1). Skipping
        /// it left a hosted session with no occupied chunks at all, so regrowth never
        /// ran in the way people actually play.
        ///
        /// Built from the local player's own radius, never from what is resident: on a
        /// server, residency follows *every* player, and subscribing this client to all
        /// of them would be wrong.
        /// </summary>
        private void PublishSubscriptions()
        {
            if (_treeClient == null || _localCentre == null)
                return;

            _subscriptionScratch.Clear();

            ChunkStore.WorldToChunk(_localCentre.position, out int centreX, out int centreZ);

            for (int z = centreZ - _radiusInChunks; z <= centreZ + _radiusInChunks; z++)
            for (int x = centreX - _radiusInChunks; x <= centreX + _radiusInChunks; x++)
            {
                int dx = x - centreX;
                int dz = z - centreZ;

                if (dx * dx + dz * dz <= _radiusInChunks * _radiusInChunks)
                    _subscriptionScratch.Add(ChunkKey.Pack(x, z));
            }

            _treeClient.SetSubscribedChunks(_subscriptionScratch);
        }

        private void CollectCentres()
        {
            _centres.Clear();

            if (ServerCentres != null)
            {
                ServerCentres(_centres);
                return;
            }

            Transform centre = _localCentre != null ? _localCentre
                : Camera.main != null ? Camera.main.transform
                : null;

            if (centre != null)
                _centres.Add(centre.position);
        }
    }
}
