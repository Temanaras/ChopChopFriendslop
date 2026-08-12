using System;
using MemoryPack;
using UnityEngine;

namespace ChopChop.Persistence
{
    public enum SaveLoadStatus : byte
    {
        Ok = 0,

        /// <summary>Payload was unreadable. Fall back to the backup copy.</summary>
        Corrupt = 1,

        /// <summary>Written by a newer build. Refuse rather than guess at the schema.</summary>
        TooNew = 2,

        /// <summary>
        /// Generation output has changed since this was written, so tree indices may no
        /// longer refer to the same trees. The caller decides whether to drop diffs or
        /// refuse the save (TECH 6.3).
        /// </summary>
        GenerationChanged = 3,
    }

    /// <summary>
    /// Binary serialization for saves (TECH 6.2). MemoryPack rather than JsonUtility or
    /// FishNet's serializer: snapshots are large, written every 45s, and sent over the
    /// network, so allocation and size both matter. A JSON export path can be added for
    /// debugging, but it must not become the primary format.
    /// </summary>
    public static class SaveSerializer
    {
        static SaveSerializer()
        {
            /* MemoryPack registers its Unity type formatters (Vector3, Quaternion, ...)
             * from a RuntimeInitializeOnLoadMethod, which only fires when entering play
             * mode. Edit-mode tests and any editor tooling would otherwise fail to
             * serialize a PlayerSave. Registration is idempotent, so calling it here as
             * well is safe and makes the dependency explicit. */
            MemoryPackUnityFormatterProviderInitializer.RegisterInitialFormatters();
        }

        /// <summary>Forces the static constructor to run. Call once at boot.</summary>
        public static void EnsureInitialized() { }

        public static byte[] Serialize(WorldSave save)
        {
            if (save == null)
                throw new ArgumentNullException(nameof(save));

            // Stamp the current versions rather than trusting whatever the instance
            // carried, so a loaded-and-upgraded save writes back as current.
            save.SaveFormatVersion = SaveFormat.Version;
            save.WorldGenVersion = SaveFormat.WorldGenVersion;

            return MemoryPackSerializer.Serialize(save);
        }

        public static byte[] Serialize(PlayerSave save)
        {
            if (save == null)
                throw new ArgumentNullException(nameof(save));

            return MemoryPackSerializer.Serialize(save);
        }

        /// <summary>
        /// Reads a world save, upgrading it if it was written by an older build.
        /// </summary>
        /// <remarks>
        /// A <see cref="SaveLoadStatus.GenerationChanged"/> result still returns the
        /// save — the diffs are readable, they just may not line up with regenerated
        /// trees. Deciding what to do about that is per-change and belongs to the
        /// caller; silently corrupting the world is not an option (TECH 6.3).
        /// </remarks>
        public static SaveLoadStatus TryDeserialize(byte[] bytes, out WorldSave save)
        {
            save = null;

            if (bytes == null || bytes.Length == 0)
                return SaveLoadStatus.Corrupt;

            try
            {
                save = MemoryPackSerializer.Deserialize<WorldSave>(bytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save] World save could not be read: {e.Message}");
                return SaveLoadStatus.Corrupt;
            }

            if (save == null)
                return SaveLoadStatus.Corrupt;

            if (save.SaveFormatVersion > SaveFormat.Version)
            {
                Debug.LogError(
                    $"[Save] Save format {save.SaveFormatVersion} is newer than this build " +
                    $"({SaveFormat.Version}). Refusing to load.");
                return SaveLoadStatus.TooNew;
            }

            if (save.SaveFormatVersion < SaveFormat.Version && !TryUpgrade(save))
                return SaveLoadStatus.Corrupt;

            if (save.WorldGenVersion != SaveFormat.WorldGenVersion)
            {
                Debug.LogWarning(
                    $"[Save] Save was generated with worldGen {save.WorldGenVersion}, this build " +
                    $"uses {SaveFormat.WorldGenVersion}. Tree indices may not correspond.");
                return SaveLoadStatus.GenerationChanged;
            }

            return SaveLoadStatus.Ok;
        }

        public static SaveLoadStatus TryDeserialize(byte[] bytes, out PlayerSave save)
        {
            save = null;

            if (bytes == null || bytes.Length == 0)
                return SaveLoadStatus.Corrupt;

            try
            {
                save = MemoryPackSerializer.Deserialize<PlayerSave>(bytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save] Player save could not be read: {e.Message}");
                return SaveLoadStatus.Corrupt;
            }

            return save == null ? SaveLoadStatus.Corrupt : SaveLoadStatus.Ok;
        }

        /// <summary>
        /// Walks a save forward one format version at a time. Each bump of
        /// <see cref="SaveFormat.Version"/> adds a case here; skipping versions is not
        /// supported, because sequential steps are what keep the upgrades reviewable.
        /// </summary>
        private static bool TryUpgrade(WorldSave save)
        {
            while (save.SaveFormatVersion < SaveFormat.Version)
            {
                switch (save.SaveFormatVersion)
                {
                    // case 1: UpgradeFrom1To2(save); break;

                    default:
                        Debug.LogError(
                            $"[Save] No upgrade step from save format {save.SaveFormatVersion}.");
                        return false;
                }
            }

            return true;
        }
    }
}
