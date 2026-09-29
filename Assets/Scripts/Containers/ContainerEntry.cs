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
        /// A virtual thing: a document's data. Not implemented yet; the id will point at
        /// the document store once that exists.
        /// </summary>
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
        /// Who put this here, or -1 when nobody in particular did.
        /// </summary>
        /// <remarks>
        /// Carried in the entry rather than tracked alongside it, because a container that
        /// needs to tell owners apart — a printer serving two queues — would otherwise have
        /// to keep a parallel list, and a second copy of the truth drifts.
        ///
        /// It is a client id, not a team id. Teams arrive with 2v2 and will map several
        /// clients onto one faction above this layer; nothing here has to change for that.
        /// </remarks>
        public int OwnerClientId;

        /// <summary>
        /// Builds an entry for a physical item.
        /// </summary>
        public static ContainerEntry ForEntity(int payloadIndex, int ownerClientId = -1) => new()
        {
            Kind = (byte)ContainerEntryKind.Entity,
            PayloadIndex = payloadIndex,
            DataId = -1,
            OwnerClientId = ownerClientId,
        };

        /// <summary>
        /// Builds an entry for a document's data.
        /// </summary>
        public static ContainerEntry ForData(int dataId, int ownerClientId = -1) => new()
        {
            Kind = (byte)ContainerEntryKind.Data,
            PayloadIndex = -1,
            DataId = dataId,
            OwnerClientId = ownerClientId,
        };

        /// <summary>
        /// True when this holds nothing.
        /// </summary>
        public static bool IsEmpty(in ContainerEntry entry) =>
            entry.Kind == (byte)ContainerEntryKind.Empty;
    }
}
