using System;
using System.Collections.Generic;
using ChopChop.Core;

namespace ChopChop.Networking
{
    public enum SegmentResult : byte
    {
        /// <summary>Stored; the transfer is still incomplete.</summary>
        Accepted = 0,

        /// <summary>Already had this one. Ignored.</summary>
        Duplicate = 1,

        /// <summary>Index or count made no sense. Ignored.</summary>
        Invalid = 2,

        /// <summary>Final segment arrived and the payload passed its hash check.</summary>
        Completed = 3,

        /// <summary>Complete but the payload hash did not match. Discarded.</summary>
        HashMismatch = 4,
    }

    /// <summary>
    /// Reassembles a payload from segments (TECH 6.5), with no transport attached.
    ///
    /// Kept separate from <see cref="SnapshotReceiver"/> so the offset arithmetic and
    /// the hash check can be tested without standing up a NetworkManager. This is the
    /// path a late joiner's whole world arrives through, and an off-by-one here would
    /// surface as a corrupt world rather than as an obvious network error.
    /// </summary>
    public sealed class SegmentAssembler
    {
        private sealed class Assembly
        {
            public byte[][] Segments;
            public int Received;
            public ulong Hash;
        }

        private readonly Dictionary<uint, Assembly> _inFlight = new();

        public int InFlightCount => _inFlight.Count;

        /// <summary>
        /// Adds one segment. <paramref name="payload"/> is set only on
        /// <see cref="SegmentResult.Completed"/>.
        /// </summary>
        public SegmentResult Add(uint transferId, ushort index, ushort count, ulong payloadHash,
            byte[] data, out byte[] payload)
        {
            payload = null;

            if (count == 0 || index >= count || data == null)
                return SegmentResult.Invalid;

            if (!_inFlight.TryGetValue(transferId, out Assembly assembly))
            {
                assembly = new Assembly
                {
                    Segments = new byte[count][],
                    Hash = payloadHash,
                };
                _inFlight[transferId] = assembly;
            }
            else if (assembly.Segments.Length != count)
            {
                // Two different transfers claiming one id; the sender is confused.
                return SegmentResult.Invalid;
            }

            if (assembly.Segments[index] != null)
                return SegmentResult.Duplicate;

            assembly.Segments[index] = data;
            assembly.Received++;

            if (assembly.Received < count)
                return SegmentResult.Accepted;

            _inFlight.Remove(transferId);

            byte[] assembled = Join(assembly.Segments);

            if (Fnv1a.Hash(assembled) != assembly.Hash)
                return SegmentResult.HashMismatch;

            payload = assembled;
            return SegmentResult.Completed;
        }

        /// <summary>Drops a partial transfer, e.g. when the sender disconnects.</summary>
        public void Abandon(uint transferId) => _inFlight.Remove(transferId);

        public void Clear() => _inFlight.Clear();

        private static byte[] Join(byte[][] segments)
        {
            int total = 0;
            for (int i = 0; i < segments.Length; i++)
                total += segments[i].Length;

            byte[] payload = new byte[total];
            int offset = 0;

            for (int i = 0; i < segments.Length; i++)
            {
                Buffer.BlockCopy(segments[i], 0, payload, offset, segments[i].Length);
                offset += segments[i].Length;
            }

            return payload;
        }
    }
}
