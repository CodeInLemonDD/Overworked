namespace Overworked.Documents
{
    /// <summary>
    /// One document a computer is fetching, and the machine it is going to.
    /// </summary>
    /// <remarks>
    /// Public fields and nothing else, deliberately — the same rule as
    /// <see cref="DocumentRecord"/> and <see cref="Containers.ContainerEntry"/>. FishNet's weaver
    /// builds the serializer by walking this type's fields and **silently skips private ones**, so
    /// a [SerializeField] private field would compile, run, and never cross the wire.
    ///
    /// This is a job, not a document. Nothing in <see cref="DocumentStore"/> exists while one of
    /// these is in flight: the number is handed out when the fetch lands, not when it is ordered.
    /// That is what keeps a request that is never satisfied — a printer removed from the scene
    /// while the fetch was running — from burning a number that nothing can ever use.
    ///
    /// There is no id. A fetch is identified by the spec it is for, because a computer can only be
    /// waiting on one of each at a time.
    /// </remarks>
    [System.Serializable]
    public struct DocumentFetch
    {
        /// <summary>
        /// Index into the computer's <see cref="DocumentCatalogue"/>.
        /// </summary>
        /// <remarks>
        /// The index rather than the <see cref="DocumentCatalogue.Spec"/> itself. A spec is an
        /// asset-authoring detail, and copying one into a replicated struct would freeze the
        /// name and the duration at the moment the fetch started — so renaming a document or
        /// retuning its cost would apply to new fetches and not to running ones, for no gain.
        /// Every peer already holds the catalogue and can look the index up.
        /// </remarks>
        public int SpecIndex;

        /// <summary>
        /// Object id of the printer this document is being fetched for.
        /// </summary>
        /// <remarks>
        /// An object id rather than a reference, because a replicated struct cannot hold a
        /// reference. It is resolved on the server at the moment the fetch lands, which is also
        /// the moment the printer may have stopped existing — so the lookup is the check.
        ///
        /// Clients display it and nothing else: which machine a document is going to is the
        /// server's business until it arrives.
        /// </remarks>
        public int PrinterObjectId;

        /// <summary>
        /// Which team ordered it, or -1 for no colour.
        /// </summary>
        /// <remarks>
        /// Carried here rather than read off the player at the end, because there is no player at
        /// the end: a fetch outlives the press that started it, and whoever is standing at the
        /// machine when it lands need not be whoever ordered it — or connected at all. It becomes
        /// <see cref="DocumentRecord.Team"/> the moment the document exists.
        /// </remarks>
        public int Team;

        /// <summary>
        /// The server's clock reading at which this lands. **Clients must not read this.**
        /// </summary>
        /// <remarks>
        /// It is the server's <c>Time.timeAsDouble</c>, which has no meaning on a peer that
        /// started at a different moment. It is written exactly once, when the fetch begins, and
        /// never again — which is what keeps a running countdown off the wire entirely. A client
        /// draws its bar from its own clock and <c>Spec.FetchSeconds</c>.
        ///
        /// Kept here rather than in a list beside this one because two lists that have to stay
        /// index-aligned are two lists that can stop being index-aligned. One list that the server
        /// reads a deadline out of cannot drift from itself.
        /// </remarks>
        public float ServerReadyAt;
    }
}
