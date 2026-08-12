using System;
using System.Collections.Generic;
using ChopChop.Core;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace ChopChop.Networking
{
    /// <summary>
    /// One slice of a larger payload (TECH 6.5).
    /// </summary>
    /// <remarks>
    /// A broadcast rather than an RPC, because this is systems-level messaging with no
    /// NetworkObject behind it (TECH 17).
    /// </remarks>
    public struct SnapshotSegmentBroadcast : IBroadcast
    {
        public uint TransferId;
        public ushort SegmentIndex;
        public ushort SegmentCount;

        /// <summary>Hash of the whole reassembled payload, repeated on every segment.</summary>
        public ulong PayloadHash;

        public byte[] Data;
    }

    /// <summary>
    /// Splits a payload across several ticks and sends it to a client (TECH 6.5).
    ///
    /// A full world snapshot will not fit in one message once players have explored, and
    /// dumping it into the transport in one go would stall gameplay while it drains.
    /// This is background traffic, so it is metered: a fixed number of segments per
    /// tick, reliable channel, in order.
    ///
    /// The same mechanism serves late join (TECH 8.4) and host migration (TECH 8.2) —
    /// built once, deliberately.
    /// </summary>
    public sealed class SnapshotSender : IDisposable
    {
        /// <summary>
        /// TECH 6.5. Server to client only: FishNet splits oversized messages by itself
        /// on the way down, but a client sending upward is capped by
        /// TransportManager.MaximumClientPacketSize (20480 by default), so a
        /// client-to-server transfer would need this lowered.
        /// </summary>
        public const int SegmentSize = 32 * 1024;

        private sealed class Transfer
        {
            public uint Id;
            public NetworkConnection Target;
            public byte[] Payload;
            public ulong Hash;
            public ushort SegmentCount;
            public ushort NextSegment;
        }

        private readonly NetworkManager _networkManager;
        private readonly Queue<Transfer> _queue = new();
        private readonly int _segmentsPerTick;

        private uint _nextTransferId = 1;
        private bool _subscribed;

        /// <param name="segmentsPerTick">
        /// How much of the transfer to release each tick. Higher finishes sooner and
        /// competes harder with gameplay traffic for bandwidth.
        /// </param>
        public SnapshotSender(NetworkManager networkManager, int segmentsPerTick = 2)
        {
            _networkManager = networkManager
                ? networkManager
                : throw new ArgumentNullException(nameof(networkManager));

            _segmentsPerTick = Mathf.Max(1, segmentsPerTick);

            _networkManager.TimeManager.OnTick += HandleTick;
            _subscribed = true;
        }

        public void Dispose()
        {
            if (!_subscribed)
                return;

            _subscribed = false;
            _networkManager.TimeManager.OnTick -= HandleTick;
            _queue.Clear();
        }

        /// <summary>True while any transfer is still draining.</summary>
        public bool IsSending => _queue.Count > 0;

        /// <summary>
        /// Queues a payload for a single client. Returns the transfer id, or 0 if the
        /// payload was empty.
        /// </summary>
        public uint Send(NetworkConnection target, byte[] payload)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (payload == null || payload.Length == 0)
                return 0;

            int segmentCount = (payload.Length + SegmentSize - 1) / SegmentSize;

            if (segmentCount > ushort.MaxValue)
            {
                Debug.LogError($"[Snapshot] Payload of {payload.Length} bytes needs too many segments.");
                return 0;
            }

            Transfer transfer = new()
            {
                Id = _nextTransferId++,
                Target = target,
                Payload = payload,
                Hash = Fnv1a.Hash(payload),
                SegmentCount = (ushort)segmentCount,
                NextSegment = 0,
            };

            _queue.Enqueue(transfer);
            return transfer.Id;
        }

        private void HandleTick()
        {
            if (!_networkManager.IsServerStarted)
                return;

            int budget = _segmentsPerTick;

            while (budget > 0 && _queue.Count > 0)
            {
                Transfer transfer = _queue.Peek();

                // The client left mid-transfer; drop the rest rather than sending into
                // a dead connection every tick.
                if (transfer.Target == null || !transfer.Target.IsActive)
                {
                    _queue.Dequeue();
                    continue;
                }

                SendSegment(transfer);
                budget--;

                if (transfer.NextSegment >= transfer.SegmentCount)
                    _queue.Dequeue();
            }
        }

        private void SendSegment(Transfer transfer)
        {
            int start = transfer.NextSegment * SegmentSize;
            int length = Mathf.Min(SegmentSize, transfer.Payload.Length - start);

            byte[] slice = new byte[length];
            Buffer.BlockCopy(transfer.Payload, start, slice, 0, length);

            SnapshotSegmentBroadcast message = new()
            {
                TransferId = transfer.Id,
                SegmentIndex = transfer.NextSegment,
                SegmentCount = transfer.SegmentCount,
                PayloadHash = transfer.Hash,
                Data = slice,
            };

            _networkManager.ServerManager.Broadcast(transfer.Target, message, true, Channel.Reliable);
            transfer.NextSegment++;
        }
    }

    /// <summary>
    /// Reassembles segmented payloads on the receiving client (TECH 6.5).
    /// </summary>
    public sealed class SnapshotReceiver : IDisposable
    {
        private readonly NetworkManager _networkManager;
        private readonly SegmentAssembler _assembler = new();
        private bool _subscribed;

        /// <summary>Raised once a payload has arrived complete and passed its hash check.</summary>
        public event Action<byte[]> Completed;

        public SnapshotReceiver(NetworkManager networkManager)
        {
            _networkManager = networkManager
                ? networkManager
                : throw new ArgumentNullException(nameof(networkManager));

            _networkManager.ClientManager.RegisterBroadcast<SnapshotSegmentBroadcast>(HandleSegment);
            _subscribed = true;
        }

        public void Dispose()
        {
            if (!_subscribed)
                return;

            _subscribed = false;
            _networkManager.ClientManager.UnregisterBroadcast<SnapshotSegmentBroadcast>(HandleSegment);
            _assembler.Clear();
        }

        private void HandleSegment(SnapshotSegmentBroadcast message, Channel channel)
        {
            SegmentResult result = _assembler.Add(message.TransferId, message.SegmentIndex,
                message.SegmentCount, message.PayloadHash, message.Data, out byte[] payload);

            switch (result)
            {
                case SegmentResult.Completed:
                    Completed?.Invoke(payload);
                    break;

                case SegmentResult.HashMismatch:
                    // Better to drop it than to hand a corrupted world to the loader.
                    Debug.LogError($"[Snapshot] Transfer {message.TransferId} failed its hash check. Discarded.");
                    break;

                case SegmentResult.Invalid:
                    Debug.LogError(
                        $"[Snapshot] Segment {message.SegmentIndex}/{message.SegmentCount} " +
                        $"of transfer {message.TransferId} was rejected.");
                    break;
            }
        }
    }
}
