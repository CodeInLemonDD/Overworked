using System;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Who is winning, and how long is left.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject. It is the round's clock and its tally,
    /// and **it knows nothing about how the round is played**: it does not know what a customer
    /// is, who delivered what, or why points moved. Something else decides that and calls
    /// <see cref="ServerAward"/>. Keeping it that way is what lets the same board keep score for
    /// a mode that has no customers in it at all.
    ///
    /// **The clock is server-authoritative.** Every peer could count down from a start time and
    /// most of the time they would agree, but "most of the time" is not good enough for the thing
    /// that decides when the round is over — a client whose countdown drifted by a second would
    /// see the round end while the server was still accepting deliveries, or the other way round.
    /// So the server owns the number and the clients display what they are told.
    ///
    /// That is the opposite of the decision made for the computer's download progress, and the
    /// difference is worth keeping straight: the download is a pure function of when it started,
    /// so every peer computing it locally is free and correct. This is not a function of anything
    /// a client can see, because the round can be extended, reset, or ended early by the server.
    ///
    /// **The server writes a few times a second, not every frame.** A SyncVar sends on change, so
    /// assigning it every frame would put a float on the wire every frame for a number the client
    /// can interpolate perfectly well from two of them. The server keeps the exact value for its
    /// own decisions — when the round is over — and publishes it on a cadence.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ScoreBoard : NetworkBehaviour
    {
        /// <summary>
        /// What <see cref="Winner"/> reads while the round is still running.
        /// </summary>
        public const int NoWinner = -1;

        /// <summary>
        /// What <see cref="Winner"/> reads when the top two teams are level.
        /// </summary>
        public const int Draw = -2;

        /// <summary>
        /// How many teams are being scored.
        /// </summary>
        /// <remarks>
        /// Drives the length of the score list. Two this round, and serialized rather than
        /// hardcoded because the list has to be exactly this long or <see cref="ScoreOf"/>
        /// quietly answers zero for a team that exists.
        /// </remarks>
        [Tooltip("How many teams are being scored. The score list is this long.")]
        [Min(1)]
        [SerializeField]
        private int _teamCount = 2;

        /// <summary>
        /// How long a round lasts, in seconds.
        /// </summary>
        [Tooltip("How long a round lasts, in seconds.")]
        [Min(1f)]
        [SerializeField]
        private float _roundSeconds = 300f;

        /// <summary>
        /// How often the server publishes the clock, in seconds.
        /// </summary>
        /// <remarks>
        /// The tuning knob for the one trade this class makes. Smaller is smoother and chattier;
        /// at a fifth of a second a client is never more than a fifth of a second stale, which no
        /// player can see on a countdown displayed to the second, and it costs five small writes
        /// a second per client.
        /// </remarks>
        [Tooltip("How often the server publishes the clock, in seconds.")]
        [Min(0.01f)]
        [SerializeField]
        private float _writeInterval = 0.2f;

        /// <summary>
        /// One score per team, indexed by team.
        /// </summary>
        /// <remarks>
        /// A list rather than a SyncVar per team: the number of teams is a setting, and a field
        /// per team would mean the setting and the fields could disagree. Reading and writing go
        /// through <see cref="ServerAward"/>, which is where the shape FishNet requires is
        /// spelled out.
        /// </remarks>
        private readonly SyncList<int> _scores = new();

        /// <summary>
        /// Seconds left in the round, as published to clients.
        /// </summary>
        /// <remarks>
        /// Not the value the server decides with — see <see cref="_exactRemaining"/>. Anything
        /// reading the clock for display wants <see cref="Remaining"/>, which picks the right one
        /// for the peer it is running on.
        /// </remarks>
        private readonly SyncVar<float> _remaining = new(0f);

        /// <summary>
        /// Whether the round has begun. Replicated, so every peer can tell "waiting for players"
        /// from "playing" without guessing from the clock.
        /// </summary>
        /// <remarks>
        /// The clock reading its full length is not the same fact — a board that has just been
        /// reset is also showing the full length, and one of those two states is a match about to
        /// start and the other is a match that has not been asked to. Only the server moves this,
        /// through <see cref="ServerBeginRound"/>.
        /// </remarks>
        private readonly SyncVar<bool> _started = new(false);

        /// <summary>
        /// Seconds left in the round, exactly, on the server.
        /// </summary>
        /// <remarks>
        /// The server cannot decide the round is over from <see cref="_remaining"/>, because that
        /// is only refreshed every <see cref="_writeInterval"/> and would end the round up to a
        /// fifth of a second late — or, worse, would keep accepting deliveries for a fifth of a
        /// second after the clock on screen says zero. So the truth lives here, and the SyncVar
        /// is a publication of it.
        /// </remarks>
        private float _exactRemaining;

        /// <summary>
        /// Seconds since the clock was last published.
        /// </summary>
        private float _writeTimer;

        /// <summary>
        /// Whether the clock is running.
        /// </summary>
        /// <remarks>
        /// A separate flag rather than "is the remaining time above zero", because those are two
        /// different facts at the moment the round ends and they must not be confused: a stopped
        /// clock and an expired one both read zero, and only one of them should fire the
        /// end-of-round notice again on the next frame.
        /// </remarks>
        private bool _running;

        /// <summary>
        /// The last over/not-over state raised to <see cref="ScoresChanged"/>.
        /// </summary>
        /// <remarks>
        /// The clock changes on the wire several times a second, and every one of those is a
        /// redraw of a number nobody needs an event for — they poll it. What is worth an event is
        /// the round ending, because that is when <see cref="IsOver"/> and <see cref="Winner"/>
        /// change and a scoreboard has to rearrange itself. Tracking the last state seen is what
        /// collapses the stream of clock writes into the one transition that matters.
        /// </remarks>
        private bool _reportedOver;

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// The board in the scene, or null before it has spawned.
        /// </summary>
        public static ScoreBoard Instance { get; private set; }

        /// <summary>
        /// How many teams are being scored.
        /// </summary>
        public int TeamCount => _teamCount;

        /// <summary>
        /// Seconds left in the round.
        /// </summary>
        /// <remarks>
        /// The exact value on the server and the last published one everywhere else. The
        /// asymmetry is deliberate and harmless: nothing client-side makes a decision from this,
        /// and a countdown shown to the second cannot display a fifth of a second of staleness.
        /// </remarks>
        public float Remaining => IsServerInitialized ? _exactRemaining : _remaining.Value;

        /// <summary>
        /// True when the round is over.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="Remaining"/> rather than sent as its own flag, so the two can
        /// never disagree — a board that said "over" while showing time left would be a bug with
        /// no good explanation.
        /// </remarks>
        public bool IsOver => Remaining <= 0f;

        /// <summary>
        /// True once the round has begun.
        /// </summary>
        /// <remarks>
        /// False until <see cref="ServerBeginRound"/>, which is what a match waiting on its
        /// players looks like. Anything that should not be happening before the whistle reads this
        /// — the customer spawner is the one that matters, because a round that seated customers
        /// during the wait would be spending its own time limit on the wait.
        /// </remarks>
        public bool HasStarted => _started.Value;

        /// <summary>
        /// The team with the highest score once the round is over.
        /// </summary>
        /// <remarks>
        /// <see cref="NoWinner"/> while the round is running, <see cref="Draw"/> for a tie —
        /// including the tie where nobody scored anything, which is the honest reading of a round
        /// both teams slept through.
        ///
        /// Computed on read rather than stored, because it is a function of two things that are
        /// already here and a third copy could disagree with them.
        /// </remarks>
        public int Winner
        {
            get
            {
                if (!IsOver || _teamCount <= 0)
                    return NoWinner;

                int best = NoWinner;
                int bestScore = int.MinValue;
                bool tied = false;

                for (int team = 0; team < _teamCount; team++)
                {
                    int score = ScoreOf(team);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = team;
                        tied = false;
                    }
                    else if (score == bestScore)
                    {
                        tied = true;
                    }
                }

                return tied ? Draw : best;
            }
        }

        /// <summary>
        /// Raised on every peer when a score changes, and once when the round ends.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. The clock is deliberately not part of this — a countdown
        /// is polled by whatever draws it, and firing an event several times a second to redraw
        /// one number is more machinery than the number is worth.
        /// </remarks>
        public event Action ScoresChanged;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(ScoreBoard)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; scores will disagree.",
                    this);
            }

            Instance = this;

            _scores.OnChange += OnScoresChanged;
            _remaining.OnChange += OnRemainingChanged;
        }

        public override void OnStopNetwork()
        {
            _scores.OnChange -= OnScoresChanged;
            _remaining.OnChange -= OnRemainingChanged;

            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Subscribed by hand rather than through TickNetworkBehaviour, for the same reason
             * the printer does it: this has to stay a plain NetworkBehaviour, and OnTick is no
             * substitute — it runs two or three times a frame and may drop ticks, which is no
             * basis for a clock. */
            _timeManager = TimeManager;
            if (_timeManager != null)
                _timeManager.OnUpdate += ServerUpdate;

            /* The round starts when the server does. A real match would want a lobby and a count
             * in, and that is a later round; what matters today is that the clock exists and can
             * be reset, so a round can be restarted from the console without a reload. */
            ServerReset();
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
        /// How many points a team has.
        /// </summary>
        /// <remarks>
        /// Zero for a team that does not exist, rather than an exception or a sentinel. A HUD
        /// asking about team 3 on a two-team board has made no mistake worth crashing over, and
        /// zero is what it would draw anyway.
        ///
        /// **Scores can go negative.** A team that loses more customers than it serves ends the
        /// round below zero, and that is left alone rather than floored: a floor would quietly
        /// turn the patience penalty into a no-op for a team that is already losing, which is
        /// exactly the team the penalty is for.
        /// </remarks>
        public int ScoreOf(int team)
        {
            if (team < 0 || team >= _scores.Count)
                return 0;

            return _scores[team];
        }

        /// <summary>
        /// Server: moves a team's score.
        /// </summary>
        /// <remarks>
        /// Negative points are expected — the patience penalty is five off — so this is a single
        /// method rather than an award and a deduct that would have to be kept in step.
        ///
        /// Refused once the round is over. Nothing should be scoring after the whistle, and a
        /// customer left mid-delivery at the final frame is a real case that would otherwise move
        /// the result after the result was decided.
        /// </remarks>
        [Server]
        public void ServerAward(int team, int points)
        {
            if (team < 0 || team >= _scores.Count)
                return;

            if (!HasStarted || IsOver)
                return;

            /* Read the element out, change the copy, write it back. The obvious
             * `_scores[team]++` does not compile (CS1612), and the version that does compile —
             * reaching the backing list through GetCollection and indexing into it — silently
             * never marks the value dirty, so the score would move on the server and nowhere
             * else. This is the only shape that both compiles and syncs. */
            int score = _scores[team];
            _scores[team] = score + points;
        }

        /// <summary>
        /// Server: puts every score back to zero and restarts the clock.
        /// </summary>
        /// <remarks>
        /// The list is rebuilt rather than trimmed, because its length is what says how many
        /// teams there are — a reset that left it short would leave <see cref="ScoreOf"/>
        /// answering zero for a team that exists, which reads as "that team is losing" rather
        /// than "the board is broken".
        ///
        /// Resetting the clock here rather than in a separate call, because a board with zeroed
        /// scores and no time on it is not a state anything wants to be in.
        ///
        /// **It does not start the round.** A reset is what a board does before a match — every
        /// score back to zero, the full time on the clock, nobody moving — and the moment it
        /// starts is <see cref="ServerBeginRound"/>, which something else decides. Folding the two
        /// together was the first thing this did and it was wrong for a reason that is only visible
        /// from outside: the round would begin the instant the server did, so a match that waits
        /// for its players to pick sides would be spending its five minutes on the wait.
        /// </remarks>
        [Server]
        public void ServerReset()
        {
            _scores.Clear();

            for (int i = 0; i < _teamCount; i++)
                _scores.Add(0);

            _exactRemaining = _roundSeconds;
            _writeTimer = 0f;
            _running = false;
            _started.Value = false;

            _remaining.Value = _roundSeconds;
        }

        /// <summary>
        /// Server: starts the clock.
        /// </summary>
        /// <remarks>
        /// Called once, by <see cref="Match.MatchStarter"/>, at the moment the match actually
        /// begins. Marked [Server] rather than folded into <see cref="ServerReset"/> for the reason
        /// that method's remarks give.
        /// </remarks>
        [Server]
        public void ServerBeginRound()
        {
            if (_started.Value)
                return;

            _exactRemaining = _roundSeconds;
            _writeTimer = 0f;
            _running = true;
            _started.Value = true;

            _remaining.Value = _roundSeconds;
        }

        /// <summary>
        /// Server: one frame of the clock.
        /// </summary>
        private void ServerUpdate()
        {
            if (!_running)
                return;

            /* Unscaled, per the project's timer rule: this is a round length, and a dropped frame
             * or a paused editor must not change how long the round is. */
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _exactRemaining = Mathf.Max(_exactRemaining - deltaTime, 0f);

            if (_exactRemaining <= 0f)
            {
                /* The final value is published immediately rather than waiting for the next
                 * write slot: a client sitting on a stale 0.1 would keep showing time left on a
                 * round that is already decided. */
                _running = false;
                _remaining.Value = 0f;
                return;
            }

            _writeTimer += deltaTime;
            if (_writeTimer < _writeInterval)
                return;

            _writeTimer = 0f;
            _remaining.Value = _exactRemaining;
        }

        /// <summary>
        /// Raises <see cref="ScoresChanged"/> exactly once per score change on every peer.
        /// </summary>
        /// <remarks>
        /// A host receives every change twice: once as the server's own write (asServer true) and
        /// once as the echoed client read (asServer false). Prefer the client echo wherever this
        /// peer runs a client, which covers hosts and pure clients, and fall back to the server
        /// call on a dedicated server where no echo exists.
        /// </remarks>
        private void OnScoresChanged(SyncListOperation op, int index, int oldItem, int newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            ScoresChanged?.Invoke();
        }

        /// <summary>
        /// Raises <see cref="ScoresChanged"/> when the round ends, and only then.
        /// </summary>
        /// <remarks>
        /// This fires on every published clock value, several times a second, and almost all of
        /// those are noise — see <see cref="_reportedOver"/>. The same asServer guard as the score
        /// list, for the same reason.
        /// </remarks>
        private void OnRemainingChanged(float prev, float next, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            bool over = next <= 0f;
            if (over == _reportedOver)
                return;

            _reportedOver = over;
            ScoresChanged?.Invoke();
        }
    }
}
