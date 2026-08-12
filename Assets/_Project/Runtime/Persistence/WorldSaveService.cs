using System;
using UnityEngine;

namespace ChopChop.Persistence
{
    /// <summary>
    /// Owns the live world (TECH 6). Server-only — a session belongs to a server, and
    /// the server is the single writer of the save. Clients hold no copy.
    ///
    /// That is a deliberate change from the original distributed model. Every client
    /// holding a full copy existed to survive the host vanishing; with a server that
    /// can outlive any player, it bought a monotonic version counter, a conflict
    /// resolution rule, and a class of "whose world wins" bugs, in exchange for
    /// nothing.
    ///
    /// Autosave is deliberately dumb: a wall-clock interval, an atomic write, and no
    /// cleverness about what changed. A snapshot is either complete or it is not
    /// worth having, because the same bytes have to be able to reconstruct the world
    /// from nothing for a late joiner (TECH 2.4, 8.4).
    /// </summary>
    public sealed class WorldSaveService : IDisposable
    {
        /// <summary>TECH 6.4.</summary>
        public const float AutosaveIntervalSeconds = 45f;

        public const string WorldFileName = "world.sav";

        private readonly SaveStore _store;
        private readonly string _fileName;

        private float _secondsSinceSave;
        private bool _disposed;

        /// <summary>The authoritative world. Never handed out to clients as an object.</summary>
        public WorldSave World { get; private set; }

        /// <summary>Raised after a successful write, with the bytes that were written.</summary>
        public event Action<byte[]> Saved;

        public WorldSaveService(SaveStore store, string fileName = WorldFileName)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _fileName = string.IsNullOrEmpty(fileName) ? WorldFileName : fileName;
        }

        /// <summary>
        /// Loads the existing world, or creates one for <paramref name="newWorldSeed"/>
        /// if there is nothing to load.
        /// </summary>
        /// <returns>Why the load ended as it did, for the caller to report.</returns>
        public SaveLoadStatus LoadOrCreate(int newWorldSeed)
        {
            if (!_store.TryRead(_fileName, out byte[] bytes, out bool usedBackup))
            {
                World = WorldSave.CreateNew(newWorldSeed);
                Debug.Log($"[World] No save found; created a new world with seed {newWorldSeed}.");
                return SaveLoadStatus.Ok;
            }

            SaveLoadStatus status = SaveSerializer.TryDeserialize(bytes, out WorldSave loaded);

            switch (status)
            {
                case SaveLoadStatus.Ok:
                case SaveLoadStatus.GenerationChanged:
                    World = loaded;
                    Debug.Log(
                        $"[World] Loaded seed {World.WorldSeed} at tick {World.WorldTick}, " +
                        $"{World.Chunks.Count} chunk(s) with diffs{(usedBackup ? ", from backup" : "")}.");
                    break;

                default:
                    /* Refusing is the point. A save from a newer build, or one we cannot
                     * parse, must not be quietly replaced with an empty world — that
                     * turns a recoverable problem into a deleted world. */
                    Debug.LogError(
                        $"[World] Refusing to start: existing save is unusable ({status}). " +
                        "Move or delete it deliberately if you want a fresh world.");
                    World = null;
                    break;
            }

            return status;
        }

        /// <summary>Advances the autosave clock. Call once per frame on the server.</summary>
        public void Tick(float deltaSeconds)
        {
            if (World == null || _disposed)
                return;

            _secondsSinceSave += deltaSeconds;

            if (_secondsSinceSave < AutosaveIntervalSeconds)
                return;

            Save();
        }

        /// <summary>
        /// Writes immediately. Also called on player join, player leave, and graceful
        /// shutdown (TECH 6.4).
        /// </summary>
        public bool Save()
        {
            if (World == null)
                return false;

            _secondsSinceSave = 0f;
            World.SaveVersion++;

            byte[] bytes = SaveSerializer.Serialize(World);

            if (!_store.TryWrite(_fileName, bytes))
                return false;

            Saved?.Invoke(bytes);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // A clean shutdown should not cost the last 45 seconds.
            Save();
        }
    }
}
