namespace Overworked.Documents
{
    /// <summary>
    /// Where a document came from.
    /// </summary>
    /// <remarks>
    /// Stored as an int on <see cref="DocumentCatalogue.Spec"/> rather than as this type, for the
    /// same reason <see cref="Containers.ContainerEntry.Kind"/> is a byte: the serializer walks
    /// fields, and a field declared as an enum is one more thing whose width has to be reasoned
    /// about. The enum exists so call sites read as words.
    /// </remarks>
    public enum DocumentSource
    {
        /// <summary>
        /// The company's own files. Available at the computer immediately.
        /// </summary>
        Filing = 0,

        /// <summary>
        /// Fetched over the Internet. Costs <c>FetchSeconds</c> before it can be printed.
        /// </summary>
        Internet = 1,
    }

    /// <summary>
    /// One document that has been named in this round.
    /// </summary>
    /// <remarks>
    /// Public fields and nothing else, deliberately — the same rule as
    /// <see cref="Containers.ContainerEntry"/>. FishNet's weaver builds the serializer by
    /// walking this type's fields and **silently skips private ones**, so a
    /// [SerializeField] private field would compile, run, and never cross the wire. It also
    /// picks up any public property with both a public getter and a public setter, so adding
    /// one would quietly widen the format. Read only through the public fields.
    ///
    /// Keep it a struct with no base type: the writer walks every field it can find while the
    /// reader skips types from UnityEngine.* assemblies, and a class inheriting one of those
    /// would desync between the two.
    ///
    /// **It holds a kind, not an appearance.** Three fields, and each is the smallest thing that
    /// answers a question nothing else can:
    ///
    /// - <see cref="SpecIndex"/> rather than a payload index, because several specs share one
    ///   payload — a contract and a report are both a sheet of paper — so the appearance cannot
    ///   say which kind a document is, and the panel groups by kind.
    /// - <see cref="Number"/>, assigned per kind *and per team*, so that one team's Excel 1 and
    ///   the other team's Excel 1 are two documents that happen to share a name.
    /// - <see cref="Team"/>, because the two teams make progress separately and neither can print
    ///   the other's work.
    ///
    /// **The source and the appearance are not here on purpose.** Both belong to the kind, both
    /// are on <see cref="DocumentCatalogue.Spec"/>, and copying them in would be a second place
    /// for the same fact to live — one that can disagree with the asset the moment somebody
    /// retunes it. Ask <see cref="DocumentStore.TryGetSpec"/> for them.
    ///
    /// There is no id field. A document's id is its index in <see cref="DocumentStore"/>, which
    /// keeps the two from ever disagreeing.
    /// </remarks>
    [System.Serializable]
    public struct DocumentRecord
    {
        /// <summary>
        /// Which kind of document this is: an index into the catalogue.
        /// </summary>
        /// <remarks>
        /// Everything else about how it looks and where it comes from is read through this.
        /// </remarks>
        public int SpecIndex;

        /// <summary>
        /// The document's number, counting from 1 within its own kind and team.
        /// </summary>
        /// <remarks>
        /// Handed out by the store rather than chosen by whoever asked, so two jobs can never
        /// both come out as the same document. Counting per team is what lets each side have its
        /// own Excel 1 — the same name for two documents that never meet.
        /// </remarks>
        public int Number;

        /// <summary>
        /// Which team the document belongs to, or -1 for no team colour.
        /// </summary>
        public int Team;
    }
}
