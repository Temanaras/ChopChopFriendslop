using UnityEngine;
using UnityEngine.Events;

namespace ChopChop.AI
{
    /// <summary>
    /// Drives a rig from the enemy's replicated state.
    ///
    /// This is the seam a rigged model plugs into. It reads state and speed, which the
    /// server decides, and pushes them at an <see cref="Animator"/> — so swapping the
    /// grey-box capsule for a wolf is a matter of assigning an Animator and matching a
    /// few parameter names, with no behaviour code touched.
    ///
    /// Presentation only, and it runs on every machine including the server's own copy.
    /// It never decides anything; if this component is missing the enemy still behaves
    /// identically, just without animation.
    /// </summary>
    public sealed class EnemyAnimationBridge : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Animator on the model. Leave empty to search children, so a rig can be " +
                 "dropped in as a child without rewiring.")]
        [SerializeField] private Animator _animator;

        [Header("Parameter names")]
        [Tooltip("Float. Planar speed in m/s, for a locomotion blend tree.")]
        [SerializeField] private string _speedParameter = "Speed";

        [Tooltip("Int. The EnemyState value, if the controller switches on it directly.")]
        [SerializeField] private string _stateParameter = "State";

        [Tooltip("Trigger. Fired once when a strike begins.")]
        [SerializeField] private string _attackTrigger = "Attack";

        [Tooltip("Trigger. Fired once when the enemy flinches.")]
        [SerializeField] private string _staggerTrigger = "Stagger";

        [Tooltip("Trigger. Fired once on death.")]
        [SerializeField] private string _deathTrigger = "Death";

        [Header("Smoothing")]
        [Tooltip("Seconds to blend the speed parameter. Stops a locomotion tree snapping " +
                 "between walk and run on a single tick of network jitter.")]
        [SerializeField] private float _speedDamping = 0.15f;

        [Header("Events")]
        [Tooltip("Fired on every state change, for audio and effects that are easier to " +
                 "wire in the inspector than in an animator.")]
        public UnityEvent<EnemyState> StateEntered;

        [Tooltip("Fired when a strike actually connects, for impact effects.")]
        public UnityEvent StruckTarget;

        private EnemyBrain _brain;
        private float _smoothedSpeed;

        private int _speedHash, _stateHash, _attackHash, _staggerHash, _deathHash;

        private void Awake()
        {
            _brain = GetComponentInParent<EnemyBrain>();

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            _speedHash = Animator.StringToHash(_speedParameter);
            _stateHash = Animator.StringToHash(_stateParameter);
            _attackHash = Animator.StringToHash(_attackTrigger);
            _staggerHash = Animator.StringToHash(_staggerTrigger);
            _deathHash = Animator.StringToHash(_deathTrigger);
        }

        private void OnEnable()
        {
            if (_brain == null)
                return;

            _brain.StateChanged += HandleStateChanged;
            _brain.Struck += HandleStruck;
        }

        private void OnDisable()
        {
            if (_brain == null)
                return;

            _brain.StateChanged -= HandleStateChanged;
            _brain.Struck -= HandleStruck;
        }

        private void Update()
        {
            if (_brain == null || _animator == null)
                return;

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, _brain.Speed,
                _speedDamping <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / _speedDamping));

            if (HasParameter(_speedHash, AnimatorControllerParameterType.Float))
                _animator.SetFloat(_speedHash, _smoothedSpeed);
        }

        private void HandleStateChanged(EnemyState previous, EnemyState next)
        {
            StateEntered?.Invoke(next);

            if (_animator == null)
                return;

            if (HasParameter(_stateHash, AnimatorControllerParameterType.Int))
                _animator.SetInteger(_stateHash, (int)next);

            /* Triggers for the one-shots. A blend tree can handle locomotion from Speed
             * alone, but a strike or a death has to fire exactly once, and driving those
             * off a state int risks replaying them if the state is re-entered. */
            switch (next)
            {
                case EnemyState.Attack:
                    SetTrigger(_attackHash);
                    break;

                case EnemyState.Stagger:
                    SetTrigger(_staggerHash);
                    break;

                case EnemyState.Dead:
                    SetTrigger(_deathHash);
                    break;
            }
        }

        private void HandleStruck() => StruckTarget?.Invoke();

        private void SetTrigger(int hash)
        {
            if (HasParameter(hash, AnimatorControllerParameterType.Trigger))
                _animator.SetTrigger(hash);
        }

        /// <summary>
        /// Checked rather than assumed, so a controller that only implements some of
        /// these does not spam warnings every time an enemy blinks.
        /// </summary>
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
