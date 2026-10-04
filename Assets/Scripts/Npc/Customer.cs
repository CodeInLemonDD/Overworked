using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Containers;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Npc
{
    /// <summary>
    /// Where one team stands with one customer.
    /// </summary>
    /// <remarks>
    /// Stored on the wire as a byte rather than as this type — the same rule
    /// <see cref="Containers.ContainerEntry.Kind"/> follows. The enum exists so call sites read as
    /// words; the field is a number so nothing has to reason about an enum's width.
    /// </remarks>
    public enum CustomerPhase : byte
    {
        /// <summary>
        /// Has not taken the job. Free to take it, and the waiting clock is what is running.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Took the job. Runs a patience clock of its own and is the only state that can deliver.
        /// </summary>
        Working = 1,

        /// <summary>
        /// Ran out of time. Cannot deliver this customer at all, though the other team still can.
        /// </summary>
        Failed = 2,
    }

    /// <summary>
    /// One customer: what they want, how long they will wait, and who has taken the job.
    /// </summary>
    /// <remarks>
    /// **Two things at once, and that is the whole of the design.** It is a
    /// <see cref="StationBase"/>, so E reaches it and taking the job is a press; and it is an
    /// intake, so a folder dropped or thrown at it is a delivery. Those cannot be the same verb
    /// in this project — a press only becomes a station interaction when there is nothing to pick
    /// up, so an object that can be picked up can never also be the target of a press. See
    /// CONSTRAINTS.md, "进料型工位没有主动动词".
    ///
    /// **A request is a name, not a document.** Both teams have their own Excel 2, and this
    /// customer will take either one — whoever gets here first. That is what makes the round a
    /// race rather than two parallel jobs, and it is why nothing here records who ordered
    /// anything. See <see cref="DocumentRequest"/>.
    ///
    /// **The clocks are server-authoritative.** The waiting clock and the per-team patience clock
    /// decide who is out and who is paid, and neither is a function of anything a client can see,
    /// so the server owns them and publishes them — the same arrangement as
    /// <see cref="ScoreBoard"/>, and for the same reason. The exact values live beside the
    /// published ones because a delivery must not be accepted for a fifth of a second after the
    /// clock on screen says zero.
    ///
    /// **Two teams, one customer, winner takes all.** Both may take the job, both may be working
    /// on it at once, and the first folder that arrives ends it for the other. The loser keeps
    /// their documents and has wasted the trip; that is the mechanic, not an oversight.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Customer : StationBase
    {
        [Header("Clocks")]

        /// <summary>
        /// How long the customer waits for any team to take the job, in seconds.
        /// </summary>
        /// <remarks>
        /// Runs whenever nobody is working on this customer and not every team is out. It is not
        /// "the clock that starts when the customer appears" — see <see cref="AdvanceClocks"/>.
        /// </remarks>
        [Tooltip("How long the customer waits for a team to take the job. Seconds.")]
        [Min(1f)]
        [SerializeField]
        private float _waitingSeconds = 20f;

        /// <summary>
        /// How long a team has to deliver once it has taken the job, in seconds.
        /// </summary>
        [Tooltip("How long a team has to deliver once it has taken the job. Seconds.")]
        [Min(1f)]
        [SerializeField]
        private float _patienceSeconds = 60f;

        /// <summary>
        /// How often the server publishes the clocks, in seconds.
        /// </summary>
        /// <remarks>
        /// The same trade <see cref="ScoreBoard"/> makes: a SyncVar sends on change, so assigning
        /// every frame would put floats on the wire every frame for numbers a label can do without
        /// being fresher than a tenth of a second.
        /// </remarks>
        [Tooltip("How often the server publishes the clocks, in seconds.")]
        [Min(0.01f)]
        [SerializeField]
        private float _writeInterval = 0.2f;

        [Header("Scoring")]

        /// <summary>
        /// What the first team to deliver is paid.
        /// </summary>
        [Tooltip("Points for delivering a full folder.")]
        [SerializeField]
        private int _rewardPoints = 10;

        /// <summary>
        /// What a team loses when its clock runs out.
        /// </summary>
        /// <remarks>
        /// Positive here and subtracted at the call site, so the two numbers read as "ten for this,
        /// five against that" rather than as a ten and a minus five.
        /// </remarks>
        [Tooltip("Points deducted when a team runs out of time or lets the customer walk.")]
        [SerializeField]
        private int _penaltyPoints = 5;

        [Header("Delivery")]

        /// <summary>
        /// Middle of the box a folder has to land in, in this object's own space.
        /// </summary>
        /// <remarks>
        /// Deliberately centred low and sized generously. A delivery is a folder thrown at a
        /// person, and a thrown folder ends up on the floor at their feet — a box around the
        /// chest alone would only accept a folder that was still falling when it arrived, which
        /// would read as the customer refusing deliveries for no reason.
        /// </remarks>
        [Tooltip("Middle of the delivery box, in this object's own space. Low and wide: the folder lands at their feet.")]
        [SerializeField]
        private Vector3 _intakeCentre = new(0f, 0.5f, 0f);

        /// <summary>
        /// Half the size of the delivery box, in this object's own space.
        /// </summary>
        [Tooltip("Half the size of the delivery box. Drawn as a gizmo when this object is selected.")]
        [SerializeField]
        private Vector3 _intakeHalfExtents = new(0.9f, 0.7f, 0.9f);

        [Header("Presence")]

        /// <summary>
        /// Where the customer stands while it has no request, relative to where it is placed.
        /// </summary>
        /// <remarks>
        /// **Zero means the customer never moves**, which is the default: it is a piece of staging,
        /// and a scene that has not asked for it should not get it.
        ///
        /// Set, it is applied on every peer from replicated state rather than on the server and
        /// replicated, because a scene object has no NetworkTransform — a customer moved on the
        /// server alone would walk on the host's screen and stand still on everyone else's. Every
        /// peer deriving the same position from the same replicated request state is what makes it
        /// visible to all of them without one.
        /// </remarks>
        [Tooltip("Where the customer stands while it has no request, relative to where it is placed. Zero means it never moves.")]
        [SerializeField]
        private Vector3 _idleOffset = Vector3.zero;

        /// <summary>
        /// How fast the customer crosses between the two spots, in metres per second.
        /// </summary>
        [Tooltip("How fast the customer steps aside and back, in metres per second.")]
        [Min(0.1f)]
        [SerializeField]
        private float _moveSpeed = 2f;

        /// <summary>
        /// Which request this customer is waiting on, or -1 when it has none.
        /// </summary>
        /// <remarks>
        /// Replicated because it is what every label asks first. -1 is the ordinary state of a
        /// customer between jobs, and callers read it as "nothing here" rather than as an error.
        ///
        /// It is also the cursor into <see cref="RequestBoard"/>, so a request that has been
        /// removed from the board leaves this pointing at nothing. <see cref="HasRequest"/> asks
        /// the board, not just the number, for that reason.
        /// </remarks>
        private readonly SyncVar<int> _requestId = new(-1);

        /// <summary>
        /// What each team is doing, one entry per team.
        /// </summary>
        /// <remarks>
        /// A list rather than a field per team, for the reason <see cref="ScoreBoard"/> gives about
        /// its scores: the number of teams is a setting, and a field per team would be a second
        /// place for that setting to live and disagree.
        ///
        /// Bytes rather than <see cref="CustomerPhase"/>, because nothing that crosses the wire in
        /// this project is typed as an enum.
        /// </remarks>
        private readonly SyncList<byte> _phases = new();

        /// <summary>
        /// Each team's remaining patience, as published to clients.
        /// </summary>
        /// <remarks>
        /// Zero for a team that is not working. The live value is
        /// <see cref="_exactRemaining"/>; anything drawing a clock wants
        /// <see cref="RemainingFor"/>, which picks the right one for the peer it is on.
        /// </remarks>
        private readonly SyncList<float> _remaining = new();

        /// <summary>
        /// How long is left before the customer walks out, as published to clients.
        /// </summary>
        private readonly SyncVar<float> _waiting = new(0f);

        /// <summary>
        /// Each team's remaining patience, exactly, on the server.
        /// </summary>
        /// <remarks>
        /// The server cannot decide a team has failed from the published value, because that is
        /// only refreshed every <see cref="_writeInterval"/> — a delivery arriving in that window
        /// would be accepted after the clock it was racing had already run out.
        /// </remarks>
        private readonly List<float> _exactRemaining = new();

        /// <summary>
        /// How long is left before the customer walks out, exactly, on the server.
        /// </summary>
        private float _exactWaiting;

        /// <summary>
        /// Whether the waiting clock is currently running.
        /// </summary>
        /// <remarks>
        /// Tracked so the clock can be <em>restarted</em> rather than merely resumed the moment
        /// nobody is working on this customer again. Without it, a team that took the job and ran
        /// out would leave the other team with no clock at all — the customer would stand there
        /// holding a place in the scene while the remaining team took as long as it liked.
        /// </remarks>
        private bool _waitingRunning;

        /// <summary>
        /// Seconds since the clocks were last published.
        /// </summary>
        private float _writeTimer;

        /// <summary>
        /// Where this customer was placed, which is where it stands while serving.
        /// </summary>
        private Vector3 _homePosition;

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Reused by the delivery scan. See <see cref="IntakeVolume.CollectInside"/>.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        /// <summary>
        /// Reused by <see cref="Meets"/> for the rows of the request being checked.
        /// </summary>
        private readonly List<DocumentRequest> _wantedBuffer = new();

        /// <summary>
        /// Reused by <see cref="Meets"/> for the folder entries already accounted for.
        /// </summary>
        /// <remarks>
        /// A document satisfies one row and one row only, so a customer asking for two contracts
        /// cannot be paid with one folder holding the same contract twice. Kept as a list of
        /// positions rather than a copy of the entries: the entries are not what is being matched
        /// against, the rows are, and this only has to answer "have I used this one yet".
        /// </remarks>
        private readonly List<int> _matched = new();

        /// <summary>
        /// Which request this customer is waiting on, or -1 when it has none.
        /// </summary>
        public int RequestId => _requestId.Value;

        /// <summary>
        /// How many teams there are to have a phase each.
        /// </summary>
        /// <remarks>
        /// Taken from <see cref="ScoreBoard"/> rather than serialized here. Two numbers that have
        /// to agree is one number too many, and the board is the one that already knows — a
        /// customer with a phase for a team that does not exist would be a clock nobody can see,
        /// and a customer missing a phase for one that does would be a team that can never take
        /// the job.
        /// </remarks>
        public int TeamCount => ScoreBoard.Instance != null ? ScoreBoard.Instance.TeamCount : 0;

        /// <summary>
        /// True when this customer is waiting on a request that is still on the board.
        /// </summary>
        /// <remarks>
        /// Asked of the board as well as of the number, because the number outlives the request:
        /// a customer that has just been paid has a stale id for the frame or so before it is
        /// cleared, and a label reading that would draw the order it has already had filled.
        /// </remarks>
        public bool HasRequest
        {
            get
            {
                if (_requestId.Value < 0)
                    return false;

                RequestBoard board = RequestBoard.Instance;
                if (board == null)
                    return true;

                /* Scanned rather than asked through the buffer Meets uses. Both are called from a
                 * label and from a delivery, and on a host those are the same process — one shared
                 * scratch list between two public members is a trap waiting for the first caller
                 * that nests them. */
                for (int i = 0; i < board.Count; i++)
                {
                    if (board.TryGet(i, out DocumentRequest row) && row.RequestId == _requestId.Value)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// How long is left before the customer walks out.
        /// </summary>
        public float WaitingRemaining => IsServerInitialized ? _exactWaiting : _waiting.Value;

        private void Awake()
        {
            /* Where it was placed is where it serves from; the idle spot is an offset from it, so
             * the scene only has to say where the customer belongs. Read once — nothing else moves
             * this object, and reading it every frame would let a frame of movement become the new
             * home. */
            _homePosition = transform.position;
        }

        /// <summary>
        /// Where a team stands with this customer.
        /// </summary>
        /// <remarks>
        /// <see cref="CustomerPhase.Idle"/> for a team that does not exist, matching
        /// <see cref="ScoreBoard.ScoreOf"/> answering zero: a caller asking after team 3 has made
        /// no mistake worth an exception, and idle is the answer that offers nothing.
        /// </remarks>
        public CustomerPhase PhaseOf(int team)
        {
            if (team < 0 || team >= _phases.Count)
                return CustomerPhase.Idle;

            return (CustomerPhase)_phases[team];
        }

        /// <summary>
        /// How long a team has left, in seconds. Zero when it is not working.
        /// </summary>
        public float RemainingFor(int team)
        {
            if (team < 0)
                return 0f;

            if (IsServerInitialized)
                return team < _exactRemaining.Count ? _exactRemaining[team] : 0f;

            return team < _remaining.Count ? _remaining[team] : 0f;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* A SyncList on a scene NetworkObject survives between sessions — the component is the
             * same component — so a customer that was mid-job when Play was last stopped would come
             * back holding that job. Cleared here rather than left to the sizing below, which only
             * rebuilds the lists when their length is wrong. */
            GoIdle();

            /* Subscribed by hand rather than through TickNetworkBehaviour, for the reason the
             * printer and the scoreboard both give: this has to stay a plain NetworkBehaviour, and
             * OnTick is no basis for a clock — it runs two or three times a frame and may drop
             * ticks. */
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
        /// Server: one frame of the customer.
        /// </summary>
        private void ServerUpdate()
        {
            EnsureTeamSlots();

            if (_requestId.Value < 0)
                return;

            /* Unscaled, per the project's timer rule: these are clocks, and a dropped frame or a
             * paused editor must not change how long a customer waits. */
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            AdvanceClocks(deltaTime);

            /* The clocks may have just walked the customer out, and a customer with no request has
             * nothing to accept a folder for. */
            if (_requestId.Value < 0)
                return;

            ScanForDeliveries();

            _writeTimer += deltaTime;
            if (_writeTimer < _writeInterval)
                return;

            _writeTimer = 0f;
            PublishClocks();
        }

        /// <summary>
        /// Server: gives this customer a request to wait on.
        /// </summary>
        /// <remarks>
        /// Not part of the frozen interface, because it is not a seam anything outside this
        /// window's two files crosses: the spawner is the only caller, and a customer is given a
        /// request rather than choosing one. It is here rather than on the spawner because the
        /// state it resets is all the customer's.
        /// </remarks>
        /// <param name="requestId">The request the spawner has just written.</param>
        [Server]
        public void ServerAssign(int requestId)
        {
            if (requestId < 0)
                return;

            EnsureTeamSlots();

            _requestId.Value = requestId;
            _writeTimer = 0f;

            for (int team = 0; team < _phases.Count; team++)
            {
                _phases[team] = (byte)CustomerPhase.Idle;
                _exactRemaining[team] = 0f;
                _remaining[team] = 0f;
            }

            StartWaiting();
        }

        /// <summary>
        /// Server: a player pressed E on this customer, meaning to take the job.
        /// </summary>
        /// <remarks>
        /// **Pressing E again does not restart the clock.** That is the whole reason this refuses a
        /// team that is already working: the patience clock is the pressure the round is built on,
        /// and a key that reset it would turn the pressure into a key to mash.
        ///
        /// False also covers the other two ways there is nothing to take — no request, and a team
        /// that has already run out. The customer does not answer either; the label over its head
        /// is what says why.
        /// </remarks>
        /// <returns>True when this press is what took the job.</returns>
        [Server]
        public bool ServerAccept(int team)
        {
            if (_requestId.Value < 0)
                return false;
            if (team < 0 || team >= _phases.Count)
                return false;

            if ((CustomerPhase)_phases[team] != CustomerPhase.Idle)
                return false;

            _phases[team] = (byte)CustomerPhase.Working;
            _exactRemaining[team] = _patienceSeconds;

            /* Published at once rather than at the next write slot: this is the frame the label
             * changes from the waiting clock to this team's own, and a fifth of a second of the
             * wrong clock reads as the press having done nothing. */
            _remaining[team] = _patienceSeconds;

            return true;
        }

        /// <summary>
        /// Does this folder satisfy the request, for this team?
        /// </summary>
        /// <remarks>
        /// **Pure judgement.** Nothing here changes the world, empties the folder or pays anybody —
        /// <see cref="ServerDeliver"/> is what does that. The split exists so the interesting part
        /// can be asked on its own, and this is the part most likely to be wrong: it is the only
        /// place in the round where a name, a number and a team all have to agree at once.
        ///
        /// Matched against the **rows** rather than against document ids, because a request does
        /// not name documents — it names a kind and a number, and both teams have one of each. The
        /// team is what decides which of the two copies satisfies it, and the folder has to be that
        /// team's as well, or a player could carry the other side's folder over and be paid for it.
        ///
        /// **One document satisfies one row.** A request for two contracts is not satisfied by one
        /// contract in the folder twice over, which is what the scratch list of used entries is
        /// for.
        ///
        /// Uses a shared buffer, so it is not re-entrant. Every caller is on the server and none
        /// of them nest.
        /// </remarks>
        /// <param name="team">The team doing the delivering.</param>
        /// <param name="folder">The folder being handed over.</param>
        public bool Meets(int team, NetworkGrabbable folder)
        {
            if (folder == null || !folder.IsContainer)
                return false;

            if (folder.VariantTeam != team)
                return false;

            ContainerBase container = folder.GetComponent<ContainerBase>();
            if (container == null)
                return false;

            RequestBoard board = RequestBoard.Instance;
            DocumentStore store = DocumentStore.Instance;

            if (board == null || store == null)
                return false;

            if (!board.TryGetWanted(_requestId.Value, _wantedBuffer))
                return false;

            _matched.Clear();

            for (int i = 0; i < _wantedBuffer.Count; i++)
            {
                if (!TakeMatch(container, store, team, _wantedBuffer[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Server: hands a folder over, if it is what was asked for.
        /// </summary>
        /// <remarks>
        /// Everything that has to happen together happens together: the request comes off the
        /// board, the points are paid, the folder is destroyed and the customer goes idle. Split
        /// across two calls there would be a real window in which the folder is gone and the score
        /// has not moved, and no way for anybody looking at either one to tell which of the two
        /// had happened.
        ///
        /// Taking a folder rather than a list of document ids, for the same reason: a caller
        /// holding the ids could hand over a delivery whose folder had already been picked back up.
        /// </remarks>
        /// <returns>True when the delivery was taken.</returns>
        [Server]
        public bool ServerDeliver(int team, NetworkGrabbable folder)
        {
            if (PhaseOf(team) != CustomerPhase.Working)
                return false;

            if (!Meets(team, folder))
                return false;

            RequestBoard board = RequestBoard.Instance;
            if (board != null)
                board.ServerRemove(_requestId.Value);

            ScoreBoard scores = ScoreBoard.Instance;
            if (scores != null)
                scores.ServerAward(team, _rewardPoints);

            /* Destroy rather than pool: the folder carries per-life state — its team colour, the
             * documents filed in it — that a recycled instance would bring back with it. Same as
             * every other container in the project that consumes what it was given. */
            folder.NetworkObject.Despawn(DespawnType.Destroy);

            GoIdle();
            return true;
        }

        /// <summary>
        /// Server: a player pressed E on this customer.
        /// </summary>
        /// <remarks>
        /// All this does is take the job on the asking player's behalf. The team comes from the
        /// server's own copy of the player and never from anything the client sent — a client that
        /// could name its own team could take a job as the other side and hand it to itself.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)
        {
            if (player == null)
                return;

            ServerAccept(player.Team);
        }

        /// <summary>
        /// Server: runs the waiting clock and every team's patience clock.
        /// </summary>
        /// <remarks>
        /// The waiting clock is the one worth reading twice. It is **not** "the clock that starts
        /// when the customer appears" — it runs whenever nobody is working on this customer and not
        /// every team is out, and it **restarts** each time that becomes true again.
        ///
        /// The case that needs it: one team takes the job, runs out of patience, and the other team
        /// never took it. Without a restart the second team has no clock at all and can take as
        /// long as it likes while the customer holds a place in the scene. With one, letting the
        /// first team fail buys the second team a fresh twenty seconds rather than an unlimited
        /// afternoon.
        /// </remarks>
        private void AdvanceClocks(float deltaTime)
        {
            bool anyWorking = false;
            bool allFailed = true;

            for (int team = 0; team < _phases.Count; team++)
            {
                CustomerPhase phase = (CustomerPhase)_phases[team];

                if (phase == CustomerPhase.Working)
                {
                    anyWorking = true;
                    allFailed = false;

                    _exactRemaining[team] = Mathf.Max(_exactRemaining[team] - deltaTime, 0f);

                    if (_exactRemaining[team] <= 0f)
                        Fail(team);

                    continue;
                }

                if (phase != CustomerPhase.Failed)
                    allFailed = false;
            }

            bool waitingShouldRun = !anyWorking && !allFailed;

            if (waitingShouldRun && !_waitingRunning)
            {
                StartWaiting();
            }
            else if (!waitingShouldRun)
            {
                _waitingRunning = false;
            }

            if (!waitingShouldRun)
            {
                /* Everybody is out. There is nobody left to walk out on, so no penalty — the teams
                 * that ran out were each charged when it happened. */
                if (allFailed)
                    GoIdle();

                return;
            }

            _exactWaiting = Mathf.Max(_exactWaiting - deltaTime, 0f);

            if (_exactWaiting > 0f)
                return;

            /* The customer leaves, and everyone still in it pays for having kept them waiting. */
            for (int team = 0; team < _phases.Count; team++)
            {
                if ((CustomerPhase)_phases[team] != CustomerPhase.Failed)
                    Fail(team);
            }

            GoIdle();
        }

        /// <summary>
        /// Server: takes what is lying in the delivery box, if it is a delivery.
        /// </summary>
        /// <remarks>
        /// **The gate first.** <see cref="IntakeVolume.CollectInside"/> walks every loose object in
        /// the world, and four customers doing that every frame is work nobody asked for. Skipping
        /// it while no team is working is most of the saving — and it is also what makes "you
        /// cannot deliver without taking the job" structural rather than a rule somebody has to
        /// remember to write: with nobody working there is nothing to check a folder against.
        /// </remarks>
        private void ScanForDeliveries()
        {
            if (!AnyWorking())
                return;

            IntakeVolume.CollectInside(NetworkManager, transform, _intakeCentre, _intakeHalfExtents, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
            {
                NetworkGrabbable folder = _scanBuffer[i];

                if (folder == null || folder.VariantTeam < 0)
                    continue;

                int team = folder.VariantTeam;

                if (PhaseOf(team) != CustomerPhase.Working)
                    continue;

                /* Stops at the first folder that is taken. A second one in the same box belongs to
                 * the other team and the request is already gone; letting the loop run on would
                 * only ask a question whose answer cannot be yes. */
                if (ServerDeliver(team, folder))
                    break;
            }

            _scanBuffer.Clear();
        }

        /// <summary>
        /// Server: one team is out of time.
        /// </summary>
        private void Fail(int team)
        {
            _phases[team] = (byte)CustomerPhase.Failed;
            _exactRemaining[team] = 0f;
            _remaining[team] = 0f;

            ScoreBoard scores = ScoreBoard.Instance;
            if (scores != null)
                scores.ServerAward(team, -_penaltyPoints);
        }

        /// <summary>
        /// Server: the customer stops waiting on anything.
        /// </summary>
        /// <remarks>
        /// Used both when a delivery lands and when the customer gives up, because they end in the
        /// same place — the request comes off the board and the customer is free for the spawner to
        /// seat again. It also takes the request off the board itself rather than trusting a caller
        /// to have done it, so a customer can never be left standing in front of a request that is
        /// still being asked for by a label somewhere.
        /// </remarks>
        private void GoIdle()
        {
            RequestBoard board = RequestBoard.Instance;
            if (board != null && _requestId.Value >= 0)
                board.ServerRemove(_requestId.Value);

            _requestId.Value = -1;

            _waitingRunning = false;
            _exactWaiting = 0f;
            _waiting.Value = 0f;
            _writeTimer = 0f;

            for (int team = 0; team < _phases.Count; team++)
            {
                _phases[team] = (byte)CustomerPhase.Idle;
                _exactRemaining[team] = 0f;
                _remaining[team] = 0f;
            }
        }

        /// <summary>
        /// Server: starts the waiting clock over.
        /// </summary>
        private void StartWaiting()
        {
            _exactWaiting = _waitingSeconds;
            _waitingRunning = true;

            /* Published at once, so a restart is visible as a clock jumping back rather than as one
             * that quietly stopped counting down. */
            _waiting.Value = _exactWaiting;
        }

        /// <summary>
        /// Server: makes sure there is one phase and one clock per team.
        /// </summary>
        /// <remarks>
        /// Done lazily rather than in <c>OnStartServer</c> because it depends on
        /// <see cref="ScoreBoard"/>, and two scene objects have no guaranteed order between them.
        /// By the time a customer is given a request the board has long since spawned, so this
        /// normally runs once and then costs a comparison a frame.
        /// </remarks>
        private void EnsureTeamSlots()
        {
            int teams = TeamCount;
            if (teams <= 0 || _phases.Count == teams)
                return;

            _phases.Clear();
            _remaining.Clear();
            _exactRemaining.Clear();

            for (int team = 0; team < teams; team++)
            {
                _phases.Add((byte)CustomerPhase.Idle);
                _remaining.Add(0f);
                _exactRemaining.Add(0f);
            }
        }

        /// <summary>
        /// Server: publishes the clocks.
        /// </summary>
        private void PublishClocks()
        {
            _waiting.Value = _exactWaiting;

            for (int team = 0; team < _remaining.Count && team < _exactRemaining.Count; team++)
                _remaining[team] = _exactRemaining[team];
        }

        /// <summary>
        /// Finds an unused entry in the folder that satisfies one row of the request.
        /// </summary>
        /// <remarks>
        /// The team is checked on the **document** as well as on the folder. A folder that only
        /// accepts its own team's documents should make that impossible, but this is the check the
        /// points are paid on, and a rule enforced one layer away is a rule that is one bug away
        /// from not being enforced at all.
        /// </remarks>
        /// <returns>True when a match was found, which is then marked as used.</returns>
        private bool TakeMatch(ContainerBase container, DocumentStore store, int team, DocumentRequest row)
        {
            for (int i = 0; i < container.Count; i++)
            {
                if (_matched.Contains(i))
                    continue;

                if (!container.TryGetEntry(i, out ContainerEntry entry))
                    continue;
                if ((ContainerEntryKind)entry.Kind != ContainerEntryKind.Data)
                    continue;

                if (!store.TryGet(entry.DataId, out DocumentRecord record))
                    continue;

                if (record.Team != team)
                    continue;
                if (record.SpecIndex != row.SpecIndex || record.Number != row.Number)
                    continue;

                _matched.Add(i);
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when at least one team has taken this customer's job.
        /// </summary>
        private bool AnyWorking()
        {
            for (int team = 0; team < _phases.Count; team++)
            {
                if ((CustomerPhase)_phases[team] == CustomerPhase.Working)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Walks the customer between its spot and the place it waits.
        /// </summary>
        /// <remarks>
        /// On every peer, from replicated state, because a scene object has no NetworkTransform —
        /// see <see cref="_idleOffset"/>. The two ends are the same on every peer and the speed is
        /// the same, so the only thing that can differ is which frame a given peer is on, which is
        /// not something anyone can see.
        /// </remarks>
        private void UpdatePresence()
        {
            if (_idleOffset == Vector3.zero)
                return;

            Vector3 target = HasRequest ? _homePosition : _homePosition + _idleOffset;

            transform.position = Vector3.MoveTowards(transform.position, target, _moveSpeed * Time.deltaTime);
        }

        private void Update() => UpdatePresence();

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            /* The box a folder has to land in. Anything else about this customer is either
             * replicated data or an animation, and neither is worth drawing. */
            Gizmos.color = new Color(0.9f, 0.6f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(_intakeCentre, _intakeHalfExtents * 2f);

            Gizmos.matrix = previous;

            if (_idleOffset == Vector3.zero)
                return;

            /* Drawn in world space, because the offset is relative to where the object was placed
             * and that is not visible from inside its own local space. */
            Vector3 home = Application.isPlaying ? _homePosition : transform.position;
            Gizmos.color = new Color(0.9f, 0.6f, 0.2f, 0.5f);
            Gizmos.DrawLine(home, home + _idleOffset);
            Gizmos.DrawWireSphere(home + _idleOffset, 0.2f);
        }
    }
}
