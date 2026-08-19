using System.Collections.Generic;
using UnityEngine;

namespace ChopChop.Cabin
{
    /// <summary>
    /// The cabin itself: the shell, and whatever has been put inside it.
    ///
    /// This carries no networked state of its own. It sits on a <c>NetworkObject</c> only
    /// so the server can spawn the whole building in one call and clients receive its
    /// fixtures with it — nested NetworkObjects spawn with their root.
    ///
    /// Its one job is to be the seam that keeps the cabin extensible: it finds every
    /// <see cref="ICabinFixture"/> beneath it and binds them all. That is why adding a
    /// workbench is a prefab edit rather than a code change, and why
    /// <c>GameBootstrap</c> has exactly one line about the cabin no matter how much ends
    /// up in it.
    /// </summary>
    public sealed class CabinBuilding : MonoBehaviour
    {
        [Tooltip("Where a player arriving at the cabin should be put down. Falls back to " +
                 "the cabin's own origin.")]
        [SerializeField] private Transform _entrance;

        private readonly List<ICabinFixture> _fixtures = new();

        /// <summary>Fixtures found at bind time. Empty before <see cref="Bind"/> runs.</summary>
        public IReadOnlyList<ICabinFixture> Fixtures => _fixtures;

        public Vector3 EntrancePosition => _entrance != null ? _entrance.position : transform.position;

        /// <summary>
        /// Hands every fixture what it needs. Server-side, once, after spawning.
        /// </summary>
        /// <remarks>
        /// Inactive children are included: a fixture that starts switched off — an
        /// unbuilt station, a locked cache — still needs its context, or it would come
        /// up inert the moment something enables it.
        /// </remarks>
        public void Bind(CabinContext context)
        {
            _fixtures.Clear();
            GetComponentsInChildren(true, _fixtures);

            for (int i = 0; i < _fixtures.Count; i++)
                _fixtures[i].Bind(context);

            Debug.Log($"[Cabin] Bound {_fixtures.Count} fixture(s).");
        }
    }
}
