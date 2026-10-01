using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// Every document that exists in this round, and the counter that numbers them.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject. The server appends, every peer reads.
    ///
    /// **Documents are never removed.** A round is a few minutes long and a document is a small
    /// struct, so the list staying whole costs nothing and buys two things: an id stays valid for
    /// as long as anything might still be holding it, and there is no removal to get wrong. A
    /// list that only grows is a list whose indices are stable, which is what lets the index
    /// *be* the id.
    ///
    /// Why not put the number and the team on <see cref="Containers.ContainerEntry"/> instead and
    /// skip this entirely: because then a document would have two spellings — an Entity entry
    /// carrying a number, and a Data entry pointing here — and every future module would have to
    /// agree on which one it meant. One thing, one representation. Materials (paper, ink) are
    /// entities and have no number; documents are data and have one.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DocumentStore : NetworkBehaviour
    {
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
        /// Every document, in creation order. The index is the id.
        /// </summary>
        /// <remarks>
        /// SyncList rather than SyncDictionary: the id is the position, so a dictionary would be
        /// a second copy of an ordering the list already has. It is the same type
        /// <see cref="Containers.ContainerBase"/> uses for its contents, which is known to
        /// replicate a struct of public fields correctly.
        /// </remarks>
        private readonly SyncList<DocumentRecord> _documents = new();

        /// <summary>
        /// How many documents exist.
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

        /// <summary>
        /// Returns the document an id refers to, or false when there is none.
        /// </summary>
        /// <remarks>
        /// False is expected, not exceptional. An id that arrived in a container entry may
        /// outrun the store's replication by a frame, and a caller that has just joined may be
        /// holding entries for documents it has not been told about yet. Every caller has to
        /// decide what to show in the meantime; silently returning a zeroed record would hand
        /// them a plausible-looking document that points at payload 0.
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
        /// Server: creates a document and returns its id.
        /// </summary>
        /// <param name="payloadIndex">Which payload it prints as, from the payload catalogue.</param>
        /// <param name="team">Which team it belongs to, or -1 for no colour.</param>
        /// <param name="source">A <see cref="DocumentSource"/>.</param>
        /// <remarks>
        /// The number is assigned here rather than passed in, so that two requests can never both
        /// come out as the same document. Callers that want a specific number are asking for the
        /// wrong thing: a number is this store's to give, and the NPC request that will check a
        /// document against what was asked for is checking a value this method produced.
        /// </remarks>
        [Server]
        public int ServerCreate(int payloadIndex, int team, int source)
        {
            DocumentRecord record = new()
            {
                PayloadIndex = payloadIndex,
                Number = NextNumber(payloadIndex),
                Team = team,
                Source = source,
            };

            _documents.Add(record);
            return _documents.Count - 1;
        }

        /// <summary>
        /// Server: the next free number for a given payload.
        /// </summary>
        /// <remarks>
        /// Counted by scanning rather than kept in a counter field. A counter would be a second
        /// piece of state that has to be reset when a round starts, and the failure mode of
        /// forgetting — documents resuming at last session's numbers, or restarting at 1 while
        /// the old ones still exist — is silent. Scanning is O(n) over a list of a few dozen
        /// structs, at most once per print job, and it cannot disagree with the list because it
        /// is derived from it.
        ///
        /// Numbers are per payload index, so Excel 1 and a contract 1 can coexist. That matches
        /// how the game talks about them: "Excel 3" is a name, not a global sequence.
        /// </remarks>
        private int NextNumber(int payloadIndex)
        {
            int highest = 0;

            for (int i = 0; i < _documents.Count; i++)
            {
                DocumentRecord record = _documents[i];
                if (record.PayloadIndex == payloadIndex && record.Number > highest)
                    highest = record.Number;
            }

            return highest + 1;
        }
    }
}
