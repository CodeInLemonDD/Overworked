using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// The documents a player can ask the computer for.
    /// </summary>
    /// <remarks>
    /// A catalogue of **specifications**, not of documents that exist. Nothing here is created
    /// when the asset is loaded; picking an entry is what makes a
    /// <see cref="DocumentRecord"/> and gives it a number. That split is what keeps the
    /// unbounded axis — the number — out of the asset: an asset whose entries multiplied every
    /// round would be a prefab per document all over again, which is the thing the payload
    /// split was made to avoid.
    ///
    /// The array is keyed by index for the same reason <see cref="Containers.PayloadCatalogue"/>
    /// is: the index is what a computer panel sends and what a console command types, so it has
    /// to mean one thing everywhere. **Append only, never reorder** — a reorder silently changes
    /// what every existing index means, and nothing will fail to compile.
    /// </remarks>
    [CreateAssetMenu(fileName = "DocumentCatalogue", menuName = "Overworked/Document Catalogue")]
    public class DocumentCatalogue : ScriptableObject
    {
        /// <summary>
        /// One kind of document the computer offers.
        /// </summary>
        /// <remarks>
        /// A plain serializable struct rather than a nested ScriptableObject: these are authored
        /// in one array on one asset, and giving each row its own asset would mean managing a
        /// folder of them for no gain.
        ///
        /// This one **may** have private fields with [SerializeField] — it is never networked,
        /// unlike <see cref="DocumentRecord"/>, so the weaver's field walk never sees it.
        /// </remarks>
        [System.Serializable]
        public struct Spec
        {
            /// <summary>
            /// What the computer calls this, in the player's language.
            /// </summary>
            [Tooltip("What the computer panel calls this. Shown to the player.")]
            public string DisplayName;

            /// <summary>
            /// Which payload this prints as.
            /// </summary>
            /// <remarks>
            /// An index into the payload catalogue. The two catalogues are keyed differently on
            /// purpose: several specs may share one payload (a contract and a report could both
            /// be a sheet of paper), and which payload a spec prints as is a presentation
            /// decision that belongs here.
            /// </remarks>
            [Tooltip("Index into the payload catalogue: what this prints as.")]
            public int PayloadIndex;

            /// <summary>
            /// A <see cref="DocumentSource"/>, kept as an int.
            /// </summary>
            [Tooltip("0 = the company's own files, 1 = fetched over the Internet, 2 = somebody else has it.")]
            public int Source;

            /// <summary>
            /// Whether a sheet of this kind is worthless until it has been stamped.
            /// </summary>
            /// <remarks>
            /// **This is a flag on the kind rather than a second kind.** A contract and a contract
            /// with a stamp on it are the same document — same record, same number, same team — and
            /// the stamp is a state that one sheet is in, not a different thing to be. Modelling it
            /// as a second spec would mean two catalogue rows that have to be kept in step, and a
            /// delivery rule that has to know which of the two to look for.
            ///
            /// What the flag buys is that the stamp stops being a rule anybody has to remember:
            /// the printer is what sets a sheet's stamped flag, from this, at the moment it makes
            /// one, and everything downstream reads the flag on the sheet rather than looking the
            /// kind up again. See <see cref="Interaction.NetworkGrabbable.IsStamped"/>.
            /// </remarks>
            [Tooltip("Tick for a kind that is worthless until it has been stamped, such as a contract.")]
            public bool NeedsStamp;

            /// <summary>
            /// How long this takes to obtain, in seconds.
            /// </summary>
            /// <remarks>
            /// The cost of "data acquisition", and the only place the two sources differ in
            /// behaviour rather than in labelling. Zero is the right value for a filing-cabinet
            /// document: it is already there.
            ///
            /// Read by <see cref="Stations.Computer"/>, which holds the document for this long
            /// before filing it into the printer's queue. The wait belongs to the machine rather
            /// than to the player, so walking away does not cancel it.
            /// </remarks>
            [Tooltip("Seconds before this can be printed. 0 for the company's own files.")]
            [Min(0f)]
            public float FetchSeconds;

            /// <summary>
            /// Extra seconds each of these adds to a request's clock, per document.
            /// </summary>
            /// <remarks>
            /// **A paper's worth is not one thing: it is how much walking it costs.** A contract
            /// costs a trip to the stamp desk, a spreadsheet costs a trade with whoever is holding
            /// it, an image or an article costs the wait on the computer — so a request for three
            /// spreadsheets is not the same job as a request for three contracts, and one clock for
            /// every request makes the big ones impossible rather than hard.
            ///
            /// This is what stops that clock being a guess made in advance by whoever wrote the
            /// tier table. The tiers already say what a request contains; this says what each thing
            /// in it costs, and the two together are the clock. Authoring a new kind therefore means
            /// answering this at the same time, and forgetting to leaves it free — which is the
            /// right default for a thing that turns up on a shelf.
            ///
            /// Read by <see cref="Npc.Customer"/>, added up over the request's rows.
            /// </remarks>
            [Tooltip("Extra seconds on the job clock for each of these a request asks for.")]
            [Min(0f)]
            public float TimeBonusSeconds;
        }

        /// <summary>
        /// The specs, in the order the panel lists them.
        /// </summary>
        [Tooltip("The documents the computer offers, in the order it lists them. Append only; the index is the contract.")]
        [SerializeField]
        private Spec[] _specs;

        /// <summary>
        /// How many specs there are.
        /// </summary>
        public int Count => _specs != null ? _specs.Length : 0;

        /// <summary>
        /// Returns the spec at an index, or false when there is none.
        /// </summary>
        public bool TryGet(int index, out Spec spec)
        {
            if (_specs == null || index < 0 || index >= _specs.Length)
            {
                spec = default;
                return false;
            }

            spec = _specs[index];
            return true;
        }

        /// <summary>
        /// Returns the spec at an index, or a zeroed one when there is none.
        /// </summary>
        /// <remarks>
        /// For callers that have already established the index is good. Anything reading an
        /// index that came from off the wire should use <see cref="TryGet"/> instead — a zeroed
        /// spec is a real-looking document pointing at payload 0.
        /// </remarks>
        public Spec Get(int index) => TryGet(index, out Spec spec) ? spec : default;
    }
}
