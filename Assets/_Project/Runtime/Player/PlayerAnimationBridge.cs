using UnityEngine;

namespace ChopChop.Player
{
    /// <summary>
    /// Drives the character rig from what the player is actually doing.
    ///
    /// The same seam as <c>EnemyAnimationBridge</c>: it reads state that already exists
    /// and pushes it at an <see cref="Animator"/>, so swapping the model or the animation
    /// pack is a matter of assigning a controller and matching parameter names. No
    /// gameplay code knows a rig exists.
    ///
    /// Runs on every machine. Speed comes from <see cref="PlayerMotor.PlanarSpeed"/>,
    /// which is valid for remote players too because state forwarding replays their
    /// inputs locally — so a spectator sees a run cycle rather than a slide.
    /// </summary>
    public sealed class PlayerAnimationBridge : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Leave empty to search children, so a model can be dropped in without rewiring.")]
        [SerializeField] private Animator _animator;

        [Header("Parameter names")]
        [SerializeField] private string _speedParameter = "Speed";
        [SerializeField] private string _chopTrigger = "Chop";
        [SerializeField] private string _stumbleTrigger = "Stumble";
        [SerializeField] private string _rateParameter = "LocomotionRate";

        [Header("Smoothing")]
        [Tooltip("Seconds to blend the speed parameter, so a locomotion tree does not " +
                 "snap between idle and run on a single tick.")]
        [SerializeField] private float _speedDamping = 0.12f;

        [Header("Cadence")]
        [Tooltip("The speed the run clip was authored to travel at. This is a property of " +
                 "the animation, not of the game: it should match the run child's threshold " +
                 "in the blend tree and has no business tracking the sprint speed.")]
        [SerializeField] private float _naturalRunSpeed = 5.5f;

        [Tooltip("Ceiling on how far playback is sped up to keep the legs under the body. " +
                 "Sprint travels 55% faster than the run clip, but playing it back at 1.55x " +
                 "reads as fast-forward — a different wrong. Real runners buy some of that " +
                 "speed with stride length, so the legs are allowed to fall a little short.")]
        [SerializeField] private float _maxRate = 1.35f;

        [Header("Facing")]
        [Tooltip("Turn the model toward the direction of travel. Movement is world-space " +
                 "and the body has no facing of its own yet, so without this the " +
                 "character runs sideways.")]
        [SerializeField] private bool _faceMovement = true;

        [SerializeField] private float _turnDegreesPerSecond = 720f;

        private PlayerMotor _motor;
        private CharacterController _controller;
        private float _smoothedSpeed;
        private int _speedHash, _chopHash, _stumbleHash, _rateHash;

        private void Awake()
        {
            _motor = GetComponentInParent<PlayerMotor>();
            _controller = GetComponentInParent<CharacterController>();

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            _speedHash = Animator.StringToHash(_speedParameter);
            _chopHash = Animator.StringToHash(_chopTrigger);
            _stumbleHash = Animator.StringToHash(_stumbleTrigger);
            _rateHash = Animator.StringToHash(_rateParameter);
        }

        private void Update()
        {
            if (_motor == null || _animator == null)
                return;

            float target = _motor.PlanarSpeed;

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, target,
                _speedDamping <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / _speedDamping));

            if (HasParameter(_speedHash, AnimatorControllerParameterType.Float))
                _animator.SetFloat(_speedHash, _smoothedSpeed);

            /* Sprint travels faster than any clip in the tree, so choosing the right clip
             * is only half the job — the other half is running it at the right cadence.
             * Without this the legs keep a 5 m/s beat while the body covers 8.5 and the
             * feet skate. Read off the smoothed value so the rate does not judder on a
             * single tick's worth of noise. */
            if (HasParameter(_rateHash, AnimatorControllerParameterType.Float))
            {
                float rate = _naturalRunSpeed <= 0f
                    ? 1f
                    : Mathf.Clamp(_smoothedSpeed / _naturalRunSpeed, 1f, _maxRate);

                _animator.SetFloat(_rateHash, rate);
            }

            if (_faceMovement)
                FaceTravel();
        }

        /// <summary>
        /// Points the model where it is going. Read from the controller's actual velocity
        /// rather than from input, so it is correct on remote players as well.
        /// </summary>
        private void FaceTravel()
        {
            if (_controller == null)
                return;

            Vector3 velocity = _controller.velocity;
            velocity.y = 0f;

            if (velocity.sqrMagnitude < 0.25f)
                return;

            Quaternion wanted = Quaternion.LookRotation(velocity.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, wanted, _turnDegreesPerSecond * Time.deltaTime);
        }

        /// <summary>Hook this to PlayerChopper's Swung event in the inspector.</summary>
        public void PlaySwing() => SetTrigger(_chopHash);

        /// <summary>
        /// Hook this to PlayerChopper's Missed event in the inspector.
        ///
        /// A missed swing locks the player out for about a second. Until this existed the
        /// only sign of that was a word on the HUD, so the penalty read as the game having
        /// hiccupped rather than as something the player did.
        /// </summary>
        public void PlayStumble() => SetTrigger(_stumbleHash);

        private void SetTrigger(int hash)
        {
            if (_animator != null && HasParameter(hash, AnimatorControllerParameterType.Trigger))
                _animator.SetTrigger(hash);
        }

        private bool HasParameter(int hash, AnimatorControllerParameterType type)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return false;

            foreach (AnimatorControllerParameter parameter in _animator.parameters)
            {
                if (parameter.nameHash == hash && parameter.type == type)
                    return true;
            }

            return false;
        }
    }
}
