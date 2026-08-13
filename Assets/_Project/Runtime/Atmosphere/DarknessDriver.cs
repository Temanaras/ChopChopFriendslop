using ChopChop.Biomes;
using ChopChop.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChopChop.Atmosphere
{
    /// <summary>
    /// Turns local tree density into how dark it is where you are standing (TECH 12).
    ///
    /// **Entirely client-local and never networked.** The assembly this lives in cannot
    /// reference Networking or FishNet at all, so that is enforced by the compiler rather
    /// than by discipline. Two players standing in different places see different
    /// darkness, and neither is authoritative — there is nothing to agree about.
    ///
    /// The whole thing costs one bilinear array read per frame. No raycasts, no collider
    /// queries, nothing per-tree (TECH 12.2).
    /// </summary>
    public sealed class DarknessDriver : MonoBehaviour
    {
        [Header("World")]
        [Tooltip("Supplies the density grid. Found automatically if left empty.")]
        [SerializeField] private WorldStreamer _streamer;

        [Tooltip("Whose density-to-darkness curve to use. Resolved by distance from origin.")]
        [SerializeField] private BiomeSet _biomes;

        [Header("Response")]
        [Tooltip("Seconds for darkness to catch up. Without this, walking past one trunk " +
                 "flickers the lighting (TECH 12.2).")]
        [SerializeField] private float _smoothingSeconds = 0.5f;

        [Header("Fog")]
        [SerializeField] private bool _driveFog = true;
        [SerializeField] private float _openFogDensity = 0.004f;
        [SerializeField] private float _denseFogDensity = 0.055f;
        [SerializeField] private Color _openFogColor = new(0.68f, 0.74f, 0.80f);
        [SerializeField] private Color _denseFogColor = new(0.12f, 0.14f, 0.15f);

        [Header("Light")]
        [SerializeField] private Light _sun;
        [SerializeField] private float _openSunIntensity = 1.1f;
        [SerializeField] private float _denseSunIntensity = 0.12f;

        [Header("Ambient")]
        [SerializeField] private Color _openAmbient = new(0.55f, 0.58f, 0.62f);
        [SerializeField] private Color _denseAmbient = new(0.06f, 0.07f, 0.08f);

        [Header("Post")]
        [Tooltip("Optional. Its weight is driven by darkness, so put vignette and " +
                 "exposure on it and they fade in as the canopy closes.")]
        [SerializeField] private Volume _volume;

        private DensityField _field;
        private Transform _viewer;
        private float _smoothedDensity;
        private bool _captured;

        // Restored on teardown; these are global render settings and leak between plays.
        private float _originalFogDensity;
        private Color _originalFogColor;
        private bool _originalFog;
        private Color _originalAmbient;
        private AmbientMode _originalAmbientMode;

        /// <summary>Current smoothed density, 0 to 1. Useful for audio mixing and debug.</summary>
        public float Density => _smoothedDensity;

        /// <summary>Current darkness after the biome curve, 0 to 1.</summary>
        public float Darkness { get; private set; }

        private void Awake()
        {
            // Nothing to light on a headless server, and no camera to light it for.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            CaptureOriginalSettings();
        }

        private void CaptureOriginalSettings()
        {
            _originalFog = RenderSettings.fog;
            _originalFogDensity = RenderSettings.fogDensity;
            _originalFogColor = RenderSettings.fogColor;
            _originalAmbient = RenderSettings.ambientLight;
            _originalAmbientMode = RenderSettings.ambientMode;
            _captured = true;
        }

        private void OnDisable()
        {
            if (!_captured)
                return;

            /* RenderSettings is global and survives leaving play mode in the editor.
             * Without this, a dark forest reading follows you back into every scene you
             * open next. */
            RenderSettings.fog = _originalFog;
            RenderSettings.fogDensity = _originalFogDensity;
            RenderSettings.fogColor = _originalFogColor;
            RenderSettings.ambientLight = _originalAmbient;
            RenderSettings.ambientMode = _originalAmbientMode;
        }

        private void Update()
        {
            if (!TryResolveDependencies())
                return;

            float raw = _field.Sample(_viewer.position);

            /* Exponential smoothing rather than a fixed step, so the response is the same
             * whatever the frame rate. */
            float t = _smoothingSeconds <= 0f
                ? 1f
                : 1f - Mathf.Exp(-Time.deltaTime / _smoothingSeconds);

            _smoothedDensity = Mathf.Lerp(_smoothedDensity, raw, t);
            Darkness = Mathf.Clamp01(EvaluateCurve(_smoothedDensity));

            Apply(Darkness);
        }

        /// <summary>
        /// The density-to-darkness mapping is per-biome data, not code, because it can
        /// only be tuned by eye (TECH 12.3).
        /// </summary>
        private float EvaluateCurve(float density)
        {
            if (_biomes == null || _biomes.Count == 0)
                return density;

            _biomes.Resolve(_viewer.position.magnitude, out BiomeDefinition current, out _, out _);

            if (current == null || current.DensityToDarkness == null)
                return density;

            return current.DensityToDarkness.Evaluate(density);
        }

        private void Apply(float darkness)
        {
            if (_driveFog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = Mathf.Lerp(_openFogDensity, _denseFogDensity, darkness);
                RenderSettings.fogColor = Color.Lerp(_openFogColor, _denseFogColor, darkness);
            }

            if (_sun != null)
                _sun.intensity = Mathf.Lerp(_openSunIntensity, _denseSunIntensity, darkness);

            /* Flat, not Skybox. Under Skybox ambient the sky lights the scene and
             * ambientLight is ignored outright — the values here would look correct in
             * the inspector while changing nothing on screen, which is exactly how this
             * was first written and exactly why the forest stayed bright. */
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.Lerp(_openAmbient, _denseAmbient, darkness);

            if (_volume != null)
                _volume.weight = darkness;
        }

        private bool TryResolveDependencies()
        {
            if (_streamer == null)
                _streamer = FindObjectOfType<WorldStreamer>();

            if (_streamer == null || _streamer.Store == null)
                return false;

            // Rebuilt if the streamer swapped stores, e.g. when the seed changed.
            if (_field == null)
                _field = new DensityField(_streamer.Store);

            if (_viewer == null && Camera.main != null)
                _viewer = Camera.main.transform;

            return _viewer != null;
        }
    }
}
