using System;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Containers;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.UI;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Where a player chooses a document to have printed.
    /// </summary>
    /// <remarks>
    /// This is data acquisition, not printing. The machine hands nothing to anybody and spawns
    /// nothing: pressing E asks the server to open this client's panel, and every choice is made
    /// there and sent back. Keeping the choice out of the station is what lets the panel be a
    /// purely local thing — built in code, holding no replicated state, one per client.
    ///
    /// It reaches printers that are nowhere near it, and that is the point. The cost the game
    /// charges for a document is fetch-and-carry; making the player stand at the machine while it
    /// prints would delete that cost. So the only range that means anything is the one back to the
    /// computer the player is standing at — never the one to the printer they picked.
    ///
    /// Acquiring a document is this machine's job, not the panel's and not the printer's. A
    /// document from the company's own files is filed straight into the chosen machine's queue;
    /// one fetched over the Internet is held here for its kind's <c>FetchSeconds</c> first. The
    /// wait is the whole of what "data acquisition" costs, and it is the only place the two
    /// sources differ in behaviour rather than in labelling.
    ///
    /// **It makes nothing.** The document it is asked to print was named long before — by whoever
    /// handed it over — and this machine's part is to check that the asking player is entitled to
    /// it, wait out the download if there is one, and put it in a queue. Two of those checks are
    /// the ones a modified client would try to skip: the document has to belong to the asking
    /// player's team, and it has to have been handed over. Both are answered here rather than on
    /// the panel, which belongs to the client and can say whatever it likes.
    ///
    /// The fetch outlives the panel that started it, on purpose: walking away from a download
    /// does not cancel it, the same way walking away from the machine does not stop the printing.
    /// It also outlives the player, so a fetch ordered just before a disconnect still lands.
    ///
    /// Refusals are silent. A full queue, a printer that has since gone away, a document that is
    /// not this team's and one that has not been handed over all end the same way: nothing
    /// happens. Each is a normal state for the player to be in, and a machine that announced them
    /// would be reporting its own bookkeeping.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Computer : StationBase
    {
        /// <summary>
        /// The panel that opens on a client when this computer is used.
        /// </summary>
        private ComputerPanel _panel;

        /// <summary>
        /// Documents being fetched over the Internet, oldest first.
        /// </summary>
        /// <remarks>
        /// Replicated in full, because the wait is a fact about this machine that every player
        /// standing at it should be able to see — not a private note belonging to whoever pressed
        /// the button. Two teammates share a computer, and the second one needs to know that the
        /// machine is already busy fetching something.
        ///
        /// Must stay readonly: the weaver rejects a SyncType field that is assigned to.
        /// </remarks>
        private readonly SyncList<DocumentFetch> _fetching = new();

        /// <summary>
        /// Time source the fetch countdown runs on, held so it can be unsubscribed from.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Raised once per change to <see cref="_fetching"/>, on every peer, including the server.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. The same shape and the same duplicate-callback handling as
        /// <see cref="ContainerBase.ContentsChanged"/>, so a view has one pattern to follow.
        /// </remarks>
        public event Action FetchingChanged;

        /// <summary>
        /// How many fetches are in flight.
        /// </summary>
        public int FetchingCount => _fetching.Count;

        /// <summary>
        /// Reads a fetch by position in the list.
        /// </summary>
        public bool TryGetFetch(int index, out DocumentFetch fetch)
        {
            if (index < 0 || index >= _fetching.Count)
            {
                fetch = default;
                return false;
            }

            fetch = _fetching[index];
            return true;
        }

        /// <summary>
        /// True when this machine is already waiting on a document.
        /// </summary>
        /// <remarks>
        /// The panel asks this to grey a row out. It is deliberately keyed on the document rather
        /// than on "is anything fetching at all": a second, different document can be ordered
        /// while the first is still coming, and stopping the player from doing that would be the
        /// machine inventing a rule it has no reason to have.
        /// </remarks>
        public bool IsFetching(int documentId)
        {
            for (int i = 0; i < _fetching.Count; i++)
            {
                if (_fetching[i].DocumentId == documentId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The panel belonging to this computer, or null when the prefab has none.
        /// </summary>
        /// <remarks>
        /// Resolved on demand rather than in Awake. A NetworkBehaviour's Awake is rewritten by the
        /// weaver to run its own initialisation around the user's, and there is no reason to put a
        /// component lookup inside that. This is a lookup on one object, once, on the first press.
        /// </remarks>
        public ComputerPanel Panel
        {
            get
            {
                if (_panel == null)
                    _panel = GetComponentInChildren<ComputerPanel>(includeInactive: true);

                return _panel;
            }
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _fetching.OnChange += OnFetchingChanged;
        }

        public override void OnStopNetwork()
        {
            _fetching.OnChange -= OnFetchingChanged;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* The one wiring mistake left on this machine. A station with no panel is a machine
             * that does nothing, which is indistinguishable from a machine that is working and
             * simply not wanted yet. */
            if (Panel == null)
            {
                Debug.LogError(
                    $"{nameof(Computer)} on {gameObject.name} has no {nameof(ComputerPanel)} in its children; the panel it opens will never appear.",
                    this);
            }

            /* Subscribed by hand rather than through TickNetworkBehaviour, which is where this
             * hook normally comes from: a station has to stay a plain NetworkBehaviour, and the
             * base class offers it. OnTick is no substitute — it runs two or three times a frame
             * and may drop ticks, which is no basis for a timer. Same wiring as Printer. */
            _timeManager = TimeManager;
            if (_timeManager != null)
                _timeManager.OnUpdate += UpdateFetches;
        }

        public override void OnStopServer()
        {
            if (_timeManager != null)
            {
                _timeManager.OnUpdate -= UpdateFetches;
                _timeManager = null;
            }

            /* A scene object starts again on a later session, and the list is replicated rather
             * than owned by this component's lifetime. Whatever was in flight when the session
             * ended is not in flight in the next one. */
            _fetching.Clear();

            base.OnStopServer();
        }

        /// <summary>
        /// Server: drops whatever the last round was fetching.
        /// </summary>
        /// <remarks>
        /// The same clear <see cref="OnStopServer"/> does, for the same reason and with one more
        /// behind it. A fetch in flight belongs to a round that has been cleared, and the document
        /// it is waiting on has just been forgotten by the store — so a download allowed to finish
        /// would look itself up, find nothing, and hand a machine a job it cannot print. Dropping it
        /// costs the player the wait and nothing else, which is the right side to lose on.
        ///
        /// Clients see the list empty out on their own; it is a SyncList, and this is a server
        /// write like any other.
        /// </remarks>
        protected override void OnServerReset() => _fetching.Clear();

        /// <summary>
        /// Server: tells the asking client to open its panel.
        /// </summary>
        /// <remarks>
        /// Nothing is created and nothing is decided here. The station's whole part in the
        /// transaction is to be the thing the player pressed E on, and — later, when the choice
        /// comes back — to be the point the range is measured from.
        ///
        /// The press length is ignored: there is one verb, and how long the key was held does not
        /// change what it means.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, float heldSeconds)
        {
            if (player == null || conn == null)
                return;

            /* Deliberately no guard on there being anything to offer. There used to be one, back
             * when this opened only if the catalogue had entries; what the panel lists now is the
             * round's documents, and a round that has named none is a perfectly good thing to open
             * a window on — it says so, which is more use than a press that does nothing. */
            player.ServerOpenComputerPanel(this);
        }

        /// <summary>
        /// Server: acquires a document and files it into a machine's job queue.
        /// </summary>
        /// <remarks>
        /// This is the whole of what the two sources differ in. A document from the company's own
        /// files goes into the queue on this call; one fetched over the Internet is held first and
        /// lands when <see cref="UpdateFetches"/> decides the wait is over.
        ///
        /// Room in the queue is deliberately **not** checked up front for a fetch that has to wait.
        /// The queue can fill and empty several times during the wait, so a check made now is a
        /// guess about the future — and at the end of the wait both available answers are wrong:
        /// refusing throws away a wait the player has already paid for, and forcing the entry in
        /// overflows a container that is supposed to have a size. The wait happens first and the
        /// room is checked when it matters; see <see cref="Land"/>.
        /// </remarks>
        /// <returns>False when the request could not be started at all.</returns>
        [Server]
        public bool ServerBeginFetch(int documentId, Printer printer, int team)
        {
            if (printer == null || !printer.IsSpawned)
                return false;

            DocumentStore store = DocumentStore.Instance;
            if (store == null || !store.TryGet(documentId, out DocumentRecord record))
                return false;

            /* Not this team's document, and not handed over yet. Both are refused here rather than
             * only greyed on the panel: the panel belongs to the client, and a modified one can
             * send an id straight past it. This is the only place the answer cannot be argued
             * with.
             *
             * The team is the record's, compared against the team the calling player is on —
             * never against anything the client said. Each side has its own documents and none of
             * them can be printed by the other, which is what stops one team's progress from
             * being spent by the other. */
            if (record.Team != team)
                return false;

            DocumentUnlocks unlocks = DocumentUnlocks.Instance;
            if (unlocks != null && !unlocks.IsUnlocked(documentId))
                return false;

            if (!store.TryGetSpec(documentId, out DocumentCatalogue.Spec spec))
                return false;

            NetworkObject printerObject = printer.NetworkObject;
            if (printerObject == null)
                return false;

            /* Zero is the filing cabinet: it is already here. Nothing is queued and no state is
             * kept, so a local document cannot fail for a reason a local document has no business
             * having. */
            if (spec.FetchSeconds <= 0f)
                return Land(documentId, printer);

            _fetching.Add(new DocumentFetch
            {
                DocumentId = documentId,
                PrinterObjectId = printerObject.ObjectId,
                ServerReadyAt = (float)Time.timeAsDouble + spec.FetchSeconds,
            });

            return true;
        }

        /// <summary>
        /// Server: advances every fetch, landing the ones whose wait is over.
        /// </summary>
        /// <remarks>
        /// Unscaled, per the project's timer rule: a dropped frame or a paused editor must not
        /// change how long a download takes.
        ///
        /// A fetch that cannot land yet is left where it is and retried next frame. That happens
        /// for two reasons and they are not alike: a printer that has gone away, which is
        /// permanent and so is dropped, and a queue with no room, which is not. Holding on to the
        /// second is what makes a full queue cost the player time instead of the work — the
        /// document has not been created yet, so nothing is lost while it waits, and the number is
        /// handed out on the frame it finally goes in.
        /// </remarks>
        private void UpdateFetches()
        {
            if (_fetching.Count == 0)
                return;

            float now = (float)Time.timeAsDouble;

            for (int i = 0; i < _fetching.Count; i++)
            {
                DocumentFetch fetch = _fetching[i];
                if (now < fetch.ServerReadyAt)
                    continue;

                if (!TryResolvePrinter(fetch.PrinterObjectId, out Printer printer))
                {
                    _fetching.RemoveAt(i);
                    i--;
                    continue;
                }

                if (!Land(fetch.DocumentId, printer))
                    continue;

                _fetching.RemoveAt(i);
                i--;
            }
        }

        /// <summary>
        /// Server: files the document into the machine's queue.
        /// </summary>
        /// <remarks>
        /// The room is checked here rather than when the fetch started, because this is the moment
        /// it has to be true.
        ///
        /// Nothing is created and no number is spent. The document was named by whoever handed it
        /// over, long before anybody asked for it to be printed, so a queue with no room in it
        /// costs the player time rather than the work — which is the whole reason this can afford
        /// to wait instead of refusing.
        /// </remarks>
        /// <returns>False when there is nowhere to put it yet.</returns>
        private bool Land(int documentId, Printer printer)
        {
            ContainerBase queue = printer.Queue;
            if (queue == null || queue.IsFull)
                return false;

            /* Silent when it fails, like every other full container in the project. */
            return queue.ServerTryAdd(ContainerEntry.ForData(documentId));
        }

        /// <summary>
        /// Finds the machine a fetch was ordered for, or false when it is gone.
        /// </summary>
        /// <remarks>
        /// Looked up by object id rather than held as a reference, because a replicated struct
        /// cannot hold one. The lookup doubles as the check: a printer despawned while the fetch
        /// was running is simply no longer in the collection.
        /// </remarks>
        private bool TryResolvePrinter(int objectId, out Printer printer)
        {
            printer = null;

            NetworkManager manager = NetworkManager;
            if (manager == null || !manager.IsServerStarted)
                return false;

            if (!manager.ServerManager.Objects.Spawned.TryGetValue(objectId, out NetworkObject nob))
                return false;

            if (nob == null)
                return false;

            printer = nob.GetComponent<Printer>();
            return printer != null;
        }

        /// <summary>
        /// Raises <see cref="FetchingChanged"/> exactly once per change on every peer.
        /// </summary>
        /// <remarks>
        /// The same shape as <see cref="ContainerBase"/>'s contents callback and for the same
        /// reason: a host receives every change twice — once as the server's own write and once as
        /// the echoed client read — and letting both through rebuilds the panel twice per change,
        /// which is visible as flicker.
        /// </remarks>
        private void OnFetchingChanged(SyncListOperation op, int index, DocumentFetch oldItem, DocumentFetch newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            FetchingChanged?.Invoke();
        }
    }
}
