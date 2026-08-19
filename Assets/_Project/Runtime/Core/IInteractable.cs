using System.Collections.Generic;
using UnityEngine;

namespace ChopChop.Core
{
    /// <summary>
    /// Something a player can walk up to and use.
    ///
    /// Lives in Core, and deliberately mentions no networking type at all. The player
    /// does the finding and the cabin does the doing, and those two assemblies do not
    /// reference each other (TECH 3) — so the contract between them has to sit somewhere
    /// both can see, with nothing in its signature that Core is not allowed to name.
    ///
    /// <see cref="Interact"/> is called on the interacting client. It is a *request*:
    /// the implementation sends its own server RPC and the server re-checks everything,
    /// exactly as chopping and shooting do. Nothing here is authoritative.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>Where the player has to be near. Usually the object's own position.</summary>
        Vector3 InteractPoint { get; }

        /// <summary>How close, in metres.</summary>
        float InteractRange { get; }

        /// <summary>
        /// False hides it from the prompt entirely — a station mid-build, a chest that is
        /// still locked. The server checks this too; this is only what the player sees.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>What to show on screen, e.g. "Light the fire".</summary>
        string Prompt { get; }

        /// <summary>Ask to use it. Client-side; the implementation goes on to ask the server.</summary>
        void Interact();
    }

    /// <summary>
    /// Every interactable currently in the world.
    ///
    /// A flat list scanned by distance rather than a physics query, because interactables
    /// are counted in tens and a trigger collider on each one would be both more setup
    /// and more per-frame work than walking a short array. If that ever stops being true,
    /// this is the one place that has to change.
    ///
    /// Static rather than a <see cref="ServiceLocator"/> entry so there is no boot
    /// ordering to get wrong: registration is balanced against OnEnable/OnDisable, which
    /// also means it empties itself when play mode ends.
    /// </summary>
    public static class Interactables
    {
        private static readonly List<IInteractable> Registered = new();

        public static IReadOnlyList<IInteractable> All => Registered;

        public static void Register(IInteractable interactable)
        {
            if (interactable != null && !Registered.Contains(interactable))
                Registered.Add(interactable);
        }

        public static void Unregister(IInteractable interactable) => Registered.Remove(interactable);
    }
}
