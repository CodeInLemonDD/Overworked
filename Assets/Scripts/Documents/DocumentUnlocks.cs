using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// Which of the round's documents the players have actually been given.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject, alongside <see cref="DocumentStore"/>.
    /// The server writes, every peer reads.
    ///
    /// **Being named and being yours are two different things, and this is the second one.** A
    /// customer asking for a contract, a colleague promising a spreadsheet, a file whose download
    /// rights have just opened — each of those puts a record in the store, and none of them puts
    /// anything in a player's hands. This list is what changes when the document is actually
    /// handed over, and it is what the computer refuses to print without.
    ///
    /// That gap is the whole point of the round it was built for. A task names three documents
    /// and the panel shows three rows, two of them greyed — the player can see what the job wants
    /// before they have any of it, which is what makes the job a plan rather than a surprise.
    ///
    /// **Taking a job is what grants what the job asks for, and only to the team that took it.**
    /// The gap above is what makes a job a plan, and it is also the whole contest: a customer that
    /// appeared already handing its paperwork round would be a race nobody had to run, and both
    /// sides would be holding the same documents. So naming and granting are kept apart at exactly
    /// the point that matters — the spawner names, <see cref="Npc.Customer.ServerAccept"/> grants.
    ///
    /// Nothing has to say "for this team". A grant is a document id, document ids are per team, and
    /// two sides' identically-named contracts are two different ids — so granting one team's copy
    /// leaves the other team's exactly as locked as it was.
    ///
    /// An NPC handing over a file that no request named — a colleague trading one document for
    /// another — is the case that still needs an explicit <see cref="ServerUnlock"/>.
    ///
    /// **It holds document ids, not catalogue indices.** A kind is not something a player earns;
    /// an Excel is. Two teams earning "Excel" separately earn two different documents, and a list
    /// of kinds could not tell them apart.
    ///
    /// **The list only grows.** Nothing is ever taken back, so membership is the whole of the
    /// state and there is no ordering to keep. That is why it is a list of ids rather than a
    /// dictionary or a set: the same type <see cref="DocumentStore"/> and
    /// <see cref="Containers.ContainerBase"/> already use, and an item goes over the wire through
    /// the very path a <c>SyncVar&lt;int&gt;</c> already uses — which the printer runs on, so it
    /// is not a new thing to be right about. A linear scan over a handful of entries is cheaper
    /// than the machinery to avoid it.
    ///
    /// **One change, one event — but a reset is several changes.** <see cref="ServerResetUnlocks"/>
    /// clears and every removal raises <see cref="UnlockedChanged"/>. A view that rebuilds on
    /// this event will therefore rebuild once per entry when a round restarts. That is not worth
    /// machinery to avoid; it is worth knowing before wondering why the panel flickers on startup.
    ///
    /// **A null <see cref="Instance"/> means "nothing is locked".** Callers read it that way on
    /// purpose: a scene with no unlocks component should behave exactly as it did before this
    /// existed, rather than having every machine refuse everything. The failure that hides is
    /// "locks do not apply", whose symptom is a panel with nothing greyed out; the failure that
    /// would hide if this refused instead is "the computer stopped working", which looks like a
    /// station nobody wired — a much more expensive thing to go and find.
    ///
    /// There is no contrast to draw with a misconfigured component any more, which there was when
    /// this read a catalogue: ids are validated against <see cref="DocumentStore"/>, so a
    /// component that is present is a component that works, and the only failure left is being
    /// absent.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DocumentUnlocks : NetworkBehaviour
    {
        /// <summary>
        /// The unlocks in force, as document ids, in the order they were handed over.
        /// </summary>
        /// <remarks>
        /// Must stay readonly: the weaver rejects a SyncType field that is assigned to.
        /// </remarks>
        private readonly SyncList<int> _unlocked = new();

        /// <summary>
        /// The unlocks in the scene, or null before they have spawned — and null for good on a
        /// scene that has none. See the class remarks for what callers should do with that.
        /// </summary>
        public static DocumentUnlocks Instance { get; private set; }

        /// <summary>
        /// Raised once per change, on every peer, including the server.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. Same shape and same duplicate-callback handling as
        /// <see cref="Containers.ContainerBase.ContentsChanged"/>, so a view has one pattern.
        /// </remarks>
        public event Action UnlockedChanged;

        /// <summary>
        /// How many documents have been handed over.
        /// </summary>
        public int UnlockedCount => _unlocked.Count;

        /// <summary>
        /// Reads an unlock by its position in this list.
        /// </summary>
        /// <remarks>
        /// Position in this list, not a document id — see <see cref="TryGetUnlocked"/>. The order
        /// is the order things were earned in, which is what a console listing wants to show.
        /// </remarks>
        public bool TryGetUnlocked(int index, out int documentId)
        {
            if (index < 0 || index >= _unlocked.Count)
            {
                documentId = -1;
                return false;
            }

            documentId = _unlocked[index];
            return true;
        }

        /// <summary>
        /// True when a document has been handed over.
        /// </summary>
        /// <remarks>
        /// False for an id that does not exist, as well as for one that is merely still
        /// outstanding. The caller's answer to either is the same — it cannot be printed — and
        /// separating them would invite somebody to report the first as a problem when the second
        /// is the normal state of a round in progress.
        /// </remarks>
        public bool IsUnlocked(int documentId)
        {
            for (int i = 0; i < _unlocked.Count; i++)
            {
                if (_unlocked[i] == documentId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Server: hands a document over.
        /// </summary>
        /// <remarks>
        /// This is the whole interface the rest of the game needs. Whatever ends up deciding that
        /// a player has earned a document — a customer closing a job, a colleague accepting a
        /// folder, a debug command — calls this and nothing else; the panel and the machines never
        /// learn where it came from, because they have no business knowing.
        ///
        /// The id is checked against <see cref="DocumentStore"/> rather than taken on trust, so a
        /// console typo cannot put an id in this list that nothing will ever resolve.
        /// </remarks>
        /// <returns>False when no such document exists, or it was already handed over.</returns>
        [Server]
        public bool ServerUnlock(int documentId)
        {
            DocumentStore store = DocumentStore.Instance;
            if (store == null || !store.TryGet(documentId, out _))
                return false;
            if (IsUnlocked(documentId))
                return false;

            _unlocked.Add(documentId);
            return true;
        }

        /// <summary>
        /// Server: forgets every unlock.
        /// </summary>
        /// <remarks>
        /// A SyncList keeps its contents across sessions — nothing resets it — so a round that
        /// did not clear this would start with everything the last one had earned. See the same
        /// reasoning on <see cref="Stations.Computer"/>'s fetch list.
        ///
        /// Clearing is the whole of it now. It used to seed from the catalogue, back when which
        /// kinds exist was the same question as which ones a player may print; those came apart
        /// when documents became things that are named rather than kinds that are opened.
        /// </remarks>
        [Server]
        public void ServerResetUnlocks() => _unlocked.Clear();

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _unlocked.OnChange += OnUnlockedChanged;

            /* Two of these in one scene is a wiring mistake whose only symptom would be a panel
             * that disagrees with itself about what has been handed over. */
            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(DocumentUnlocks)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; unlocks will disagree.",
                    this);
            }

            Instance = this;
        }

        public override void OnStopNetwork()
        {
            _unlocked.OnChange -= OnUnlockedChanged;

            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Nothing is seeded. Every document a round can produce is one somebody hands over,
             * so a fresh round starts with none of them — which is the state this list already
             * happens to be in when it is cleared. Done explicitly anyway, so the answer does not
             * depend on the list having been empty to begin with. */
            ServerResetUnlocks();
        }

        /// <summary>
        /// Raises <see cref="UnlockedChanged"/> exactly once per change on every peer.
        /// </summary>
        /// <remarks>
        /// A host receives every change twice — once as the server's own write and once as the
        /// echoed client read — and letting both through rebuilds whatever is watching twice.
        /// Same handling as <see cref="Containers.ContainerBase"/>.
        /// </remarks>
        private void OnUnlockedChanged(SyncListOperation op, int index, int oldItem, int newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            UnlockedChanged?.Invoke();
        }
    }
}
