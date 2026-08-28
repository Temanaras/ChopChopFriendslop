namespace ChopChop.Items
{
    /// <summary>
    /// Why an item request did or did not happen.
    ///
    /// Exists because the first version of this system computed exactly this and threw it
    /// away — <c>CabinStorage</c> still returns a result that both of its RPCs discard,
    /// so a refused deposit is indistinguishable from a lost packet. A slot that springs
    /// back with no explanation reads as a bug, which is the same argument TECH 5.6 makes
    /// for answering a refused chop.
    ///
    /// Lives in Items rather than beside either caller so the player's inventory and the
    /// cabin's storage can refuse in the same words.
    /// </summary>
    public enum ItemMoveResult : byte
    {
        Ok = 0,

        /// <summary>The source slot was empty by the time the server looked.</summary>
        NothingThere = 1,

        /// <summary>The destination could not take it, wholly or in part.</summary>
        NoRoom = 2,

        /// <summary>The item does not belong in that paperdoll slot.</summary>
        WrongSlot = 3,

        /// <summary>Out of range index, no inventory, or an unknown item id.</summary>
        Invalid = 4,

        /// <summary>Too far from the container to be touching it.</summary>
        OutOfReach = 5,
    }
}
