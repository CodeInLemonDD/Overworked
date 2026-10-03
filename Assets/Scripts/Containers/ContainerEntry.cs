namespace Overworked.Containers
{
    /// <summary>
    /// What a container entry holds.
    /// </summary>
    public enum ContainerEntryKind : byte
    {
        /// <summary>
        /// Nothing. The default value, so an uninitialised entry reads as empty rather than
        /// as a real item.
        /// </summary>
        Empty = 0,

        /// <summary>
        /// A physical thing: paper, ink, a printed document. Resolved through
        /// <see cref="PayloadCatalogue"/>.
        /// </summary>
        Entity = 1,

        /// <summary>
        /// A document: <c>DataId</c> is an id in <c>DocumentStore</c>, which is where its
        /// kind, number and team live.
        /// </summary>
        /// <remarks>
        /// Materials are entities and documents are data, with no overlap — an entity carrying a
        /// number as well would give one document two spellings, and every module would have to
        /// agree on which one it meant.
        /// </remarks>
        Data = 2,
    }

    /// <summary>
    /// One item inside a container.
    /// </summary>
    /// <remarks>
    /// Public fields and nothing else, deliberately. FishNet's weaver builds the serializer
    /// by walking this type's fields and **silently skips private ones** — a
    /// [SerializeField] private field would compile, run, and never cross the wire. It also
    /// picks up any public property that has both a public getter and a public setter, so
    /// adding one would quietly widen the format. Read only through the public fields.
    ///
    /// Keep it a struct with no base type: the writer walks every field it can find while
    /// the reader skips types from UnityEngine.* assemblies, and a class inheriting one of
    /// those would desync between the two.
    /// </remarks>
    [System.Serializable]
    public struct ContainerEntry
    {
        /// <summary>
        /// A <see cref="ContainerEntryKind"/>, kept as a byte so it serialises as one.
        /// </summary>
        public byte Kind;

        /// <summary>
        /// Index into the payload catalogue for <see cref="ContainerEntryKind.Entity"/>,
        /// otherwise -1.
        /// </summary>
        public int PayloadIndex;

        /// <summary>
        /// Id of a document for <see cref="ContainerEntryKind.Data"/>, otherwise -1.
        /// </summary>
        public int DataId;

        /// <summary>
        /// Builds an entry for a physical item.
        /// </summary>
        public static ContainerEntry ForEntity(int payloadIndex) => new()
        {
            Kind = (byte)ContainerEntryKind.Entity,
            PayloadIndex = payloadIndex,
            DataId = -1,
        };

        /// <summary>
        /// Builds an entry for a document's data.
        /// </summary>
        public static ContainerEntry ForData(int dataId) => new()
        {
            Kind = (byte)ContainerEntryKind.Data,
            PayloadIndex = -1,
            DataId = dataId,
        };

        /// <summary>
        /// True when this holds nothing.
        /// </summary>
        public static bool IsEmpty(in ContainerEntry entry) =>
            entry.Kind == (byte)ContainerEntryKind.Empty;
    }
}
