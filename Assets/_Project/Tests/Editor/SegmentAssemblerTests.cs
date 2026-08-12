using System;
using ChopChop.Core;
using ChopChop.Networking;
using NUnit.Framework;

namespace ChopChop.Tests.Editor
{
    /// <summary>
    /// The chunked transfer (TECH 6.5) carries a late joiner's entire world, and later
    /// the host migration payload. A reassembly bug here reads as a corrupt world rather
    /// than as a network error, so it is worth pinning down away from the transport.
    /// </summary>
    public sealed class SegmentAssemblerTests
    {
        private const int SegmentSize = 1024;

        private static byte[] MakePayload(int length)
        {
            byte[] payload = new byte[length];

            // Position-dependent so a mis-ordered or mis-offset join cannot pass.
            for (int i = 0; i < length; i++)
                payload[i] = (byte)(i * 31 + (i >> 8));

            return payload;
        }

        private static byte[][] Split(byte[] payload, int segmentSize)
        {
            int count = (payload.Length + segmentSize - 1) / segmentSize;
            byte[][] segments = new byte[count][];

            for (int i = 0; i < count; i++)
            {
                int start = i * segmentSize;
                int length = Math.Min(segmentSize, payload.Length - start);
                segments[i] = new byte[length];
                Buffer.BlockCopy(payload, start, segments[i], 0, length);
            }

            return segments;
        }

        [Test]
        public void Reassembles_PayloadWithPartialFinalSegment()
        {
            // Deliberately not a multiple of the segment size.
            byte[] payload = MakePayload(SegmentSize * 3 + 137);
            byte[][] segments = Split(payload, SegmentSize);
            ulong hash = Fnv1a.Hash(payload);

            SegmentAssembler assembler = new();
            byte[] assembled = null;

            for (int i = 0; i < segments.Length; i++)
            {
                SegmentResult result = assembler.Add(1, (ushort)i, (ushort)segments.Length, hash,
                    segments[i], out byte[] output);

                if (i < segments.Length - 1)
                {
                    Assert.AreEqual(SegmentResult.Accepted, result, $"segment {i}");
                    Assert.IsNull(output);
                }
                else
                {
                    Assert.AreEqual(SegmentResult.Completed, result, "final segment");
                    assembled = output;
                }
            }

            Assert.IsNotNull(assembled);
            CollectionAssert.AreEqual(payload, assembled);
            Assert.AreEqual(0, assembler.InFlightCount, "completed transfer should be released");
        }

        [Test]
        public void Reassembles_WhenSegmentsArriveOutOfOrder()
        {
            byte[] payload = MakePayload(SegmentSize * 4);
            byte[][] segments = Split(payload, SegmentSize);
            ulong hash = Fnv1a.Hash(payload);

            SegmentAssembler assembler = new();
            int[] order = { 3, 0, 2, 1 };
            byte[] assembled = null;

            foreach (int i in order)
            {
                assembler.Add(1, (ushort)i, (ushort)segments.Length, hash, segments[i], out byte[] output);
                if (output != null)
                    assembled = output;
            }

            Assert.IsNotNull(assembled, "transfer never completed");
            CollectionAssert.AreEqual(payload, assembled);
        }

        [Test]
        public void Interleaved_TransfersDoNotContaminateEachOther()
        {
            byte[] a = MakePayload(SegmentSize * 2);
            byte[] b = MakePayload(SegmentSize + 11);

            byte[][] segmentsA = Split(a, SegmentSize);
            byte[][] segmentsB = Split(b, SegmentSize);
            ulong hashA = Fnv1a.Hash(a);
            ulong hashB = Fnv1a.Hash(b);

            SegmentAssembler assembler = new();
            byte[] doneA = null, doneB = null;

            assembler.Add(1, 0, (ushort)segmentsA.Length, hashA, segmentsA[0], out _);
            assembler.Add(2, 0, (ushort)segmentsB.Length, hashB, segmentsB[0], out _);
            assembler.Add(2, 1, (ushort)segmentsB.Length, hashB, segmentsB[1], out doneB);
            assembler.Add(1, 1, (ushort)segmentsA.Length, hashA, segmentsA[1], out doneA);

            CollectionAssert.AreEqual(a, doneA);
            CollectionAssert.AreEqual(b, doneB);
        }

        [Test]
        public void Duplicate_SegmentIsIgnored()
        {
            byte[] payload = MakePayload(SegmentSize * 2);
            byte[][] segments = Split(payload, SegmentSize);
            ulong hash = Fnv1a.Hash(payload);

            SegmentAssembler assembler = new();

            Assert.AreEqual(SegmentResult.Accepted, assembler.Add(1, 0, 2, hash, segments[0], out _));
            Assert.AreEqual(SegmentResult.Duplicate, assembler.Add(1, 0, 2, hash, segments[0], out _));
            Assert.AreEqual(SegmentResult.Completed, assembler.Add(1, 1, 2, hash, segments[1], out byte[] done));

            CollectionAssert.AreEqual(payload, done);
        }

        [Test]
        public void CorruptedPayload_IsRejectedByHash()
        {
            byte[] payload = MakePayload(SegmentSize * 2);
            byte[][] segments = Split(payload, SegmentSize);
            ulong hash = Fnv1a.Hash(payload);

            segments[1][0] ^= 0xFF; // one flipped byte in transit

            SegmentAssembler assembler = new();
            assembler.Add(1, 0, 2, hash, segments[0], out _);

            Assert.AreEqual(SegmentResult.HashMismatch,
                assembler.Add(1, 1, 2, hash, segments[1], out byte[] output));
            Assert.IsNull(output, "a failed payload must never be handed on");
        }

        [Test]
        public void OutOfRangeSegment_IsRejected()
        {
            SegmentAssembler assembler = new();

            Assert.AreEqual(SegmentResult.Invalid, assembler.Add(1, 0, 0, 0, new byte[4], out _), "zero count");
            Assert.AreEqual(SegmentResult.Invalid, assembler.Add(1, 5, 2, 0, new byte[4], out _), "index past count");
            Assert.AreEqual(SegmentResult.Invalid, assembler.Add(1, 0, 2, 0, null, out _), "null data");
            Assert.AreEqual(0, assembler.InFlightCount, "nothing should have been retained");
        }

        [Test]
        public void SingleSegmentPayload_CompletesImmediately()
        {
            byte[] payload = MakePayload(64);

            SegmentAssembler assembler = new();
            SegmentResult result = assembler.Add(7, 0, 1, Fnv1a.Hash(payload), payload, out byte[] output);

            Assert.AreEqual(SegmentResult.Completed, result);
            CollectionAssert.AreEqual(payload, output);
        }

        [Test]
        public void Fnv1a_IsStableAndSensitive()
        {
            byte[] payload = MakePayload(1000);
            byte[] copy = (byte[])payload.Clone();

            Assert.AreEqual(Fnv1a.Hash(payload), Fnv1a.Hash(copy), "same bytes, same hash");

            copy[500] ^= 0x01;
            Assert.AreNotEqual(Fnv1a.Hash(payload), Fnv1a.Hash(copy), "one flipped bit must change the hash");
        }
    }
}
