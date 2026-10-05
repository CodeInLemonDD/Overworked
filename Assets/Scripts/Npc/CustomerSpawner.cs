using System.Collections.Generic;
using FishNet;
using Overworked.Documents;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Npc
{
    /// <summary>
    /// Keeps the office staffed: how many customers are waiting on something, and what they want.
    /// </summary>
    /// <remarks>
    /// **Customers are reused, never spawned or destroyed.** The scene holds a handful of
    /// customer objects and one of them takes the next request when a place comes free. That is
    /// not a shortcut — a scene NetworkObject that is despawned degrades to
    /// <c>SetActive(false)</c> with no way back (see CONSTRAINTS.md), so "the customer leaves and
    /// another arrives" cannot be a despawn without the second half being impossible. Nothing here
    /// touches a customer's lifetime; it only decides which one is next.
    ///
    /// **Server only.** Making requests means naming documents, and naming documents is a server
    /// operation. This is a plain MonoBehaviour that checks rather than a network behaviour: there
    /// is no replicated state of its own, and every object it touches carries its own.
    ///
    /// **This is the console's <c>tier</c> command with a trigger attached.** The shape is
    /// deliberately the same one — read the board's cursor, name every document the tier asks for
    /// once per team, write the request in the numbers the store handed back — and the console's
    /// version exists precisely so the sequence could be walked through before this did. When one
    /// of the two changes, the other should be read before deciding it is still right.
    /// </remarks>
    [DisallowMultipleComponent]
    public class CustomerSpawner : MonoBehaviour
    {
        [Header("Requests")]

        /// <summary>
        /// The sequence of asks, one tier per request.
        /// </summary>
        [Tooltip("The sequence of asks, one tier per request. The same asset the console's 'tier' command reads.")]
        [SerializeField]
        private RequestCatalogue _requestCatalogue;

        /// <summary>
        /// How many customers should be waiting on something at once.
        /// </summary>
        /// <remarks>
        /// The rest of the customers stand idle, which is what makes the office read as a place
        /// with a queue in it rather than as a row of identical statues.
        /// </remarks>
        [Tooltip("How many customers should be waiting on something at once.")]
        [Min(1)]
        [SerializeField]
        private int _activeCount = 2;

        /// <summary>
        /// How long to wait after one is seated before seating the next, in seconds.
        /// </summary>
        /// <remarks>
        /// A beat rather than every frame, so a customer walking in reads as arriving. It also
        /// bounds how often a missing catalogue or board can complain — see
        /// <see cref="ReportMissing"/>.
        /// </remarks>
        [Tooltip("Seconds between one customer being seated and the next. Also the retry period when something is unwired.")]
        [Min(0f)]
        [SerializeField]
        private float _seatInterval = 1f;

        /// <summary>
        /// Where requests are written.
        /// </summary>
        private readonly List<DocumentRequest> _rows = new();

        /// <summary>
        /// The customers in the scene, found once.
        /// </summary>
        /// <remarks>
        /// Cached because they never spawn or are destroyed — that is the whole design — so the
        /// search runs until it finds something and then not again. Refreshed only if the array
        /// comes back empty or holds a destroyed entry, which covers the frames before the scene's
        /// network objects have spawned.
        /// </remarks>
        private Customer[] _customers;

        /// <summary>
        /// Seconds until the next customer may be seated.
        /// </summary>
        private float _seatTimer;

        /// <summary>
        /// Which missing dependency has already been complained about, so the message is said once
        /// rather than once per second forever.
        /// </summary>
        private string _reported;

        private void Update()
        {
            if (!InstanceFinder.IsServerStarted)
                return;

            /* Unscaled, matching every other timer in the project: this is a pacing delay, and a
             * paused editor must not turn it into something else. */
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _seatTimer -= deltaTime;
            if (_seatTimer > 0f)
                return;

            _seatTimer = _seatInterval;
            SeatOne();
        }

        /// <summary>
        /// Server: gives one idle customer the next request, if the office is short-staffed.
        /// </summary>
        private void SeatOne()
        {
            if (_requestCatalogue == null)
            {
                ReportOnce("no RequestCatalogue assigned, so there is nothing to ask for");
                return;
            }

            RequestBoard board = RequestBoard.Instance;
            if (board == null)
            {
                ReportOnce("no RequestBoard in the scene");
                return;
            }

            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                ReportOnce("no DocumentStore in the scene");
                return;
            }

            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null || scores.TeamCount <= 0)
            {
                ReportOnce("no ScoreBoard in the scene, so there is no telling how many teams there are");
                return;
            }

            /* Nothing is asked for until the match begins, and nothing is asked for once it has
             * ended. A round waiting on its players to pick sides is not a round yet, and seating
             * customers through the wait would spend the round's own time limit on the waiting — the
             * clock starts at the whistle, so the customers have to as well. The other end is the
             * same rule read backwards: a customer seated after the final whistle belongs to no
             * round, and would put a request on the board that nothing can ever collect.
             *
             * The board rather than MatchFlow's phase, because this is the server and the board has
             * the exact answer — the phase is the readout's copy of it and is up to a poll behind.
             * See MatchFlow. Silence rather than a report: waiting is the normal state of a lobby,
             * and so is a finished round. */
            if (!scores.HasStarted || scores.IsOver)
                return;

            /* Asked in this order so the common case — the office is already staffed — costs one
             * pass over the customers rather than two. */
            if (CountWaiting() >= _activeCount)
                return;

            if (!FindIdle(out Customer idle))
                return;

            WriteRequest(board, store, scores.TeamCount, idle);
        }

        /// <summary>
        /// Server: names the documents a tier asks for and gives the resulting request to a
        /// customer.
        /// </summary>
        /// <remarks>
        /// **Every document is created once per team, and the numbers must come out equal.** That
        /// they do is a consequence rather than a hope: every document in a round is created here,
        /// in lockstep for both teams, because players print documents rather than naming them.
        /// It is checked anyway, because the failure is invisible until a delivery mysteriously
        /// will not count — one team's Excel 2 being a different document from the other team's.
        ///
        /// The tier is read from the board's cursor **before** anything is created, so the request
        /// being written is the one at the current tier rather than the one after it.
        /// </remarks>
        private void WriteRequest(RequestBoard board, DocumentStore store, int teamCount, Customer customer)
        {
            int tier = board.RequestsMade;

            if (!_requestCatalogue.TryGet(tier, out RequestCatalogue.RequestTier wanted))
            {
                ReportOnce("the RequestCatalogue has no tiers authored");
                return;
            }

            _rows.Clear();

            /* **Nothing is granted here, and that is the point.** A customer saying what it wants
             * is not the same as a customer handing anything over: the documents are named, and
             * stay unprintable, until somebody walks up to this customer and takes the job.
             * Granting at this moment was the first thing this did, and it handed both teams the
             * whole request the instant it appeared — a race nobody had to run, and the other
             * side's paperwork sitting on your own computer.
             *
             * What a team gets, it gets on <see cref="Customer.ServerAccept"/>, and it gets its own
             * copies. */
            RequestWriter.Name(wanted.Wanted, store, teamCount, _rows, this);

            int requestId = board.ServerCreate(_rows);

            if (requestId < 0)
            {
                /* A tier that asks for nothing leaves the customer standing there wanting
                 * nothing, and the cursor never moves past it — so this would repeat every time
                 * the spawner tried again. Said once, like every other mistake here. */
                ReportOnce($"tier {tier} of the RequestCatalogue asks for nothing, so the round cannot go on");
                return;
            }

            customer.ServerAssign(requestId);
        }

        /// <summary>
        /// How many customers are waiting on something.
        /// </summary>
        private int CountWaiting()
        {
            if (!EnsureCustomers())
                return 0;

            int waiting = 0;

            for (int i = 0; i < _customers.Length; i++)
            {
                if (IsSeat(_customers[i]) && _customers[i].HasRequest)
                    waiting++;
            }

            return waiting;
        }

        /// <summary>
        /// Finds a customer with nothing to do.
        /// </summary>
        /// <remarks>
        /// The first one found rather than the nearest or the longest idle. Which one steps forward
        /// is staging, and staging that depends on the order of a scene search is as good as any
        /// other — the customer that takes the job is whichever one the scene lists first, and it
        /// is the same one on every run.
        /// </remarks>
        private bool FindIdle(out Customer customer)
        {
            customer = null;

            if (!EnsureCustomers())
                return false;

            for (int i = 0; i < _customers.Length; i++)
            {
                Customer candidate = _customers[i];

                if (!IsSeat(candidate) || candidate.HasRequest)
                    continue;

                customer = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this one is a seat the spawner is responsible for filling.
        /// </summary>
        /// <remarks>
        /// One method rather than the same condition written twice, because the two callers have to
        /// agree: a candidate counted as waiting but never found as idle would make the office look
        /// staffed while nothing was ever seated in it, and the bug would look like a spawner that
        /// had stopped.
        ///
        /// The search returns derived types, so this is what keeps anything that authors its own
        /// request out of the queue — see <see cref="Customer.ProvidesOwnRequest"/>.
        /// </remarks>
        private static bool IsSeat(Customer candidate) => candidate != null && !candidate.ProvidesOwnRequest;

        /// <summary>
        /// Makes sure the customer list is there, and refreshes it if it is not.
        /// </summary>
        /// <remarks>
        /// Searched until it finds something rather than once and cached forever: the first server
        /// frames can run before every scene object has spawned, and a customer found too early is
        /// a customer the office would never seat again.
        /// </remarks>
        private bool EnsureCustomers()
        {
            if (_customers != null && _customers.Length > 0)
                return true;

            _customers = FindObjectsByType<Customer>(FindObjectsInactive.Exclude);

            if (_customers.Length > 0)
                return true;

            ReportOnce("no Customer in the scene");
            return false;
        }

        /// <summary>
        /// Says once that something is wrong.
        /// </summary>
        /// <remarks>
        /// Once per distinct complaint rather than once per attempt. This runs on a timer, so a
        /// scene with the catalogue left empty would otherwise fill the console with the same line
        /// every second and bury everything else being said.
        ///
        /// There is no path that clears this. Every complaint here is a setup or wiring mistake,
        /// and none of them can be fixed while the game is running — a scene object that is not
        /// there is not going to appear.
        /// </remarks>
        private void ReportOnce(string reason)
        {
            if (_reported == reason)
                return;

            _reported = reason;

            Debug.LogError($"{nameof(CustomerSpawner)} on {gameObject.name} cannot seat anybody: {reason}.", this);
        }
    }
}
