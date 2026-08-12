using System;
using System.IO;
using UnityEngine;

namespace ChopChop.Persistence
{
    /// <summary>
    /// Reads and writes save files on disk (TECH 6.4).
    ///
    /// Writes go to a temp file and are then swapped in, so a crash partway through a
    /// write cannot leave a truncated save behind — the old file stays intact until the
    /// new one is complete. The previous copy is kept as <c>.bak</c> and is what
    /// <see cref="TryRead"/> falls back to when the primary is unreadable.
    /// </summary>
    public sealed class SaveStore
    {
        public const string BackupExtension = ".bak";
        private const string TempExtension = ".tmp";

        private readonly string _directory;

        public SaveStore(string directory)
        {
            _directory = string.IsNullOrEmpty(directory)
                ? throw new ArgumentException("Save directory is required.", nameof(directory))
                : directory;
        }

        /// <summary>The normal location: a writable per-user path on every platform.</summary>
        public static SaveStore Default => new(Path.Combine(Application.persistentDataPath, "Saves"));

        public string PathFor(string fileName) => Path.Combine(_directory, fileName);

        public bool Exists(string fileName) => File.Exists(PathFor(fileName));

        /// <summary>
        /// Writes atomically. Returns false rather than throwing, since a failed
        /// autosave must not take the session down with it.
        /// </summary>
        public bool TryWrite(string fileName, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            string target = PathFor(fileName);
            string temp = target + TempExtension;
            string backup = target + BackupExtension;

            try
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllBytes(temp, data);

                if (File.Exists(target))
                {
                    // Replaces target with temp and rolls the old target into backup as
                    // one operation, so there is no window with no valid save present.
                    File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                }
                else
                {
                    // Nothing to replace or back up on a first write.
                    File.Move(temp, target);
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save] Failed writing {fileName}: {e.Message}");

                // Don't leave a half-written temp file to confuse the next write.
                TryDelete(temp);
                return false;
            }
        }

        /// <summary>
        /// Reads a save, falling back to the backup if the primary is missing or
        /// unreadable.
        /// </summary>
        /// <param name="usedBackup">True when the returned bytes came from the backup.</param>
        public bool TryRead(string fileName, out byte[] data, out bool usedBackup)
        {
            usedBackup = false;

            if (TryReadFile(PathFor(fileName), out data))
                return true;

            if (!TryReadFile(PathFor(fileName) + BackupExtension, out data))
                return false;

            Debug.LogWarning($"[Save] {fileName} was unreadable; recovered from backup.");
            usedBackup = true;
            return true;
        }

        private static bool TryReadFile(string path, out byte[] data)
        {
            data = null;

            if (!File.Exists(path))
                return false;

            try
            {
                data = File.ReadAllBytes(path);
                return data.Length > 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save] Failed reading {path}: {e.Message}");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Nothing useful to do; the next write overwrites it anyway.
            }
        }
    }
}
