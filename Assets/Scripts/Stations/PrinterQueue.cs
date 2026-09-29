using System.Collections.Generic;
using Overworked.Containers;

namespace Overworked.Stations
{
    /// <summary>
    /// Decides whose job a printer runs next.
    /// </summary>
    /// <remarks>
    /// This is the whole of the printer's answer to griefing, and it is deliberately not a
    /// first-come queue. Every input entry carries the client that fed it, so the machine can
    /// tell two players' work apart and serves them in rotation:
    ///
    /// - one player with a whole job ready gets every cycle, so a solo machine loses nothing
    /// - two players ready alternate, so neither can starve the other
    ///
    /// Everything else follows from that. Stuffing your own jobs in only burns your own paper
    /// and only blocks your own work, so there is nothing in it for you; and keeping the
    /// machine busy is how you press the other player — by working, not by interfering. No
    /// anti-griefing rule has to be bolted on, because there is no grief left to answer.
    ///
    /// Server-only, and not a MonoBehaviour: it is per-machine server state with nothing in it
    /// for a designer to tune. A printer owns exactly one.
    /// </remarks>
    public sealed class PrinterQueue
    {
        /// <summary>
        /// Ceiling on how many distinct owners are considered in one turn.
        /// </summary>
        /// <remarks>
        /// The candidate list is bounded by the input container's capacity in practice, so this
        /// only stops an unlimited container from growing the buffer without bound.
        /// </remarks>
        private const int MaxTrackedOwners = 32;

        /// <summary>
        /// Owners holding a whole job this turn. Reused across selections.
        /// </summary>
        private readonly List<int> _readyOwners = new();

        /// <summary>
        /// Client id served last, or -1 before the first job.
        /// </summary>
        private int _lastServedClientId = -1;

        /// <summary>
        /// Client id served last, or -1 when no job has run yet.
        /// </summary>
        public int LastServedClientId => _lastServedClientId;

        /// <summary>
        /// Picks the owner whose job should run next.
        /// </summary>
        /// <param name="input">The printer's input container.</param>
        /// <param name="paperPayloadIndex">Payload index that counts as paper.</param>
        /// <param name="inkPayloadIndex">Payload index that counts as ink.</param>
        /// <returns>The chosen client id, or -1 when nobody has a whole job ready.</returns>
        public int SelectNextOwner(ContainerBase input, int paperPayloadIndex, int inkPayloadIndex)
        {
            if (input == null)
                return -1;

            _readyOwners.Clear();
            CollectOwners(input, paperPayloadIndex, inkPayloadIndex);

            if (_readyOwners.Count == 0)
                return -1;

            /* Strictly after the last id served, wrapping round. Ordering by id rather than by
             * position in the container is what makes the rotation stable: a job arriving in
             * the middle of the list must not change whose turn it is. */
            _readyOwners.Sort();

            int next = 0;
            for (int i = 0; i < _readyOwners.Count; i++)
            {
                if (_readyOwners[i] > _lastServedClientId)
                {
                    next = i;
                    break;
                }
            }

            _lastServedClientId = _readyOwners[next];
            return _lastServedClientId;
        }

        /// <summary>
        /// Server: removes one paper and one ink belonging to an owner.
        /// </summary>
        /// <remarks>
        /// All or nothing. A job that took the paper and then found the ink missing would burn
        /// an item for nothing, which is exactly the kind of loss a player cannot see coming.
        /// </remarks>
        /// <returns>False when the owner no longer has both; nothing is removed in that case.</returns>
        public bool TryConsumeJob(ContainerBase input, int ownerClientId, int paperPayloadIndex, int inkPayloadIndex)
        {
            if (input == null)
                return false;

            int paperIndex = -1;
            int inkIndex = -1;

            int count = input.Count;
            for (int i = 0; i < count; i++)
            {
                if (!input.TryGetEntry(i, out ContainerEntry entry))
                    continue;
                if (entry.OwnerClientId != ownerClientId)
                    continue;

                if (entry.Kind != (byte)ContainerEntryKind.Entity)
                    continue;

                /* One index used for both is a misconfiguration. It lands here as "no ink",
                 * which stalls the machine rather than eating pairs of paper. */
                if (entry.PayloadIndex == paperPayloadIndex)
                {
                    if (paperIndex < 0)
                        paperIndex = i;
                }
                else if (entry.PayloadIndex == inkPayloadIndex && inkIndex < 0)
                {
                    inkIndex = i;
                }
            }

            if (paperIndex < 0 || inkIndex < 0)
                return false;

            /* The later index goes first: removing the earlier one shifts everything after it
             * down, and the second removal would then take the wrong entry. */
            if (paperIndex > inkIndex)
            {
                input.ServerTryRemoveAt(paperIndex);
                input.ServerTryRemoveAt(inkIndex);
            }
            else
            {
                input.ServerTryRemoveAt(inkIndex);
                input.ServerTryRemoveAt(paperIndex);
            }

            return true;
        }

        /// <summary>
        /// Forgets the rotation.
        /// </summary>
        public void Reset()
        {
            _readyOwners.Clear();
            _lastServedClientId = -1;
        }

        /// <summary>
        /// Fills <see cref="_readyOwners"/> with the owners holding a whole job.
        /// </summary>
        private void CollectOwners(ContainerBase input, int paperPayloadIndex, int inkPayloadIndex)
        {
            int count = input.Count;

            for (int i = 0; i < count; i++)
            {
                if (!input.TryGetEntry(i, out ContainerEntry entry))
                    continue;
                if (entry.Kind != (byte)ContainerEntryKind.Entity)
                    continue;

                int owner = entry.OwnerClientId;
                if (_readyOwners.Contains(owner))
                    continue;
                if (_readyOwners.Count >= MaxTrackedOwners)
                    return;

                /* Only owners holding a complete pair are candidates. Someone with three sheets
                 * of paper and no ink is not waiting a turn — they have nothing to run, and
                 * counting them would stall the machine for everyone including themselves. */
                if (HasWholeJob(input, owner, paperPayloadIndex, inkPayloadIndex))
                    _readyOwners.Add(owner);
            }
        }

        /// <summary>
        /// True when this owner has at least one paper and at least one ink in the container.
        /// </summary>
        private static bool HasWholeJob(ContainerBase input, int ownerClientId, int paperPayloadIndex, int inkPayloadIndex)
        {
            bool paper = false;
            bool ink = false;

            int count = input.Count;
            for (int i = 0; i < count; i++)
            {
                if (!input.TryGetEntry(i, out ContainerEntry entry))
                    continue;
                if (entry.OwnerClientId != ownerClientId)
                    continue;
                if (entry.Kind != (byte)ContainerEntryKind.Entity)
                    continue;

                if (entry.PayloadIndex == paperPayloadIndex)
                    paper = true;
                else if (entry.PayloadIndex == inkPayloadIndex)
                    ink = true;

                if (paper && ink)
                    return true;
            }

            return false;
        }
    }
}
