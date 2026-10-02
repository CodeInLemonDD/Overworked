using UnityEngine;

namespace Overworked.Containers
{
    /// <summary>
    /// The shared table that gives every payload index the same meaning on every peer.
    /// </summary>
    /// <remarks>
    /// A container only ever synchronises an integer, never a prefab reference. Both ends
    /// resolve that integer through this one asset, so they cannot disagree about what
    /// "payload 3" looks like — and a container can spawn a real object from an entry
    /// without knowing anything about where the prefabs live.
    ///
    /// The array order is the contract. Reordering it renames every object already in the
    /// world, so append rather than insert.
    ///
    /// The badge sits beside the payload rather than on the machine that shows it, because a
    /// badge is a picture of **this payload** — the flattened logo on a box says what comes out
    /// of the box, not which box it is. Kept here, a box works out which picture to show from
    /// the index it already has, so it cannot end up wearing another item's logo. That failure
    /// is silent: a box labelled paper that dispenses folders looks completely correct.
    /// </remarks>
    [CreateAssetMenu(fileName = "PayloadCatalogue", menuName = "Overworked/Payload Catalogue")]
    public class PayloadCatalogue : ScriptableObject
    {
        /// <summary>
        /// One payload, and the picture of it.
        /// </summary>
        /// <remarks>
        /// A struct rather than two parallel arrays. Two arrays would put the pairing in the
        /// position of each element, where nothing checks it and an edit to one is easy to make
        /// without the other. Here the two are one record, so they cannot drift apart.
        /// </remarks>
        [System.Serializable]
        public struct Entry
        {
            /// <summary>
            /// The object this index makes, when something spawns it.
            /// </summary>
            [Tooltip("The prefab for this payload index. No Rigidbody of its own — the grabbable shell already has one.")]
            public GameObject Payload;

            /// <summary>
            /// The flattened logo for this payload, or null when nothing needs one.
            /// </summary>
            /// <remarks>
            /// Optional, and empty is normal: only a payload that a box hands out has anything
            /// to put a logo on. A box whose payload has no badge shows an empty slot, which is
            /// not an error — the same rule <see cref="PayloadLabel"/> follows for text.
            /// </remarks>
            [Tooltip("Optional. The flattened logo shown on boxes that hand this payload out. Empty is fine.")]
            public GameObject Badge;

            /// <summary>
            /// True when this payload is something you can put documents into.
            /// </summary>
            /// <remarks>
            /// A folder, and whatever follows it. The flag lives here rather than on the folder's
            /// own component because the component is on the one object prefab every grabbable is
            /// made from — so it cannot know what it is looking at, and the payload index it reads
            /// at runtime is the only thing that can say.
            ///
            /// A fact about the payload, in the same way the badge is: what this index *is*,
            /// rather than where an instance of it happens to be.
            ///
            /// Not a count. A container with no way to take anything back out has to be unlimited,
            /// or a player can jam it with a legal action and never recover — see CONSTRAINTS.md.
            /// Folders have no take-out verb, so this is a yes or a no.
            /// </remarks>
            [Tooltip("True when this payload can hold documents. Folders only, for now.")]
            public bool IsContainer;
        }

        /// <summary>
        /// One entry per payload index.
        /// </summary>
        [Tooltip("One entry per payload index, in order. Append; never reorder, or every index already in use changes meaning.")]
        [SerializeField]
        private Entry[] _entries;

        /// <summary>
        /// How many payloads are defined.
        /// </summary>
        public int Count => _entries?.Length ?? 0;

        /// <summary>
        /// Returns the prefab for an index, or null when the index is out of range.
        /// </summary>
        /// <remarks>
        /// A prefab here must not carry a Rigidbody of its own. The grabbable shell already
        /// has one, and a second would make the object fall through its own collider.
        /// </remarks>
        public GameObject Get(int index) =>
            TryGetEntry(index, out Entry entry) ? entry.Payload : null;

        /// <summary>
        /// Returns true when an index resolves to a prefab.
        /// </summary>
        public bool TryGet(int index, out GameObject prefab)
        {
            prefab = Get(index);
            return prefab != null;
        }

        /// <summary>
        /// Returns the badge for an index, or null when there is none.
        /// </summary>
        /// <remarks>
        /// Null covers both "no such index" and "this payload has no badge", deliberately. The
        /// caller's answer to either is the same — draw nothing — and separating them would
        /// invite a caller to treat one as an error worth reporting.
        /// </remarks>
        public GameObject GetBadge(int index) =>
            TryGetEntry(index, out Entry entry) ? entry.Badge : null;

        /// <summary>
        /// Returns true when an index resolves to a badge.
        /// </summary>
        public bool TryGetBadge(int index, out GameObject badge)
        {
            badge = GetBadge(index);
            return badge != null;
        }

        /// <summary>
        /// Returns true when an index is something documents can be put into.
        /// </summary>
        /// <remarks>
        /// False covers both "no such index" and "this payload is not a container", deliberately.
        /// The caller's answer to either is the same — leave the object alone — and separating
        /// them would invite a caller to treat one as an error worth reporting. Same reasoning as
        /// <see cref="GetBadge"/>.
        /// </remarks>
        public bool IsContainer(int index) =>
            TryGetEntry(index, out Entry entry) && entry.IsContainer;

        /// <summary>
        /// Returns the whole entry for an index, or false when it is out of range.
        /// </summary>
        private bool TryGetEntry(int index, out Entry entry)
        {
            if (_entries == null || index < 0 || index >= _entries.Length)
            {
                entry = default;
                return false;
            }

            entry = _entries[index];
            return true;
        }
    }
}
