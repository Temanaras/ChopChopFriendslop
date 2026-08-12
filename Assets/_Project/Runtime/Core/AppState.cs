namespace ChopChop.Core
{
    /// <summary>
    /// Coarse application phase. Deliberately not a gameplay state machine — this
    /// only tracks what the app as a whole is doing.
    /// </summary>
    public enum AppState : byte
    {
        Booting = 0,
        Menu = 1,
        Connecting = 2,
        InGame = 3,
    }
}
