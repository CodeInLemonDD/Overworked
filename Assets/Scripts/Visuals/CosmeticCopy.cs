using UnityEngine;

namespace Overworked.Visuals
{
    /// <summary>
    /// Builds the pictures of objects that get drawn inside machines and on their fronts.
    /// </summary>
    /// <remarks>
    /// Three components now draw a prefab somewhere it is not a real thing — the container view,
    /// the printer's pile and the badge on a box. Two of them had already grown their own copy of
    /// the same stripping pass, word for word, which is the point at which a third copy stops
    /// being duplication and starts being three places to fix.
    ///
    /// What lives here is only what was genuinely identical. Clearing a slot is not: a container
    /// view tracks its own visuals across rebuilds and the others clear a slot's children, so
    /// each keeps the version it needs. It is here because two callers want exactly this and a
    /// third was about to.
    /// </remarks>
    public static class CosmeticCopy
    {
        /// <summary>
        /// Instantiates a prefab into a slot, sitting at the slot's own origin.
        /// </summary>
        /// <remarks>
        /// The local transform is reset rather than left to whatever the prefab was authored
        /// with: the slot decides where the copy goes, and a prefab carrying an offset would
        /// otherwise land somewhere different in every slot it was used in.
        ///
        /// Does not clear the slot. Callers that may already have something there clear it first,
        /// and they do so after resolving the prefab — an index the catalogue cannot answer
        /// should leave the authored placeholder in place rather than empty the slot.
        /// </remarks>
        /// <returns>The copy, or null when there is nothing to instantiate.</returns>
        public static GameObject Instantiate(Transform slot, GameObject prefab)
        {
            if (slot == null || prefab == null)
                return null;

            GameObject copy = Object.Instantiate(prefab, slot);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = Vector3.one;

            Strip(copy);
            return copy;
        }

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
