using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// Every document that has been named in this round, and the counter that numbers them.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject. The server appends, every peer reads.
    ///
    /// **A record appears when a document is named, not when it is made.** A customer asking for
    /// a contract, a colleague promising a spreadsheet, a file whose download rights have just
    /// been opened — each of those is the moment the document starts to exist as far as the round
    /// is concerned, and each of them is before anybody has printed anything. Printing takes an
    /// id that is already here and turns it into a sheet of paper; it never invents one.
    ///
    /// That split is what lets the two halves of the game talk about the same thing. A request
    /// says "bring me Excel 2"; a printer's queue says "this id is being printed"; the panel says
    /// "this one is greyed because you have not been given it yet". All three name an id from
    /// this list, and none of them can be talking about a different document.
    ///
    /// **Documents are never removed.** A round is a few minutes long and a document is three
    /// ints, so the list staying whole costs nothing and buys two things: an id stays valid for
    /// as long as anything might still be holding it, and there is no removal to get wrong. A
    /// list that only grows is a list whose indices are stable, which is what lets the index
    /// *be* the id.
    ///
    /// **This is also where a document id turns into everything about it.** A record holds a
    /// kind, and nothing else; the name, the appearance and the cost all live on the catalogue
    /// entry that kind points at. Rather than make every reader hold a catalogue of its own —
    /// the printer, the printer's display, the panel and the console would each need one, and
    /// each could be wired to the wrong asset — the lookup is here, on the component that owns
    /// the ids in the first place.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DocumentStore : NetworkBehaviour
    {
        /// <summary>
        /// The kinds of document this store's records refer to.
        /// </summary>
        /// <remarks>
        /// Must be the same asset every computer offers from. A different one would not crash
        /// anything — it would silently rename documents, which is worse.
        /// </remarks>
        [Tooltip("The same DocumentCatalogue the computers offer from. Resolves what a record's SpecIndex means.")]
        [SerializeField]
        private DocumentCatalogue _catalogue;

        /// <summary>
        /// Every document, in the order it was named. The index is the id.
        /// </summary>
        /// <remarks>
        /// SyncList rather than SyncDictionary: the id is the position, so a dictionary would be
        /// a second copy of an ordering the list already has. It is the same type
        /// <see cref="Containers.ContainerBase"/> uses for its contents, which is known to
        /// replicate a struct of public fields correctly.
        /// </remarks>
        private readonly SyncList<DocumentRecord> _documents = new();

        /// <summary>
        /// The store in the scene, or null before it has spawned.
        /// </summary>
        /// <remarks>
        /// A static rather than a lookup per call, because the readers are on the client and run
        /// every frame — the printer's display resolves one id per visible slot on each pass, and
        /// a FindObjectsByType behind that would be the most expensive thing in the frame.
        ///
        /// Null is a normal state, not an error: a display whose store has not spawned yet has
        /// nothing to draw, and the right answer there is to leave the slot as it is rather than
        /// to clear it.
        /// </remarks>
        public static DocumentStore Instance { get; private set; }

        /// <summary>
        /// How many documents have been named.
        /// </summary>
        public int Count => _documents.Count;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            /* Two stores in one scene is a wiring mistake that would otherwise show up as
             * documents that exist on one screen and not the other. */
            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(DocumentStore)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; ids will disagree.",
                    this);
            }

            Instance = this;
        }

        public override void OnStopNetwork()
        {
            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Every id this store hands out is meaningless without the catalogue that says what
             * its kind is, and the failure is silent: TryGetSpec would simply answer no for every
             * document, and a printer that cannot resolve a document leaves it in the queue. */
            if (_catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(DocumentStore)} on {gameObject.name} has no {nameof(DocumentCatalogue)} assigned, so nothing that reads a document will be able to say what it is. Assign the same asset the computers use.",
                    this);
            }
        }

        /// <summary>
        /// Returns the document an id refers to, or false when there is none.
        /// </summary>
        /// <remarks>
        /// False is expected, not exceptional. An id that arrived in a container entry may
        /// outrun the store's replication by a frame, and a caller that has just joined may be
        /// holding entries for documents it has not been told about yet. Every caller has to
        /// decide what to show in the meantime; silently returning a zeroed record would hand
        /// them a plausible-looking document pointing at kind 0.
        /// </remarks>
        public bool TryGet(int id, out DocumentRecord record)
        {
            if (id < 0 || id >= _documents.Count)
            {
                record = default;
                return false;
            }

            record = _documents[id];
            return true;
        }

        /// <summary>
        /// Returns the kind a document is, or false when the id or the kind is not known.
        /// </summary>
        /// <remarks>
        /// Failure covers three cases that a caller cannot tell apart and does not need to: no
        /// such document, no catalogue assigned, and a record whose kind has since been deleted
        /// from the catalogue. The answer to all three is the same — leave whatever is on screen
        /// alone — and a caller that wanted to report one of them as a bug would have no way to
        /// know which it had.
        /// </remarks>
        public bool TryGetSpec(int id, out DocumentCatalogue.Spec spec)
        {
            spec = default;

            if (!TryGet(id, out DocumentRecord record))
                return false;

            return TryGetSpecAt(record.SpecIndex, out spec);
        }

        /// <summary>
        /// Returns a kind by its catalogue index, or false when there is no such kind.
        /// </summary>
        /// <remarks>
        /// The same lookup as <see cref="TryGetSpec"/> for a caller that already has a kind index
        /// and no document to go through — a request names a kind before any document it might
        /// resolve to is in hand, and the readout that prints what a customer is waiting for has
        /// nothing but the index.
        ///
        /// It belongs here next to the other lookup rather than on each reader, for the reason
        /// the class remarks already give: a catalogue held by four components is four chances to
        /// wire the wrong asset, and a wrong asset does not crash — it renames documents.
        /// </remarks>
        public bool TryGetSpecAt(int specIndex, out DocumentCatalogue.Spec spec)
        {
            spec = default;

            if (_catalogue == null)
                return false;

            return _catalogue.TryGet(specIndex, out spec);
        }

        /// <summary>
        /// Server: names a document and returns its id.
        /// </summary>
        /// <param name="specIndex">Which kind of document it is, from the catalogue.</param>
        /// <param name="team">Which team it belongs to, or -1 for no colour.</param>
        /// <remarks>
        /// The number is assigned here rather than passed in, so that two documents can never both
        /// come out as the same one. Callers that want a specific number are asking for the wrong
        /// thing: a number is this store's to give, and the request that checks a delivered folder
        /// is checking a value this method produced.
        ///
        /// Nothing is printed by this. Naming a document and making one are separate acts now —
        /// see the class remarks.
        /// </remarks>
        [Server]
        public int ServerCreate(int specIndex, int team)
        {
            DocumentRecord record = new()
            {
                SpecIndex = specIndex,
                Number = NextNumber(specIndex, team),
                Team = team,
            };

            _documents.Add(record);
            return _documents.Count - 1;
        }

        /// <summary>
        /// Server: the next free number for a kind, within one team.
        /// </summary>
        /// <remarks>
        /// Counted by scanning rather than kept in a counter field. A counter would be a second
        /// piece of state that has to be reset when a round starts, and the failure mode of
        /// forgetting — documents resuming at last session's numbers, or restarting at 1 while
        /// the old ones still exist — is silent. Scanning is O(n) over a list of a few dozen
        /// structs, at most once per document named, and it cannot disagree with the list because
        /// it is derived from it.
        ///
        /// **Per kind, and per team.** Per kind, so a contract 1 and a spreadsheet 1 can coexist;
        /// that is how the game talks, and "Excel 3" is a name rather than a position in a global
        /// sequence. Per team, so both sides can have an Excel 1 — the two are different
        /// documents that share a name, and neither side's progress can push the other's numbers
        /// around. Counting for both teams in one sequence was a real bug: whichever team printed
        /// second would find its first spreadsheet numbered 2.
        /// </remarks>
        private int NextNumber(int specIndex, int team)
        {
            int highest = 0;

            for (int i = 0; i < _documents.Count; i++)
            {
                DocumentRecord record = _documents[i];
                if (record.SpecIndex == specIndex && record.Team == team && record.Number > highest)
                    highest = record.Number;
            }

            return highest + 1;
        }
    }
}
