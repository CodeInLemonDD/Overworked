using System.Collections.Generic;
using FishNet.Managing;
using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// The box a machine looks in, and who is allowed to be in it.
    /// </summary>
    /// <remarks>
    /// This is the third machine in the project that takes things in by watching a volume
    /// rather than by being handed them — the printer, the folder, and now the customer. The
    /// first two each wrote their own copy of this, and the copies had already drifted: one
    /// took the half-extents first and the centre second, one read the transform out of a
    /// field, and only one of them asked the same question about who counts.
    ///
    /// **What is shared is the judgement, not the flow.** Everything here answers "which
    /// objects count, and are they in the box". What a machine then *does* with them is
    /// entirely its own: the printer sorts them by payload and asks two slots, the folder
    /// takes anything that is a document, and a delivery zone will only want the one object
    /// it is being handed. None of that belongs here, and putting any of it here is how a
    /// shared helper turns into a shared machine.
    ///
    /// **The box is expressed in a transform's own space**, so it turns and moves with the
    /// object holding it. A station's root sits on the floor under its table, which is why
    /// every caller raises the centre above the pivot rather than centring it there — a box
    /// centred on a station's pivot swallows things at the player's feet and misses the
    /// machine.
    ///
    /// Stateless and static. There is nothing to keep between calls: the volume is authored
    /// on the machine that owns it, and the scan is rebuilt from the world every frame.
    /// </remarks>
    public static class IntakeVolume
    {
        /// <summary>
        /// Fills a buffer with the loose objects lying inside a box.
        /// </summary>
        /// <remarks>
        /// "Loose" is the first thing that narrows this, and it is the part that must not be
        /// re-derived by a caller: an object in someone's hands is never taken, which is what
        /// stops a machine grabbing something off a player who is merely walking past it. Those
        /// checks live in <see cref="GrabbableSpawner.CollectLooseGrabbables"/>, shared by every
        /// intake in the game, because the second machine to write its own copy is the one that
        /// forgets the check and the symptom is a document vanishing out of a player's hands.
        ///
        /// Order does not matter between the two steps — both are pure tests — but running the
        /// world-wide scan first means the box test only runs on objects that are actually loose,
        /// and the box test is the cheaper of the two.
        ///
        /// The buffer is cleared and refilled, and reused rather than allocated, so this is safe
        /// to call every frame from every machine on the server.
        /// </remarks>
        /// <param name="manager">The network manager the world is read from.</param>
        /// <param name="space">
        /// The transform the box is expressed in. Null yields an empty buffer rather than an
        /// exception: a machine whose box has not been wired yet should sit inert, not spam.
        /// </param>
        /// <param name="centre">Middle of the box, in <paramref name="space"/>'s local space.</param>
        /// <param name="halfExtents">Half the size of the box, in the same space.</param>
        /// <param name="buffer">Cleared, then filled with what is inside.</param>
        public static void CollectInside(
            NetworkManager manager,
            Transform space,
            Vector3 centre,
            Vector3 halfExtents,
            List<NetworkGrabbable> buffer)
        {
            GrabbableSpawner.CollectLooseGrabbables(manager, buffer);

            if (space == null)
            {
                buffer.Clear();
                return;
            }

            /* Backwards, so removing an element costs nothing and does not disturb the indices
             * still to be looked at. */
            for (int i = buffer.Count - 1; i >= 0; i--)
            {
                NetworkGrabbable grabbable = buffer[i];

                if (grabbable == null || !Contains(space, centre, halfExtents, grabbable.transform.position))
                    buffer.RemoveAt(i);
            }
        }

        /// <summary>
        /// True when a world position falls inside a box.
        /// </summary>
        /// <remarks>
        /// Measured in the given transform's own space, so a box turns with the machine rather
        /// than being a fixed patch of the floor.
        ///
        /// Public because it is the primitive the collection above is built from, and because a
        /// machine that has already narrowed its candidates by something cheaper — the folder
        /// only cares about documents, and there are usually two of them on the floor — should
        /// not have to walk the whole world to ask about one object.
        /// </remarks>
        /// <returns>False when there is no space to measure against.</returns>
        public static bool Contains(Transform space, Vector3 centre, Vector3 halfExtents, Vector3 worldPosition)
        {
            if (space == null)
                return false;

            Vector3 offset = space.InverseTransformPoint(worldPosition) - centre;

            return Mathf.Abs(offset.x) <= halfExtents.x
                && Mathf.Abs(offset.y) <= halfExtents.y
                && Mathf.Abs(offset.z) <= halfExtents.z;
        }
    }
}
