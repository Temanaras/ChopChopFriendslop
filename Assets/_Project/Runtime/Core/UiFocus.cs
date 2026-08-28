using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChopChop.Core
{
    /// <summary>
    /// Whether a screen currently has the player's hands.
    ///
    /// Gameplay input has four independent readers — the motor's
    /// <c>PlayerInputReader</c>, and the chopper, the weapon and the loadout, which each
    /// reach into the Input System directly. Disabling one does nothing to the other
    /// three, so "the inventory is open" cannot be expressed by switching a component
    /// off. It has to be a fact all four can ask about.
    ///
    /// Static rather than a <see cref="ServiceLocator"/> entry for the same reason
    /// <see cref="Interactables"/> is: input is read from Update on objects that spawn
    /// long after boot, and there is no ordering here to get wrong.
    ///
    /// Counted rather than boolean because screens stack — a chest panel opens over the
    /// inventory, and the inventory closing first must not hand movement back while the
    /// chest is still up.
    /// </summary>
    public static class UiFocus
    {
        private static readonly List<Token> Held = new();

        private static bool _blocked;
        private static bool _wantsCursor;

        /// <summary>
        /// True while any screen is open. Every reader of gameplay input must check this;
        /// a reader that forgets will keep firing behind an open menu.
        /// </summary>
        public static bool GameplayBlocked => _blocked;

        /// <summary>
        /// True while something on screen needs a mouse pointer.
        ///
        /// Separate from <see cref="GameplayBlocked"/> because they are not the same
        /// question: a full-screen death fade blocks input and wants no cursor, and a
        /// future pause overlay could want the reverse.
        /// </summary>
        public static bool WantsCursor => _wantsCursor;

        /// <summary>Raised after either flag changes, never on a no-op re-count.</summary>
        public static event Action Changed;

        /// <summary>
        /// Take the player's hands until the returned token is disposed. Dispose is
        /// idempotent and order-independent, so a screen torn down out of sequence
        /// cannot strand the game with no input.
        /// </summary>
        public static IDisposable Capture(bool cursor = true)
        {
            Token token = new(cursor);
            Held.Add(token);
            Recount();
            return token;
        }

        /// <summary>
        /// Drop every capture. For teardown, where the objects holding the tokens are
        /// going away anyway and nobody is left to dispose them.
        /// </summary>
        public static void Clear()
        {
            if (Held.Count == 0)
                return;

            for (int i = 0; i < Held.Count; i++)
                Held[i].Released = true;

            Held.Clear();
            Recount();
        }

        /* Statics survive a play session when domain reload is off, and the second run
         * would start with whatever the first one leaked still held — no movement, no
         * chopping, and nothing on screen to explain it. */
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Held.Clear();
            _blocked = false;
            _wantsCursor = false;
            Changed = null;
        }

        private static void Release(Token token)
        {
            if (token.Released)
                return;

            token.Released = true;
            Held.Remove(token);
            Recount();
        }

        private static void Recount()
        {
            bool blocked = Held.Count > 0;
            bool cursor = false;

            for (int i = 0; i < Held.Count && !cursor; i++)
                cursor = Held[i].WantsCursor;

            if (blocked == _blocked && cursor == _wantsCursor)
                return;

            _blocked = blocked;
            _wantsCursor = cursor;
            Changed?.Invoke();
        }

        private sealed class Token : IDisposable
        {
            public Token(bool wantsCursor) => WantsCursor = wantsCursor;

            public bool WantsCursor { get; }
            public bool Released { get; set; }

            public void Dispose() => Release(this);
        }
    }
}
