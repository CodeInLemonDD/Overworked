namespace Overworked.Documents
{
    /// <summary>
    /// Where a document came from.
    /// </summary>
    /// <remarks>
    /// Stored as an int on <see cref="DocumentRecord"/> rather than as this type, for the same
    /// reason <see cref="Containers.ContainerEntry.Kind"/> is a byte: the serializer walks
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
    /// One document that exists in this round.
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
    /// There is no id field. A document's id is its index in
    /// <see cref="DocumentStore"/>, which keeps the two from ever disagreeing.
    /// </remarks>
    [System.Serializable]
    public struct DocumentRecord
    {
        /// <summary>
        /// Which payload this document wears. An index into the payload catalogue, so every
        /// peer resolves it to the same appearance.
        /// </summary>
        public int PayloadIndex;

        /// <summary>
        /// The document's number, counting from 1 within its own payload index.
        /// </summary>
        /// <remarks>
        /// Handed out by the store rather than chosen by whoever asked, so two Excel jobs can
        /// never both be "Excel 1". This is the value a request will be checked against, which
        /// is why it is worth a field of its own rather than being folded into the payload.
        /// </remarks>
        public int Number;

        /// <summary>
        /// Which team the document belongs to, or -1 for no team colour.
        /// </summary>
        public int Team;

        /// <summary>
        /// A <see cref="DocumentSource"/>, kept as an int.
        /// </summary>
        public int Source;
    }
}
