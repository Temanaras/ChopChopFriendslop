using ChopChop.Core;
using ChopChop.Player;
using UnityEngine;

namespace ChopChop.UI
{
    /// <summary>
    /// A view that reads the local player, and survives not having one.
    ///
    /// The UI exists from the boot scene onward; the player is spawned into the world
    /// scene by the server minutes later, and is replaced again on respawn. Resolving
    /// once in Awake would bind to nothing forever, and resolving every frame would work
    /// but hides the rebind — so the lookup is lazy *and* event-driven, and subclasses
    /// get told when the answer changes rather than checking.
    /// </summary>
    public abstract class PlayerBoundView : MonoBehaviour
    {
        /// <summary>The local player, or null while there isn't one.</summary>
        protected LocalPlayer Player { get; private set; }

        protected virtual void OnEnable()
        {
            LocalPlayer.Changed += Resolve;
            Resolve();
        }

        protected virtual void OnDisable()
        {
            LocalPlayer.Changed -= Resolve;

            if (Player != null)
                Unbind(Player);

            Player = null;
        }

        /// <summary>Subscribe to whatever this view draws.</summary>
        protected abstract void Bind(LocalPlayer player);

        /// <summary>Undo <see cref="Bind"/>. Called before the reference is dropped.</summary>
        protected abstract void Unbind(LocalPlayer player);

        private void Resolve()
        {
            ServiceLocator.TryGet(out LocalPlayer next);

            /* Unity's fake-null makes a destroyed component compare equal to null but not
             * reference-equal to it, so compare by reference and then test for the real
             * thing — otherwise a respawn looks like "no change" and the view stays bound
             * to a corpse. */
            if (ReferenceEquals(next, Player))
                return;

            if (Player != null)
                Unbind(Player);

            Player = next != null ? next : null;

            if (Player != null)
                Bind(Player);
        }
    }
}
