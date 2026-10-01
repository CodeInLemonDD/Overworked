using Overworked.Containers;
using Overworked.Stations;
using Overworked.Visuals;
using UnityEngine;

namespace Overworked.UI
{
    /// <summary>
    /// Draws the flattened logo of whatever a box hands out, so a player can tell one box from
    /// another without walking to it and pressing E.
    /// </summary>
    /// <remarks>
    /// **Which** logo is not a field here, and that is the whole point. A badge is a picture of a
    /// payload, so it is looked up from the payload index the box already carries. An index that
    /// is wrong is then wrong in one place, and a box cannot end up wearing another item's logo —
    /// a failure that would look entirely correct on screen and be found only by a player who
    /// opened the wrong box.
    ///
    /// The copy is built once. The payload index is a serialized field rather than a replicated
    /// one, so it cannot change after the prefab is instantiated, and there is nothing to follow.
    ///
    /// Cosmetic only: no collider, no rigidbody, nothing replicated. It is a picture on a lid, and
    /// it is built locally by each peer from data it already has.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SupplyBox))]
    public class BoxBadge : MonoBehaviour
    {
        /// <summary>
        /// Where the logo goes.
        /// </summary>
        /// <remarks>
        /// A child of the box, not the box itself — the slot is emptied before the logo is put
        /// in, and pointing it at the root would take the machine with it. Checked below rather
        /// than left to be discovered.
        /// </remarks>
        [Tooltip("The child node the logo is placed on. Must not be the box's own root — the slot is emptied before the logo is drawn.")]
        [SerializeField]
        private Transform _slot;

        /// <summary>
        /// Where the badges come from. Must be the same catalogue the box and the grabbables use.
        /// </summary>
        [Tooltip("Must be the same catalogue the box hands out from.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        private void Start()
        {
            if (_catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(BoxBadge)} on {gameObject.name} has no catalogue assigned, so it will draw nothing.",
                    this);
                return;
            }

            if (_slot == null)
            {
                Debug.LogError(
                    $"{nameof(BoxBadge)} on {gameObject.name} has no slot assigned, so it will draw nothing.",
                    this);
                return;
            }

            if (_slot == transform)
            {
                Debug.LogError(
                    $"{nameof(BoxBadge)} on {gameObject.name} has its own root as the slot. The slot is emptied before the logo is drawn, so this would delete the box. Point it at a child.",
                    this);
                return;
            }

            SupplyBox box = GetComponent<SupplyBox>();
            if (box == null)
                return;

            GameObject badge = _catalogue.GetBadge(box.PayloadIndex);

            /* Not an error. Only the payloads a box hands out have a badge, and a box pointed at
             * one that has none is simply a box with a plain top. Same rule PayloadLabel follows
             * for payloads with no text: skip it, do not complain. */
            if (badge == null)
                return;

            /* Cleared only once the badge is in hand, so a payload with no badge leaves whatever
             * was authored here rather than emptying the slot on the way to doing nothing. */
            CosmeticCopy.Clear(_slot);
            CosmeticCopy.Instantiate(_slot, badge);
        }
    }
}
