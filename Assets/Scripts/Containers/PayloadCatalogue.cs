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
    /// </remarks>
    [CreateAssetMenu(fileName = "PayloadCatalogue", menuName = "Overworked/Payload Catalogue")]
    public class PayloadCatalogue : ScriptableObject
    {
        /// <summary>
        /// One prefab per payload index.
        /// </summary>
        [Tooltip("One prefab per payload index. Append; never reorder, or every index already in use changes meaning.")]
        [SerializeField]
        private GameObject[] _payloads;

        /// <summary>
        /// How many payloads are defined.
        /// </summary>
        public int Count => _payloads?.Length ?? 0;

        /// <summary>
        /// Returns the prefab for an index, or null when the index is out of range.
        /// </summary>
        /// <remarks>
        /// A prefab here must not carry a Rigidbody of its own. The grabbable shell already
        /// has one, and a second would make the object fall through its own collider.
        /// </remarks>
        public GameObject Get(int index)
        {
            if (_payloads == null)
                return null;
            if (index < 0 || index >= _payloads.Length)
                return null;

            return _payloads[index];
        }

        /// <summary>
        /// Returns true when an index resolves to a prefab.
        /// </summary>
        public bool TryGet(int index, out GameObject prefab)
        {
            prefab = Get(index);
            return prefab != null;
        }
    }
}
