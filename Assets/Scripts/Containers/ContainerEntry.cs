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
        /// Who this entry belongs to, or -1 when nobody in particular.
        /// </summary>
        /// <remarks>
        /// Unused today, and deliberately so. Raw materials have no owner — paper and ink are a
        /// shared pool, and who fed them in changes nothing — so every entry written right now
        /// passes -1.
        ///
        /// It was added for a printer that queued work per owner. That design was dropped once
        /// materials became shared, so this is waiting on the entries that genuinely need an
        /// origin: a document queued for printing belongs to a faction rather than to the
        /// machine. If the document store ends up carrying that itself, delete this field
        /// rather than leave one that nothing explains.
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
