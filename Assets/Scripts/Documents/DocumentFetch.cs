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
    /// This is a job, not a document. The document already exists — it was named by whoever asked
    /// for it, and it is in <see cref="DocumentStore"/> — and this is the wait before it can be
    /// printed. Nothing is created when the wait ends; an entry is added to a queue.
    ///
    /// There is no id. A fetch is identified by the document it is for, because a computer can
    /// only be waiting on the same document once.
    /// </remarks>
    [System.Serializable]
    public struct DocumentFetch
    {
        /// <summary>
        /// Id of the document being fetched, into <see cref="DocumentStore"/>.
        /// </summary>
        /// <remarks>
        /// An id rather than the kind or the appearance. The document is a specific one — this
        /// spreadsheet, not spreadsheets in general — and every peer already holds the store the
        /// id resolves through.
        ///
        /// It is also what the fetch lands as: the queue entry is written from this id, so a
        /// document cannot be fetched as one thing and filed as another.
        /// </remarks>
        public int DocumentId;

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
        /// The server's clock reading at which this lands. **Clients must not read this.**
        /// </summary>
        /// <remarks>
        /// It is the server's <c>Time.timeAsDouble</c>, which has no meaning on a peer that
        /// started at a different moment. It is written exactly once, when the fetch begins, and
        /// never again — which is what keeps a running countdown off the wire entirely. A client
        /// draws its bar from its own clock and the kind's <c>FetchSeconds</c>.
        ///
        /// Kept here rather than in a list beside this one because two lists that have to stay
        /// index-aligned are two lists that can stop being index-aligned. One list that the server
        /// reads a deadline out of cannot drift from itself.
        /// </remarks>
        public float ServerReadyAt;
    }
}
