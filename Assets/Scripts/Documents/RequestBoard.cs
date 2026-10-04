using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// What the customers in the office are currently asking for.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject. The server appends, every peer reads —
    /// the same shape as <see cref="DocumentStore"/>, and deliberately so: the two are read
    /// together by everything that draws a request, and a second arrangement for the same kind of
    /// data would be a second thing to learn.
    ///
    /// **A request is a group of rows, not a row.** One customer asking for a contract and two
    /// spreadsheets is three <see cref="DocumentRequest"/> rows sharing a
    /// <see cref="DocumentRequest.RequestId"/>. Flat rather than a list of lists because a
    /// SyncList of a struct of three ints is a known-good thing in this project, while a SyncList
    /// of jagged arrays is a serialization question nobody wants to answer twice.
    ///
    /// **Rows for one request are always adjacent.** Appending writes a whole group at once, and
    /// removal takes out every row of an id at once, so no ordering can interleave two requests.
    /// That is what lets <see cref="RequestCount"/> count group boundaries instead of searching,
    /// and it is worth stating out loud because a later "insert a row into the middle" would
    /// break it silently.
    ///
    /// **Requests do not create documents.** By the time a row exists, the document it names is
    /// already in <see cref="DocumentStore"/>, because the number on the row is the number the
    /// store assigned. The order is: name the documents, read their numbers back, then write the
    /// request in those terms. Doing it the other way round would mean the request choosing
    /// numbers, and two requests could then both come out asking for the same document.
    /// </remarks>
    [DisallowMultipleComponent]
    public class RequestBoard : NetworkBehaviour
    {
        /// <summary>
        /// Every row of every live request, grouped by request and in the order it was written.
        /// </summary>
        /// <remarks>
        /// SyncList rather than SyncDictionary keyed by request id: the grouping is already
        /// carried by the rows themselves, so a dictionary would be a second copy of a structure
        /// the list already has — and one that could disagree with it.
        /// </remarks>
        private readonly SyncList<DocumentRequest> _rows = new();

        /// <summary>
        /// How many requests have ever been written this round.
        /// </summary>
        /// <remarks>
        /// Two jobs at once, and they are the same fact seen from either side: it is the id the
        /// next request will be given, and it is the count a caller indexes the tier table with.
        /// Both want a number that only ever goes up, which is exactly what counting live rows
        /// cannot give — a customer leaving takes its rows with it, and a "how many requests are
        /// open" reading would then walk the difficulty *backwards* mid-round.
        ///
        /// It is on the wire because the client side wants it too: "request 3" is a thing a panel
        /// can show, and it is not derivable from the rows.
        /// </remarks>
        private readonly SyncVar<int> _made = new(0);

        /// <summary>
        /// The board in the scene, or null before it has spawned.
        /// </summary>
        /// <remarks>
        /// A static rather than a lookup per call, for the same reason
        /// <see cref="DocumentStore.Instance"/> is: the readers run every frame on every peer,
        /// and a FindObjectsByType behind a world-space label would be the most expensive thing
        /// in the frame.
        ///
        /// Null is a normal state, not an error. A label whose board has not spawned yet has
        /// nothing to draw, and the right answer there is to leave whatever is on screen alone.
        /// </remarks>
        public static RequestBoard Instance { get; private set; }

        /// <summary>
        /// How many rows there are, across every request.
        /// </summary>
        public int Count => _rows.Count;

        /// <summary>
        /// How many requests are live right now.
        /// </summary>
        /// <remarks>
        /// Counted by walking group boundaries rather than by searching for duplicates. The
        /// adjacency invariant in the class remarks is what makes that correct, and an O(n) pass
        /// over a list of a few dozen structs is not worth caching.
        /// </remarks>
        public int RequestCount
        {
            get
            {
                int count = 0;

                for (int i = 0; i < _rows.Count; i++)
                {
                    if (i == 0 || _rows[i].RequestId != _rows[i - 1].RequestId)
                        count++;
                }

                return count;
            }
        }

        /// <summary>
        /// How many requests have been written this round, ever.
        /// </summary>
        /// <remarks>
        /// **The difficulty cursor.** The next request to be written sits at this index in the
        /// tier table, so a spawner reads this *before* creating, not after. It counts what has
        /// been made rather than what is live, so a customer walking out does not make the round
        /// easier.
        /// </remarks>
        public int RequestsMade => _made.Value;

        /// <summary>
        /// Raised once per change, on every peer, including the server.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. Views subscribe to this rather than to the SyncList so
        /// the duplicate-callback handling stays in one place — the same shape
        /// <see cref="Containers.ContainerBase"/> uses.
        /// </remarks>
        public event Action RequestsChanged;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            /* Two boards in one scene is a wiring mistake that would otherwise show up as
             * customers asking for things that only one player can see. */
            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(RequestBoard)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; requests will disagree.",
                    this);
            }

            Instance = this;

            _rows.OnChange += OnRowsChanged;
        }

        public override void OnStopNetwork()
        {
            _rows.OnChange -= OnRowsChanged;

            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* A SyncList on a scene NetworkObject is not reset between sessions — the component
             * survives, so last session's rows would still be here and the first customer of the
             * new round would come out numbered however the last round left it. */
            ServerClear();
        }

        /// <summary>
        /// Returns the row at a position in the list, or false when there is none.
        /// </summary>
        /// <remarks>
        /// Positional, and that is rarely what a caller wants — this exists so a view can walk
        /// every row in the order the server wrote them. A caller that knows which request it
        /// cares about wants <see cref="TryGetWanted"/>.
        /// </remarks>
        public bool TryGet(int index, out DocumentRequest request)
        {
            if (index < 0 || index >= _rows.Count)
            {
                request = default;
                return false;
            }

            request = _rows[index];
            return true;
        }

        /// <summary>
        /// Fills a buffer with every row of one request.
        /// </summary>
        /// <remarks>
        /// The buffer is cleared first and reused rather than allocated, matching every other
        /// buffer in the project — a customer checking its own request does this on every
        /// delivery attempt, and a delivery attempt can happen on any frame.
        ///
        /// False means the request has no rows at all, which is the normal state of a request
        /// that has just been removed. It does not mean the buffer was left alone: it is always
        /// cleared, so a caller that ignores the return value sees an empty list rather than the
        /// previous request's contents.
        /// </remarks>
        /// <returns>True when the request exists.</returns>
        public bool TryGetWanted(int requestId, List<DocumentRequest> buffer)
        {
            buffer.Clear();

            for (int i = 0; i < _rows.Count; i++)
            {
                DocumentRequest row = _rows[i];
                if (row.RequestId == requestId)
                    buffer.Add(row);
            }

            return buffer.Count > 0;
        }

        /// <summary>
        /// Server: writes a request and returns its id.
        /// </summary>
        /// <remarks>
        /// The caller fills <see cref="DocumentRequest.SpecIndex"/> and
        /// <see cref="DocumentRequest.Number"/> and leaves
        /// <see cref="DocumentRequest.RequestId"/> alone — it is overwritten here, so a caller
        /// cannot accidentally staple its rows onto somebody else's request.
        ///
        /// Returns -1 for an empty ask. That is a bug in the caller rather than a state to
        /// handle — a customer with nothing to want would sit there forever — but it is a quiet
        /// one, so it is refused here where the caller can see it.
        /// </remarks>
        /// <param name="wanted">The rows, without ids.</param>
        /// <returns>The new request's id, or -1 when nothing was written.</returns>
        [Server]
        public int ServerCreate(IReadOnlyList<DocumentRequest> wanted)
        {
            if (wanted == null || wanted.Count == 0)
                return -1;

            /* The counter is both the id and the tier cursor, so the id is taken before it is
             * bumped: the request being written right now is the one at the current tier. */
            int requestId = _made.Value;
            _made.Value = requestId + 1;

            for (int i = 0; i < wanted.Count; i++)
            {
                DocumentRequest row = wanted[i];
                row.RequestId = requestId;
                _rows.Add(row);
            }

            return requestId;
        }

        /// <summary>
        /// Server: takes a request off the board.
        /// </summary>
        /// <remarks>
        /// Every row of the id goes, so a request can never be left half-there — which is also
        /// what keeps the adjacency invariant true after a removal.
        ///
        /// Returns false when there was nothing to remove, which is the ordinary outcome of two
        /// paths racing to close the same customer. Callers should read it as "already gone",
        /// not as an error.
        /// </remarks>
        [Server]
        public bool ServerRemove(int requestId)
        {
            bool removed = false;

            /* Backwards, so the rows that survive keep their order. */
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (_rows[i].RequestId != requestId)
                    continue;

                _rows.RemoveAt(i);
                removed = true;
            }

            return removed;
        }

        /// <summary>
        /// Server: empties the board and puts the difficulty back to the start.
        /// </summary>
        /// <remarks>
        /// Resetting the cursor is part of this rather than a separate call, because a board with
        /// no requests whose cursor is still at 30 would open the next round at the hardest tier.
        /// The two only make sense together.
        /// </remarks>
        [Server]
        public void ServerClear()
        {
            _rows.Clear();
            _made.Value = 0;
        }

        /// <summary>
        /// Raises <see cref="RequestsChanged"/> exactly once per change on every peer.
        /// </summary>
        /// <remarks>
        /// A host receives every change twice: once as the server's own write (asServer true)
        /// and once as the echoed client read (asServer false). Letting both through rebuilds
        /// every label twice per change, which is visible as flicker. Prefer the client echo
        /// wherever this peer runs a client — which covers hosts and pure clients — and fall
        /// back to the server call on a dedicated server, where no echo exists.
        /// </remarks>
        private void OnRowsChanged(SyncListOperation op, int index, DocumentRequest oldItem, DocumentRequest newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            RequestsChanged?.Invoke();
        }
    }
}
