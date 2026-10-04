using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// The sequence the customers ask in, from easy to hard.
    /// </summary>
    /// <remarks>
    /// **The Nth request of the round is tier N.** That is the whole contract, and it is why the
    /// asset is an ordered array rather than a table of weights or a pool to draw from: the
    /// round is meant to start gentle and get harder, and a random draw would put the two-contract
    /// ask first as often as last. A round is five minutes long, so a shape the player can feel
    /// arriving is worth more than variety.
    ///
    /// **Append only, never reorder** — the same rule <see cref="DocumentCatalogue"/> and
    /// <see cref="Containers.PayloadCatalogue"/> carry, and for the same reason: the index is the
    /// contract, and a reorder silently changes what every existing index means while nothing
    /// fails to compile.
    ///
    /// What a tier holds is **counts, not numbers**. "Contract ×2" means two more contracts, and
    /// which numbers they come out as is <see cref="DocumentStore"/>'s answer, read back after
    /// they are created. Writing "contract 1 and contract 2" here would be the asset choosing
    /// numbers, and the asset cannot see what has already been handed out.
    /// </remarks>
    [CreateAssetMenu(fileName = "RequestCatalogue", menuName = "Overworked/Request Catalogue")]
    public class RequestCatalogue : ScriptableObject
    {
        /// <summary>
        /// One kind of document a tier asks for, and how many of it.
        /// </summary>
        /// <remarks>
        /// A plain serializable struct rather than a nested ScriptableObject, the same shape
        /// <see cref="DocumentCatalogue.Spec"/> uses: these are authored in one array on one
        /// asset, and giving each row its own asset would mean managing a folder of them for no
        /// gain.
        ///
        /// This one **may** have private fields with [SerializeField] — it is never networked,
        /// so the weaver's field walk never sees it.
        /// </remarks>
        [System.Serializable]
        public struct RequestEntry
        {
            /// <summary>
            /// Which kind of document is wanted: an index into <see cref="DocumentCatalogue"/>.
            /// </summary>
            [Tooltip("Index into the document catalogue: which kind is wanted.")]
            public int SpecIndex;

            /// <summary>
            /// How many of that kind.
            /// </summary>
            /// <remarks>
            /// These are expanded into consecutive documents — a count of two means the next two
            /// the store hands out — rather than into a range of specific numbers.
            /// </remarks>
            [Tooltip("How many of that kind. Two means two more of them, not numbers 1 and 2.")]
            [Min(1)]
            public int Count;
        }

        /// <summary>
        /// Everything one customer asks for.
        /// </summary>
        [System.Serializable]
        public struct RequestTier
        {
            /// <summary>
            /// The kinds and counts this tier wants. Order is presentation only; the check does
            /// not care which order a folder's contents arrived in.
            /// </summary>
            [Tooltip("What this tier asks for. Several entries may name the same kind.")]
            public RequestEntry[] Wanted;
        }

        /// <summary>
        /// The tiers, in the order they are asked.
        /// </summary>
        [Tooltip("The tiers, in the order they are asked. Append only; the index is the contract.")]
        [SerializeField]
        private RequestTier[] _tiers;

        /// <summary>
        /// How many tiers are authored.
        /// </summary>
        public int Count => _tiers != null ? _tiers.Length : 0;

        /// <summary>
        /// Returns the tier a request should be written from.
        /// </summary>
        /// <remarks>
        /// **Out of range repeats the last tier, rather than failing.** That is a deliberate
        /// departure from the TryGet convention everywhere else in the project, and it is the
        /// right call here because of what the alternative looks like: the round is timed, the
        /// tiers are finite, and a long round against a short table would otherwise reach the end
        /// of the list and then have nothing to ask for. "The customers stopped wanting anything"
        /// is a worse ending than "the customers kept wanting the hard thing".
        ///
        /// False therefore means only one thing — the asset has no tiers authored at all — which
        /// is a wiring mistake rather than a state, and the caller has nothing to fall back on.
        /// </remarks>
        /// <param name="tier">The request index, counting from zero.</param>
        /// <param name="wanted">The tier to write the request from.</param>
        public bool TryGet(int tier, out RequestTier wanted)
        {
            wanted = default;

            if (_tiers == null || _tiers.Length == 0)
                return false;

            wanted = _tiers[Mathf.Clamp(tier, 0, _tiers.Length - 1)];
            return true;
        }
    }
}
