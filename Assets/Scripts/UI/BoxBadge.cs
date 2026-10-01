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
    ///
    /// **Deliberately not `[RequireComponent(typeof(SupplyBox))]`.** That attribute reads as a
    /// tidy way to say "this needs a box", and it does the opposite of tidy: attaching this to any
    /// object that has no box makes Unity silently add a second one, on that object, with nothing
    /// assigned. Two boxes in one hierarchy then share a container and restock it twice as fast,
    /// and which of them answers a press is whichever the search happens to reach first. The check
    /// below reports the mistake instead of creating it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class BoxBadge : MonoBehaviour
    {
        /// <summary>
        /// Where the logo goes.
        /// </summary>
        /// <remarks>
        /// A node that is not the box's own root — the slot is emptied before the logo is put in,
        /// so pointing it at the root would take the machine with it. Checked below rather than
        /// left to be discovered.
        ///
        /// This may be the node the badge component itself sits on, which is the tidiest place to
        /// put it: an empty child of the box, with the logo drawn into it.
        /// </remarks>
        [Tooltip("The node the logo is placed on. Must not be the box's own root — the slot is emptied before the logo is drawn.")]
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
            /* Searched upwards, so this component can sit on the logo's own node rather than
             * having to be on the box beside the SupplyBox. */
            SupplyBox box = GetComponentInParent<SupplyBox>();
            if (box == null)
            {
                Debug.LogError(
                    $"{nameof(BoxBadge)} on {gameObject.name} found no {nameof(SupplyBox)} above it, so it has no payload to draw a logo for.",
                    this);
                return;
            }

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

            if (_slot == box.transform)
            {
                Debug.LogError(
                    $"{nameof(BoxBadge)} on {gameObject.name} has the box's own root as its slot. The slot is emptied before the logo is drawn, so this would delete the box. Point it at a child.",
                    this);
                return;
            }

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
