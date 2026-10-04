namespace Overworked.Documents
{
    /// <summary>
    /// One line of what a customer is asking for: a document, named.
    /// </summary>
    /// <remarks>
    /// Public fields and nothing else, deliberately — the same rule as
    /// <see cref="DocumentRecord"/> and <see cref="Containers.ContainerEntry"/>. FishNet's
    /// weaver builds the serializer by walking this type's fields and **silently skips private
    /// ones**, so a [SerializeField] private field would compile, run, and never cross the
    /// wire. It also picks up any public property with both a public getter and a public
    /// setter, so adding one would quietly widen the format. Read only through the public
    /// fields.
    ///
    /// Keep it a struct with no base type: the writer walks every field it can find while the
    /// reader skips types from UnityEngine.* assemblies, and a class inheriting one of those
    /// would desync between the two.
    ///
    /// **It names a document, and it does not point at one.** There is no document id here, and
    /// that is the whole design. A request says "bring me Excel 2"; each team has its own Excel 2,
    /// and a delivery is checked by resolving the name against the delivering team's own
    /// documents. Writing an id would bind the request to one team's copy of it, and the other
    /// team's identical-looking spreadsheet — printed from their own computer, carrying their own
    /// number — would count for nothing. Both teams race the same request; that is only true if
    /// the request is written in names.
    ///
    /// **There is no team field either**, for the same reason one step further out: a request is
    /// not addressed to anybody. Whoever fills it first takes it.
    ///
    /// <see cref="Number"/> is the number <see cref="DocumentStore"/> assigned, read back out of
    /// the store rather than chosen by whoever writes the request. A customer asking for "two
    /// contracts" is asking for two more of them, not for the specific documents numbered 1 and
    /// 2 — the store hands numbers out, and the request is written down afterwards in the terms
    /// the store gave.
    /// </remarks>
    [System.Serializable]
    public struct DocumentRequest
    {
        /// <summary>
        /// Which request this line belongs to.
        /// </summary>
        /// <remarks>
        /// A group of rows sharing this value is one customer's whole ask. It is the id
        /// <see cref="RequestBoard"/> returned from <see cref="RequestBoard.ServerCreate"/>.
        /// </remarks>
        public int RequestId;

        /// <summary>
        /// Which kind of document is wanted: an index into <see cref="DocumentCatalogue"/>.
        /// </summary>
        public int SpecIndex;

        /// <summary>
        /// Which number of that kind is wanted, counting from 1 within a team.
        /// </summary>
        /// <remarks>
        /// The same number names a document for each team — both sides have an Excel 2 — and
        /// which of the two a delivery satisfies is decided by who is delivering, not by
        /// anything written here.
        /// </remarks>
        public int Number;
    }
}
