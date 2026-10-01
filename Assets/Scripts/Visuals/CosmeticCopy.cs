using UnityEngine;

namespace Overworked.Visuals
{
    /// <summary>
    /// Builds the pictures of objects that get drawn inside machines and on their fronts.
    /// </summary>
    /// <remarks>
    /// Three components draw a prefab somewhere it is not a real thing — the container view, the
    /// printer's pile and the badge on a box. Two of them had already grown their own copy of the
    /// same stripping pass, word for word, which is the point at which a third copy stops being
    /// duplication and starts being three places to fix.
    ///
    /// What lives here is only what was genuinely identical. Clearing a slot is not: a container
    /// view tracks its own visuals across rebuilds and the others clear a slot's children, so
    /// each keeps the version it needs.
    ///
    /// **A payload is always drawn at the size its own prefab was authored at.** The slot decides
    /// where it goes and which way it faces, and nothing else. That is not tidiness: the same
    /// payload is drawn in a hand, in a pile, on a lid and, later, in a folder, and the only
    /// thing that can keep those agreeing is the prefab. The alternative — letting whatever node
    /// a slot happens to hang under scale the copy — means a machine's art tree can quietly resize
    /// its own contents, and the printer's does: its pile slots sit under a table node squashed to
    /// eight tenths, under a container node squashed to a hundredth. Cancelling those by hand
    /// takes one number per squashed ancestor and nothing checks that the set is complete. It was
    /// not.
    /// </remarks>
    public static class CosmeticCopy
    {
        /// <summary>
        /// Instantiates a prefab into a slot at the slot's own origin, at the prefab's own size.
        /// </summary>
        /// <remarks>
        /// Does not clear the slot. Callers that may already have something there clear it first,
        /// and they do so after resolving the prefab — an index the catalogue cannot answer should
        /// leave the authored placeholder in place rather than empty the slot.
        /// </remarks>
        /// <returns>The copy, or null when there is nothing to instantiate.</returns>
        public static GameObject Instantiate(Transform slot, GameObject prefab)
        {
            if (slot == null || prefab == null)
                return null;

            GameObject copy = Object.Instantiate(prefab, slot);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = LocalScaleFor(slot, prefab);

            Strip(copy);
            return copy;
        }

        /// <summary>
        /// The local scale an object needs so it comes out at the size its prefab was authored at,
        /// wherever it has been put.
        /// </summary>
        /// <remarks>
        /// A parent's scale multiplies into everything below it, so undoing it is a division. The
        /// result is exact for a chain with no rotation on it, which is every slot in this project,
        /// and close enough elsewhere — a rotated and non-uniformly scaled parent is the one case
        /// where a scale cannot be expressed as a single vector at all, and Unity's own
        /// lossyScale is already an approximation there.
        ///
        /// The rotation is reset separately, so by the time this matters the copy's own
        /// contribution to any skew is gone.
        ///
        /// A zero on any axis of the inherited scale cannot be divided out — nothing drawn under
        /// such a node can have a size — so that axis is left at the prefab's own value rather
        /// than turned into an infinity.
        /// </remarks>
        public static Vector3 LocalScaleFor(Transform parent, GameObject prefab)
        {
            Vector3 wanted = prefab.transform.localScale;

            if (parent == null)
                return wanted;

            Vector3 inherited = parent.lossyScale;

            return new Vector3(
                Undo(inherited.x, wanted.x),
                Undo(inherited.y, wanted.y),
                Undo(inherited.z, wanted.z));
        }

        /// <summary>
        /// Divides the inherited scale back out of one axis, guarding the zero.
        /// </summary>
        private static float Undo(float inherited, float wanted) =>
            Mathf.Abs(inherited) < 1e-6f ? wanted : wanted / inherited;

        /// <summary>
        /// Empties a slot, so nothing it held answers a query for the rest of the frame.
        /// </summary>
        /// <remarks>
        /// Deactivated and detached before being destroyed, because Destroy is deferred to the
        /// end of the frame and a copy that is still parented and still enabled is still drawn
        /// and still answers queries for the rest of it. The collider sweep in <see cref="Strip"/>
        /// would also find it on the next pass and measure the wrong thing.
        /// </remarks>
        public static void Clear(Transform slot)
        {
            if (slot == null)
                return;

            for (int i = slot.childCount - 1; i >= 0; i--)
            {
                GameObject child = slot.GetChild(i).gameObject;
                child.SetActive(false);
                child.transform.SetParent(null, worldPositionStays: false);
                Object.Destroy(child);
            }
        }

        /// <summary>
        /// Removes everything that would let a drawn copy take part in the world.
        /// </summary>
        /// <remarks>
        /// These are pictures of objects. A live collider on one would be found by the player's
        /// pickup sector, by the placement probe and by the station scan, and a live Rigidbody
        /// would let a picture push the machine's own contents around.
        ///
        /// Colliders are disabled before being destroyed rather than only destroyed, for the same
        /// reason as <see cref="Clear"/>: Destroy is deferred, and a live collider answers queries
        /// during that window.
        /// </remarks>
        public static void Strip(GameObject visual)
        {
            if (visual == null)
                return;

            foreach (Collider collider in visual.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }

            foreach (Rigidbody body in visual.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                body.isKinematic = true;
                Object.Destroy(body);
            }
        }
    }
}
