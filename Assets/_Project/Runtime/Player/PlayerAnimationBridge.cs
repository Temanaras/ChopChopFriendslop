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

        [Header("Smoothing")]
        [Tooltip("Seconds to blend the speed parameter, so a locomotion tree does not " +
                 "snap between idle and run on a single tick.")]
        [SerializeField] private float _speedDamping = 0.12f;

        [Header("Facing")]
        [Tooltip("Turn the model toward the direction of travel. Movement is world-space " +
                 "and the body has no facing of its own yet, so without this the " +
                 "character runs sideways.")]
        [SerializeField] private bool _faceMovement = true;

        [SerializeField] private float _turnDegreesPerSecond = 720f;

        private PlayerMotor _motor;
        private CharacterController _controller;
        private float _smoothedSpeed;
        private int _speedHash, _chopHash;

        private void Awake()
        {
            _motor = GetComponentInParent<PlayerMotor>();
            _controller = GetComponentInParent<CharacterController>();

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            _speedHash = Animator.StringToHash(_speedParameter);
            _chopHash = Animator.StringToHash(_chopTrigger);
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
        public void PlaySwing()
        {
            if (_animator != null && HasParameter(_chopHash, AnimatorControllerParameterType.Trigger))
                _animator.SetTrigger(_chopHash);
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
