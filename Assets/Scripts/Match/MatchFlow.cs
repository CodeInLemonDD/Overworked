using System.Collections.Generic;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Where the round is.
    /// </summary>
    /// <remarks>
    /// Stored on the wire as a byte rather than as this type — the same rule
    /// <see cref="Containers.ContainerEntry.Kind"/> and <see cref="Npc.CustomerPhase"/> follow. The
    /// enum exists so call sites read as words; the field is a number so nothing has to reason
    /// about an enum's width.
    ///
    /// **There is no <c>Resetting</c>.** Clearing the world is not a state anything can observe: it
    /// happens inside one server frame and nothing reads the phase while it runs. A phase for it
    /// would be a state that exists for zero frames, which is a phase nobody can test.
    /// </remarks>
    public enum MatchPhase : byte
    {
        /// <summary>
        /// Before the whistle. Players are picking sides; the zones and their countdown are
        /// <see cref="MatchStarter"/>'s business.
        /// </summary>
        Lobby = 0,

        /// <summary>
        /// The round is running. The clock, and therefore how long is left, belongs to
        /// <see cref="ScoreBoard"/>.
        /// </summary>
        Playing = 1,

        /// <summary>
        /// The clock has run out. Logic has stopped, but **the office is still standing** — see
        /// the class remarks.
        /// </summary>
        Over = 2,
    }

    /// <summary>
    /// The round's sequence: when it is over, who won, and putting it all back for the next one.
    /// </summary>
    /// <remarks>
    /// Put on the same object as <see cref="ScoreBoard"/>, for the reason
    /// <see cref="MatchStarter"/> gives: the board is the scene object that already has a
    /// NetworkObject and is already about the round rather than about any one machine.
    ///
    /// **Three things it is deliberately not.** It is not the clock — <see cref="ScoreBoard"/>
    /// owns that, and this only watches it. It is not the match-maker — <see cref="MatchStarter"/>
    /// owns the zones and the countdown, and this only notices when that has started the round. And
    /// it does not know what a customer is beyond the one question it asks of every station, which
    /// is "put yourself back".
    ///
    /// **The office stays standing when the clock runs out.** This is the decision the whole class
    /// is shaped around. Stopping the round *and* clearing the room in the same breath would be
    /// simpler, and it is wrong: the settlement is already planned to be something the players do —
    /// walking to a boss, pressing things, watching something happen — and a ceremony needs a stage.
    /// So the clock stopping is one moment, and clearing the room is another one, later, and the
    /// gap between them is where the settlement goes.
    ///
    /// **Nothing clears itself on a timer.** The round ends and stays ended until somebody asks for
    /// the next one. During layout and the first play tests that somebody is the console's
    /// <c>restart</c>, which is the point: a round that restarted itself would take the aftermath
    /// away from whoever is looking at it. When the settlement exists it will call
    /// <see cref="ServerRestart"/> itself at the end of its own animation, and nothing here changes.
    ///
    /// **The phase is a published view, not the authority.** <see cref="ScoreBoard"/> already knows
    /// exactly when the round began and when its clock ran out, on the server, to the frame. This
    /// does not take that over — it watches the board and gives the answer a name, for the parts of
    /// the game that draw rather than decide. The split is the one the board already makes between
    /// its exact clock and its published one: **anything on the server that has to be exact asks
    /// the board** (the customer's clocks and the spawner both do, and both already hold the
    /// reference), and **anything drawing asks this**. Folding the two together would mean either a
    /// phase that lags a tenth of a second behind decisions, or a board that has to know what a
    /// lobby is — and the board is deliberately ignorant of how a round is played.
    ///
    /// What that leaves this class owning outright is the sequence: **when the round is cleared,
    /// what clearing means, and in what order.** That is the part nothing else can derive.
    /// </remarks>
    [DisallowMultipleComponent]
    public class MatchFlow : NetworkBehaviour
    {
        /// <summary>
        /// How often to look at the clock, in seconds.
        /// </summary>
        /// <remarks>
        /// A tenth of a second rather than every frame, on the same reasoning as
        /// <see cref="MatchStarter"/>'s poll: the answer changes twice a round, and the cost of
        /// being a tenth of a second late is that the office keeps working for a tenth of a second
        /// after the clock says zero. Nobody can see that, and the alternative is a comparison
        /// against the scoreboard every frame for the whole round.
        /// </remarks>
        [Tooltip("How often to look at the round clock, in seconds.")]
        [Min(0.02f)]
        [SerializeField]
        private float _pollSeconds = 0.1f;

        /// <summary>
        /// Where the round is. Replicated, so every peer draws the aftermath without guessing.
        /// </summary>
        private readonly SyncVar<byte> _phase = new((byte)MatchPhase.Lobby);

        /// <summary>
        /// Seconds until the next look at the clock.
        /// </summary>
        private float _timer;

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Reused by the clear, so restarting a round allocates nothing.
        /// </summary>
        private readonly List<NetworkGrabbable> _grabbables = new();

        /// <summary>
        /// The flow in the scene, or null before it has spawned.
        /// </summary>
        public static MatchFlow Instance { get; private set; }

        /// <summary>
        /// Where the round is.
        /// </summary>
        public MatchPhase Phase => (MatchPhase)_phase.Value;

        /// <summary>
        /// The round has been played out and is waiting to be cleared.
        /// </summary>
        /// <remarks>
        /// For drawing the aftermath, and for deciding whether there is anything to restart. **Not
        /// for deciding whether the round is still live on the server** — that question has an exact
        /// answer on <see cref="ScoreBoard"/>, and this one is up to a poll behind it. See the class
        /// remarks.
        /// </remarks>
        public bool IsOver => _phase.Value == (byte)MatchPhase.Over;

        /// <summary>
        /// Which team won, or <see cref="ScoreBoard.NoWinner"/> / <see cref="ScoreBoard.Draw"/>.
        /// </summary>
        /// <remarks>
        /// Read straight off the board rather than copied into a field here. Scores stop moving the
        /// moment the round is over — <see cref="ScoreBoard.ServerAward"/> refuses past that point —
        /// so the answer cannot change underneath a caller, and a second copy would only be a second
        /// thing that could be wrong.
        /// </remarks>
        public int Winner
        {
            get
            {
                ScoreBoard scores = ScoreBoard.Instance;
                return scores != null ? scores.Winner : ScoreBoard.NoWinner;
            }
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(MatchFlow)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; the two would disagree about whether the round is over.",
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

            /* A SyncVar on a scene NetworkObject survives between sessions — the component is the
             * same component — so a session that was stopped with the round over would come back
             * with the office frozen and nothing having said so. The same trap the customer's
             * request list has, and the same fix. */
            _phase.Value = (byte)MatchPhase.Lobby;
            _timer = 0f;

            /* Subscribed by hand rather than through TickNetworkBehaviour, for the reason the
             * scoreboard, the printer and the match starter all give: OnTick runs two or three
             * times a frame and may drop ticks, and this is watching a clock. */
            _timeManager = TimeManager;
            if (_timeManager != null)
                _timeManager.OnUpdate += ServerUpdate;
        }

        public override void OnStopServer()
        {
            if (_timeManager != null)
            {
                _timeManager.OnUpdate -= ServerUpdate;
                _timeManager = null;
            }

            base.OnStopServer();
        }

        /// <summary>
        /// Server: one look at where the round is.
        /// </summary>
        /// <remarks>
        /// **It watches rather than being told.** The round begins when <see cref="MatchStarter"/>
        /// calls <see cref="ScoreBoard.ServerBeginRound"/>, and this notices on its next poll
        /// instead of that call also having to remember to tell this. Two calls that must both
        /// happen is exactly the shape that ends with one of them missing.
        /// </remarks>
        private void ServerUpdate()
        {
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _timer -= deltaTime;
            if (_timer > 0f)
                return;

            _timer = _pollSeconds;

            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null)
                return;

            switch (Phase)
            {
                case MatchPhase.Lobby:
                    if (scores.HasStarted)
                        _phase.Value = (byte)MatchPhase.Playing;
                    return;

                case MatchPhase.Playing:
                    if (scores.IsOver)
                        EnterOver();
                    return;

                /* Over. Nothing happens until somebody asks for the next round — see the class
                 * remarks for why that is not an oversight. */
                default:
                    return;
            }
        }

        /// <summary>
        /// Server: the round is played out.
        /// </summary>
        /// <remarks>
        /// Setting the phase and saying who won is the whole of it. Nothing is stopped here, and
        /// that is not an omission: the board refuses to move a score once its own clock says zero,
        /// the customers read that same clock, and the spawner will not seat anybody past it — so
        /// the round is already frozen by the time this runs. What this adds is a name for the state
        /// and a line in the log saying the round is there to be restarted.
        /// </remarks>
        private void EnterOver()
        {
            _phase.Value = (byte)MatchPhase.Over;

            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null)
                return;

            int winner = scores.Winner;

            if (winner == ScoreBoard.Draw)
                Debug.Log($"{nameof(MatchFlow)}: the round is over on a draw.");
            else if (winner >= 0)
                Debug.Log($"{nameof(MatchFlow)}: the round is over, team {winner} won. Type 'restart' for the next one.");
            else
                Debug.Log($"{nameof(MatchFlow)}: the round is over. Type 'restart' for the next one.");
        }

        /// <summary>
        /// Server: clears the office and puts the round back before the whistle.
        /// </summary>
        /// <remarks>
        /// **This is the whole of "the next round", and it is one call on purpose.** The settlement
        /// that is coming will end by calling this; the console calls it today; a button on a
        /// results panel would call it tomorrow. None of those should have to know the order below,
        /// and none of them should be able to get the order wrong.
        ///
        /// **Allowed from any phase, not just from the end.** A host whose round has fallen apart —
        /// somebody dropped, the scene is jammed, a customer is stuck — needs to be able to start
        /// again without restarting the process, and a rule that only permitted it after the final
        /// whistle would be a rule that stops exactly the person who is already having a bad time.
        /// It is still a server-only call, so it is not something a client can do to a round.
        ///
        /// **Everything that says "this round" is put back**, in this order, and the order is not
        /// arbitrary — see <see cref="ClearWorld"/>.
        /// </remarks>
        /// <returns>False when there is no board to reset, which means nothing else is wired either.</returns>
        [Server]
        public bool ServerRestart()
        {
            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null)
                return false;

            ClearWorld();

            /* Last, and after the clearing rather than before it. The board's own reset is what
             * tells the round it is back in the lobby — HasStarted goes false and the match
             * starter starts looking at the zones again — so doing it first would put the round in
             * a state where it is accepting players while the office is still full of the last
             * one's paperwork. */
            scores.ServerReset();

            _phase.Value = (byte)MatchPhase.Lobby;
            _timer = 0f;

            return true;
        }

        /// <summary>
        /// Server: takes everything that belonged to the round back out of the office.
        /// </summary>
        /// <remarks>
        /// Six steps, and the first one has to be first.
        ///
        /// **The match-making is stopped before anything is taken apart.** A starter that was still
        /// departing would not look at the zones again, and one still counting down would begin a
        /// round in the middle of this. Neither is a state to leave the scene in while it is being
        /// dismantled underneath them.
        ///
        /// **Loose objects go before the stations are asked to put themselves back.** Every
        /// grabbable in the world carries a folder container, which means a sweep for containers
        /// finds the entire world rather than the machines — the trap CONSTRAINTS.md describes. It
        /// stops being a trap once the grabbables are gone, because then the only containers left
        /// *are* the machines'. So: destroy the world's loose things, and what remains is the office.
        ///
        /// **The office is put back the way it was authored**, which is the one arrangement every
        /// later round measures its staging from — see <see cref="OfficeLayout.ServerReset"/>. It
        /// goes after the stations have been asked to reset their own state, because it moves them
        /// and a station being moved is not a station mid-reset.
        ///
        /// **Documents go last** for the mirror-image reason. A document id is valid until the store
        /// forgets it, and four other things hold ids — the request rows, the unlock list, a
        /// printer's queue and the data id on an object in somebody's hands. The store is what turns
        /// an id into something, so forgetting them is safe only once nothing else is still holding
        /// one, and the steps above are exactly the things that do.
        /// </remarks>
        private void ClearWorld()
        {
            /* First, so that nothing is still watching for people to stand in zones or waiting on a
             * deployment while the room is taken apart. */
            MatchStarter.Instance?.ServerReset();

            /* Hands too, not just the floor. An object being carried is still an object from the
             * last round, and leaving it would start the next one with somebody already holding a
             * finished delivery. Despawning it out of their hands is safe: the player notices the
             * object is gone and releases the carry on its own — see PlayerInteraction's carry
             * check, which exists for exactly this case. */
            if (NetworkManager != null)
                GrabbableSpawner.DespawnAllGrabbables(NetworkManager, _grabbables);

            /* Asked rather than swept. Every kind of station has some state of its own that a
             * container sweep would not reach — a printer mid-job, a computer mid-download, a
             * customer holding a request — and the only thing that knows what that state is, is the
             * station. A new station gets a reset by overriding one method; the alternative is
             * coming back here every time one is added, and that is the step that gets forgotten. */
            StationBase[] stations = FindObjectsByType<StationBase>(FindObjectsInactive.Exclude);
            for (int i = 0; i < stations.Length; i++)
            {
                if (stations[i] != null)
                    stations[i].ServerReset();
            }

            OfficeLayout.Instance?.ServerReset();

            RequestBoard.Instance?.ServerClear();
            DocumentUnlocks.Instance?.ServerResetUnlocks();
            DocumentStore.Instance?.ServerClear();
        }
    }
}
