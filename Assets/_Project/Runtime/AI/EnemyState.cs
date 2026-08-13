namespace ChopChop.AI
{
    /// <summary>
    /// What an enemy is currently doing.
    ///
    /// **This is the animation contract.** The server decides the state and replicates
    /// it; clients read it and play whatever the rig needs. Nothing on a client ever
    /// decides a state, so two players always see the same wolf doing the same thing.
    ///
    /// Values are part of the wire format — add new states at the end, never renumber.
    /// </summary>
    public enum EnemyState : byte
    {
        /// <summary>Standing still, nothing seen. Idle or look-around loop.</summary>
        Idle = 0,

        /// <summary>Moving without a target. Walk cycle.</summary>
        Patrol = 1,

        /// <summary>Target acquired and closing. Run cycle.</summary>
        Chase = 2,

        /// <summary>In range and striking. Attack animation, driven off the state change.</summary>
        Attack = 3,

        /// <summary>Took a hit hard enough to interrupt. Brief flinch.</summary>
        Stagger = 4,

        /// <summary>Health gone. Death animation, then despawn.</summary>
        Dead = 5,
    }
}
