using System.Collections.Generic;
using ChopChop.Biomes;
using UnityEngine;

namespace ChopChop.World
{
    /// <summary>
    /// Keeps chunks resident around the local player and draws the ones that are.
    ///
    /// Client-side for now: this is the visual band (TECH 5.4), which is presentation
    /// and exists only where there is a camera. The server will need its own residency
    /// pass around *every* player once chopping needs colliders to hit — that is a
    /// different radius for a different reason and deliberately not this component.
    /// </summary>
    public sealed class WorldStreamer : MonoBehaviour
    {
        [Header("World")]
        [SerializeField] private BiomeSet _biomes;

        [Tooltip("Overridden by the server's seed once the world is handed over. The " +
                 "inspector value is what a standalone editor scene uses.")]
        [SerializeField] private int _worldSeed = 1337;

        [Header("Streaming")]
        [Tooltip("Chunk radius kept resident. 64m chunks, so 3 is roughly the 200m " +
                 "visual band in TECH 5.4.")]
        [Range(1, 8)][SerializeField] private int _radiusInChunks = 3;

        [Tooltip("Metres the centre must move before residency is recalculated. Stops a " +
                 "player standing on a chunk boundary from thrashing.")]
        [SerializeField] private float _restreamDistance = 16f;

        [Header("Clearing")]
        [SerializeField] private float _clearingRadius = 60f;
        [SerializeField] private float _clearingRampWidth = 25f;

        private ChunkStore _store;
        private TreeRenderer _renderer;

        private Transform _centre;
        private Vector3 _lastStreamPosition;
        private bool _hasStreamed;

        public ChunkStore Store => _store;
        public int LoadedChunks => _store?.LoadedCount ?? 0;
        public int LastDrawCalls => _renderer?.LastDrawCallCount ?? 0;
        public int LastInstances => _renderer?.LastInstanceCount ?? 0;

        private readonly List<Vector3> _centres = new(1);

        private void Awake()
        {
            /* A headless server has no camera and nothing to draw for. Generating and
             * instancing a forest nobody can see would be pure waste. */
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            if (_biomes == null)
            {
                Debug.LogError("[World] No BiomeSet assigned; nothing can generate.");
                enabled = false;
                return;
            }

            WorldGenSettings settings = new()
            {
                ClearingRadius = _clearingRadius,
                ClearingRampWidth = _clearingRampWidth,
            };

            _store = new ChunkStore(_worldSeed, _biomes, settings);
            _renderer = new TreeRenderer(_biomes);
        }

        /// <summary>Point streaming at a transform, normally the local player.</summary>
        public void SetCentre(Transform centre)
        {
            _centre = centre;
            _hasStreamed = false;
        }

        /// <summary>Replaces the seed and drops everything generated from the old one.</summary>
        public void SetWorldSeed(int worldSeed)
        {
            if (_worldSeed == worldSeed && _store != null)
                return;

            _worldSeed = worldSeed;

            WorldGenSettings settings = new()
            {
                ClearingRadius = _clearingRadius,
                ClearingRampWidth = _clearingRampWidth,
            };

            _store = new ChunkStore(_worldSeed, _biomes, settings);
            _hasStreamed = false;
        }

        private void Update()
        {
            if (_store == null)
                return;

            // Fall back to the camera so the forest is visible in a scene with no player
            // yet — useful for looking at generation without standing up a session.
            Transform centre = _centre != null ? _centre
                : Camera.main != null ? Camera.main.transform
                : null;

            if (centre == null)
                return;

            Vector3 position = centre.position;

            if (!_hasStreamed || (position - _lastStreamPosition).sqrMagnitude >= _restreamDistance * _restreamDistance)
            {
                _centres.Clear();
                _centres.Add(position);

                _store.UpdateResidency(_centres, _radiusInChunks);

                _lastStreamPosition = position;
                _hasStreamed = true;
            }

            _renderer.Render(_store.Loaded, null);
        }
    }
}
