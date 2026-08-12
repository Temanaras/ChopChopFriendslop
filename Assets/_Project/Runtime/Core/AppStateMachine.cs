using System;

namespace ChopChop.Core
{
    /// <summary>
    /// Tracks the current <see cref="AppState"/> and announces transitions.
    /// </summary>
    public sealed class AppStateMachine
    {
        public AppState Current { get; private set; } = AppState.Booting;

        /// <summary>Fired as (previous, next) after <see cref="Current"/> updates.</summary>
        public event Action<AppState, AppState> StateChanged;

        public void Set(AppState next)
        {
            if (next == Current)
                return;

            AppState previous = Current;
            Current = next;
            StateChanged?.Invoke(previous, next);
        }
    }
}
