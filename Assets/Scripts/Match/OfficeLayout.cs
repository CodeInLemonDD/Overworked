using System.Collections.Generic;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Npc;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Sets the office up at the start of a round, and keeps everybody still while it happens.
    /// </summary>
    /// <remarks>
    /// Put on the same object as <see cref="ScoreBoard"/>, <see cref="MatchStarter"/> and
    /// <see cref="MatchFlow"/>, for the reason those three give: it is a NetworkBehaviour that needs
    /// a NetworkObject, and that object is already about the round rather than about any one machine.
    ///
    /// **The random layout is a seed, not a set of positions.** The server rolls one number and
    /// every peer computes the same office from it. That is not an optimisation — it is the only
    /// thing that works here. Stations are scene objects with no NetworkTransform, so a position the
    /// server changed is a position no client would ever hear about; the office would be laid out
    /// one way on the host and another way on every other machine, and the symptom would be a
    /// printer that accepts paper for one player and not another. Sending a seed sidesteps all of
    /// it, and as a bonus the whole layout is reproducible from one integer in a log line.
    ///
    /// **Two kinds of anchor, and the difference is what happens when nothing wants the spot.**
    ///
    /// | | |
    /// |---|---|
    /// | <see cref="StationAnchor"/> | always a station. Never a desk |
    /// | <see cref="TableAnchor"/> | a desk, unless a station that had nowhere else to go takes it |
    ///
    /// That is what keeps the office furnished at every size. A round with few machines would
    /// otherwise be played in a room full of holes; a round with many would be a room with no
    /// furniture at all. Filling the leftovers with desks makes the room look like a room whatever
    /// is standing in it — and because a desk is somewhere things are safe from the cleaner, moving
    /// one is a change to the round and not only to the view. See <see cref="TableAnchor"/>.
    ///
    /// **An arrangement is a prefab, and the seed picks one.** <see cref="_arrangements"/> holds one
    /// prefab per office; the chosen one is instantiated on every peer at the moment the round is
    /// handed over, and taken down again when the round is cleared. As scene objects, several
    /// arrangements would all be standing in the same room at the same time — overlapping, with no
    /// way to look at one without the others in the way — so a second arrangement would have to be
    /// authored blind. As prefabs each one opens on its own.
    ///
    /// **One entry in <see cref="_arrangements"/> is the state to build in.** The machinery is what
    /// is worth testing first, and a single arrangement takes the variety out of the picture while
    /// that happens: the seed still decides which machine stands where, but not what the room looks
    /// like. A second arrangement is then a second prefab and nothing else.
    ///
    /// **Every station anchor is filled, and the rest of the stations spill.** The order is not a
    /// preference, it is the rule: <see cref="StationAnchor"/> is the level designer saying "there is
    /// a machine here", so a station anchor standing empty would be a promise the layout broke. It
    /// also means a part of the office with two station anchors always has two machines in it,
    /// without anybody writing a quota — every anchor is filled, so every anchor's part of the room
    /// is populated.
    ///
    /// **Everything is timed off one replicated edge.** The seed arriving stages the pieces above
    /// their anchors, and <see cref="_deploying"/> going true starts a clock that every peer runs
    /// for itself. Nothing about the descent goes over the wire: each peer knows where each piece
    /// started (it staged it) and where it is going (the seed said so), so the animation is a local
    /// lerp on both machines. The cost of that is a peer seeing the desks land a round-trip later
    /// than the host, which for a two second fall is not a thing anybody can see.
    ///
    /// **It holds the round up.** The round does not begin until this is finished — see
    /// <see cref="MatchStarter"/>, which hands off to this and waits. That is what the frozen
    /// opening is for: an office cannot be assembled around people who are already running through
    /// it, and a desk that lands on somebody is a bug with no good answer.
    /// </remarks>
    [DisallowMultipleComponent]
    public class OfficeLayout : NetworkBehaviour
    {
        /// <summary>
        /// Something the layout moves: a station, or a desk.
        /// </summary>
        /// <remarks>
        /// **One type for both, because everything after this point treats them identically.** A
        /// desk and a printer differ in which list they came from and in nothing else — they are
        /// both objects that were somewhere, are going somewhere else, and have to be put back. Two
        /// parallel sets of code for that would be two places for the descent to be wrong.
        ///
        /// A class rather than a struct in a list, so that iterating does not copy the collider
        /// array on every frame of the descent.
        /// </remarks>
        private sealed class Piece
        {
            public Transform Transform;

            /// <summary>
            /// The station this is, or null for a desk.
            /// </summary>
            public StationBase Station;

            public Vector3 AuthoredPosition;
            public Quaternion AuthoredRotation;
        }

        /// <summary>
        /// One piece, and everywhere it is going to be.
        /// </summary>
        private sealed class Placement
        {
            public Piece Piece;
            public Vector3 Resting;
            public Quaternion Rotation;
            public Vector3 Staged;
        }

        /// <summary>
        /// How long the whole deployment takes, in seconds.
        /// </summary>
        /// <remarks>
        /// The round begins when this runs out. It has to cover the black of the transition, the
        /// descent, and enough after it that players have looked at the office before they have to
        /// move in it — which is the only thing the frozen opening is really buying.
        /// </remarks>
        [Tooltip("How long the deployment takes. The round begins when this runs out.")]
        [Min(0f)]
        [SerializeField]
        private float _deploySeconds = 5f;

        /// <summary>
        /// How long after the handoff the descent begins, in seconds.
        /// </summary>
        /// <remarks>
        /// **This is the black part of the transition, and it has one owner on purpose.** The
        /// screen is dark for this long while the players are moved and the office is staged, and
        /// <see cref="UI.ScreenFade"/> reads this value rather than keeping a number of its own —
        /// two numbers that have to match is a transition that goes wrong the first time somebody
        /// tunes one and not the other.
        /// </remarks>
        [Tooltip("How long the screen stays black before the descent starts, and before the players are shown the new room.")]
        [Min(0f)]
        [SerializeField]
        private float _flightDelaySeconds = 1f;

        /// <summary>
        /// How long the descent takes, in seconds.
        /// </summary>
        [Tooltip("How long a piece takes to come down onto its anchor.")]
        [Min(0.01f)]
        [SerializeField]
        private float _flightSeconds = 1.6f;

        /// <summary>
        /// How high above its anchor a piece waits to come down.
        /// </summary>
        /// <remarks>
        /// The staging position is derived from the anchor rather than authored, which is what makes
        /// the descent cost the level designer nothing: an anchor is a place a piece may stand, and
        /// "above that place" needs no second object to say so.
        ///
        /// **It has to clear the room.** A piece waiting to come down is visible to anyone looking
        /// up, and whether that is charming or broken depends entirely on whether there is a ceiling
        /// in the way. Both read fine; a piece half inside the ceiling does not.
        /// </remarks>
        [Tooltip("How high above its anchor a piece waits to be lowered in.")]
        [Min(0f)]
        [SerializeField]
        private float _stagingHeight = 4.5f;

        /// <summary>
        /// How far a piece has to land from a player spawn, in metres.
        /// </summary>
        /// <remarks>
        /// **This is a layout rule with a machine checking it, and it exists because the failure is
        /// spectacular.** A player is put on their side's spawn point at the handoff and held there
        /// while the office comes down. If an anchor is over that spot, the desk lands on them: the
        /// collider arrives with it, the CharacterController resolves the penetration by shoving
        /// them out, and it happens on the frame the round is supposed to start. They can end up
        /// inside a wall.
        ///
        /// A metre and a half clears a desk and its chair. Nothing enforces it — the check below
        /// only complains — because the fix is always to move one of two objects a person can see.
        /// </remarks>
        [Tooltip("How far a piece must land from a player spawn, in metres. The layout warns when an anchor is closer.")]
        [Min(0f)]
        [SerializeField]
        private float _spawnClearance = 1.5f;

        /// <summary>
        /// One prefab per arrangement of the office. The seed picks one.
        /// </summary>
        /// <remarks>
        /// **An arrangement is a prefab rather than a pile of scene objects.** Every arrangement in
        /// the scene would be standing in the same room at the same time — overlapping, with no way
        /// to look at one without the others in the way — so authoring a second one would mean
        /// authoring it blind. As prefabs they are separate assets, each one openable on its own,
        /// and a new arrangement is a new prefab rather than a new pile of objects to keep aligned
        /// with the first.
        ///
        /// **The order of this array is what the seed indexes into**, so it is a contract in the
        /// same way a catalogue index is: reordering it changes which arrangement every existing
        /// seed means. Nothing will fail to compile. Append rather than insert.
        ///
        /// **With one entry the seed always picks it**, which is the state to build in — it makes the
        /// layout's machinery testable before its variety is interesting. See the class remarks.
        /// </remarks>
        /// <summary>
        /// The candidate layouts a region is rolled from. Each entry is one arrangement of one region.
        /// </summary>
        /// <remarks>
        /// **The list is the pool, and every entry is a whole candidate.** AnchorGroup1, AnchorGroup2
        /// and so on are alternative arrangements of the same region — not children of one asset —
        /// and every region rolls for which one it gets. See <see cref="RollRegions"/>.
        ///
        /// **One entry is the state to build in.** Every region then rolls the same layout and only
        /// the quarter turn and the mirror differ, which is enough to prove the machinery before the
        /// variety is interesting. A second candidate is a second prefab in this list.
        ///
        /// **The anchors in a candidate are drawn around its own origin**, because the origin is what
        /// lands on the region's centre. A layout drawn off to one side hangs out of its region and
        /// into the aisle.
        ///
        /// **Append only** — the order is what a seed indexes into, the same contract a catalogue
        /// index has. Nothing will fail to compile if it changes.
        /// </remarks>
        [Tooltip("Candidate layouts, one prefab per arrangement of a single region. Append only; the index is what a seed means.")]
        [SerializeField]
        private GameObject[] _anchorGroups;

        /// <summary>
        /// The centre of the office floor.
        /// </summary>
        /// <remarks>
        /// **The region grid is built around this, not around the office GameObject.** The floor is
        /// usually a plane sitting somewhere under an empty parent, and the parent's origin is the
        /// floor's corner as often as its middle — so the middle is said out loud rather than
        /// guessed at from whatever object happens to hold the level.
        ///
        /// An empty at the floor's centre is all this needs to be.
        /// </remarks>
        [Tooltip("An empty at the centre of the office floor. The region grid is built around it.")]
        [SerializeField]
        private Transform _officeCentre;

        /// <summary>
        /// How big the office floor is, in metres.
        /// </summary>
        /// <remarks>
        /// Together with <see cref="_regionSize"/> this is what leaves the aisles: whatever the
        /// regions do not cover is gap, and the regions are pushed to the edges of the floor. A
        /// 16&times;16 floor with four 6&times;6 regions leaves a four metre cross through the middle
        /// — which is where the spawn points go, because a spawn inside a region is a spawn that
        /// some roll will eventually drop a desk onto.
        /// </remarks>
        [Tooltip("Office floor size in metres. Whatever the regions do not cover becomes aisle.")]
        [SerializeField]
        private Vector2 _officeSize = new(16f, 16f);

        /// <summary>
        /// How big one region is, in metres.
        /// </summary>
        /// <remarks>
        /// **The candidate layouts are authored against this.** A region of six metres takes anchors
        /// spread over roughly &plusmn;2.5 from its centre, and a candidate drawn wider than that
        /// will hang off the region and into the aisle.
        /// </remarks>
        [Tooltip("Size of one region in metres. Candidate layouts are authored against this.")]
        [SerializeField]
        private Vector2 _regionSize = new(6f, 6f);

        /// <summary>
        /// How many regions across the floor.
        /// </summary>
        [Tooltip("How many regions across the floor. Two by two is four regions.")]
        [Min(1)]
        [SerializeField]
        private int _regionsAcross = 2;

        /// <summary>
        /// How many regions down the floor.
        /// </summary>
        [Tooltip("How many regions down the floor. Two by two is four regions.")]
        [Min(1)]
        [SerializeField]
        private int _regionsDown = 2;

        /// <summary>
        /// How much floor to leave between regions, in metres.
        /// </summary>
        /// <remarks>
        /// **The regions are centred as a block and this is the gap between them.** Whatever floor is
        /// left over once the regions and those gaps are accounted for becomes the margin around the
        /// outside — so a 16 m floor with four 6 m regions and a 2 m gap leaves 1 m all the way
        /// round, which is what the numbers were chosen to do.
        ///
        /// **It used to be all aisle and no margin**, because the regions were pushed to the edges of
        /// the floor and the entire remainder landed in the middle as one four metre cross. That is
        /// the right answer only when the spawn points have to stand in the aisle and need 1.5 m of
        /// clear floor either side of them; it is the wrong answer when they do not, and it looks
        /// like a lobby rather than an office.
        ///
        /// **A gap is not a corridor.** It is floor with no anchors on it. Players walk through the
        /// regions themselves, between the machines — the anchors are furniture, not walls.
        /// </remarks>
        [Tooltip("Floor left between regions, in metres. Whatever is left over becomes the margin around the outside.")]
        [Min(0f)]
        [SerializeField]
        private float _aisleGap = 2f;

        /// <summary>
        /// The pure furniture generated on unused table anchors.
        /// </summary>
        /// <remarks>
        /// **One prefab, many local instances.** Tables have no networked state, so every peer makes
        /// the same table at the same anchor from the same seed. Sending fifty transforms would be
        /// wasteful; sending the seed and the player count is the deterministic contract this class
        /// already uses for the anchors.
        /// </remarks>
        [Tooltip("Table prefab generated on table anchors not claimed by stations. Leave empty to report a setup error.")]
        [SerializeField]
        private GameObject _tablePrefab;

        /// <remarks>
        /// **Replicated rather than read off the server, because every peer lays the office out.**
        /// The whole point of the seed is that each machine computes the same office without being
        /// told the positions; the same is true of which stations are in it. A client that decided
        /// the size for itself would furnish a different office and then agree with nobody about it.
        ///
        /// **Declared before the seed**, so that it is written first — but nothing relies on that.
        /// See <see cref="_builtPlayers"/>, which is the belt to that pair of braces.
        /// </remarks>
        private readonly SyncVar<int> _players = new(0);

        /// <summary>
        /// Which office to build. Replicated.
        /// </summary>
        /// <remarks>
        /// Zero means "no layout yet", which is why the server rolls from one rather than from
        /// zero — a seed of zero and an unset seed would otherwise be the same value, and the
        /// difference between them is whether the office has been built at all.
        /// </remarks>
        private readonly SyncVar<int> _seed = new(0);

        /// <summary>
        /// The office is being set up, and nobody may move. Replicated.
        /// </summary>
        private readonly SyncVar<bool> _deploying = new(false);

        /// <summary>
        /// Whole seconds left on the deployment. Replicated.
        /// </summary>
        /// <remarks>
        /// Whole seconds rather than the exact value, for the reason <see cref="MatchStarter"/>
        /// gives about its own countdown: this is a number on screen that changes once a second,
        /// and sending an integer means every peer shows the number the server decided rather than
        /// its own rounding of a value that arrived a fraction of a second ago. The **descent** is
        /// what needs sub-second precision, and it does not read this — it runs off a local clock
        /// started by the replicated edge.
        /// </remarks>
        private readonly SyncVar<int> _remaining = new(0);

        /// <summary>
        /// The exact countdown on the server, between the once-a-second writes.
        /// </summary>
        private float _exactRemaining;

        /// <summary>
        /// Seconds since the handoff, on this peer. Drives the descent.
        /// </summary>
        private float _sinceDeploy;

        /// <summary>
        /// Whether the colliders have been put back yet this deployment.
        /// </summary>
        private bool _landed;

        /// <summary>
        /// The seed the current layout was built from, or zero.
        /// </summary>
        /// <remarks>
        /// **This exists for the peer that was not there when the seed was set.** A SyncVar arriving
        /// as part of a spawn is an initial value and not a change, so <see cref="OnSeedChanged"/>
        /// never runs for somebody who joined after the office was already laid out — they would get
        /// the seed, and an office standing where the level designer authored it. Checking this
        /// against the seed every frame is timing-independent, which the callback is not: it does
        /// not matter what order FishNet delivers the spawn and the values in.
        ///
        /// It also makes the callback an optimisation rather than a requirement, which is the right
        /// way round for something that has to happen on every peer.
        /// </remarks>
        private int _builtSeed;

        /// <summary>
        /// How many players the current layout was built for.
        /// </summary>
        /// <remarks>
        /// **The other input, checked the same way and for the same reason.** The office depends on
        /// how many people are in the round as well as on the seed, and both of those arrive as
        /// replicated values whose order nothing guarantees — a peer that built on the seed before
        /// the count landed would furnish the full office for a small round and keep it. Watching
        /// both and rebuilding when either moves makes the order stop mattering; the rebuild lands
        /// on the same frame and happens while the screen is still black.
        /// </remarks>
        private int _builtPlayers;

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Every station in the office, in an order every peer agrees on.
        /// </summary>
        /// <remarks>
        /// **Sorted by where it was authored, not by the order it was found.** This is the trap the
        /// whole class rests on: <c>FindObjectsByType</c> makes no promise about the order it
        /// returns things in, and it does not have to be the same on two machines. A shuffle that
        /// ran over a differently-ordered list would deal a different office to every peer, which
        /// would look exactly like the desync the seed exists to prevent — and would be far harder
        /// to find, because the seed would be right.
        /// </remarks>
        private readonly List<Piece> _allStations = new();

        /// <summary>
        /// The stations this round is using, in the same order.
        /// </summary>
        /// <remarks>
        /// **Rebuilt every round from <see cref="_allStations"/>** by
        /// <see cref="StationBase.MinimumPlayers"/>, because the answer depends on how many people
        /// are in the round and that is different every time. Everything downstream — the anchors,
        /// the descent, the collision — works off this list, so a station the round is not using is
        /// not merely hidden but absent from the office entirely.
        ///
        /// **A station that is not in it gets switched off**, rather than being left standing where
        /// it was authored: the authored spot is a place in the room, and a printer sitting in it
        /// unlaid-out would be a machine that is not on an anchor and not part of the layout.
        /// </remarks>
        private readonly List<Piece> _stations = new();

        /// <summary>
        /// Tables generated for the current deployment.
        /// </summary>
        private readonly List<GameObject> _generatedTables = new();

        /// <summary>
        /// The stations still to be given a home, as they are consumed.
        /// </summary>
        private readonly List<Piece> _freeStations = new();

        /// <summary>
        /// Where each side's players are put at the handoff.
        /// </summary>
        /// <remarks>
        /// Read from the <see cref="TeamZone"/> objects rather than configured here, because they
        /// are already the thing that decides it — <see cref="MatchStarter"/> moves players to
        /// <c>zone.Spawn</c> and nothing else would know if that changed.
        /// </remarks>
        private readonly List<Transform> _spawns = new();

        /// <summary>
        /// What the current deployment is doing. Empty when nothing has been laid out.
        /// </summary>
        private readonly List<Placement> _placements = new();

        /// <summary>
        /// Set once a wiring complaint has been made, so it is not repeated.
        /// </summary>
        private string _reported;

        /// <summary>
        /// The layout in the scene, or null before it has spawned.
        /// </summary>
        public static OfficeLayout Instance { get; private set; }

        /// <summary>
        /// The office is being set up, and the round has not begun.
        /// </summary>
        /// <remarks>
        /// **True for the whole of the round, not just the descent.** The flag stays up after the
        /// countdown reaches zero — the office stands where it was put and the players stay held
        /// until somebody asks for the next round — so this answers "is the round being held", and
        /// it is the wrong question to ask about whether the office is ready. See
        /// <see cref="IsDeployed"/>, which is that question, and <see cref="MatchStarter"/>, which
        /// asked this one and waited for ever.
        /// </remarks>
        public bool IsDeploying => _deploying.Value;

        /// <summary>
        /// The office is still being assembled, and the players are held still.
        /// </summary>
        /// <remarks>
        /// **Not <see cref="IsDeploying"/>, although it nearly is.** The deploying flag stays up for
        /// the whole of the round, so anything that asks it "is the office still being built" gets
        /// yes for ever — which is a player who is held still until somebody asks for the next
        /// round, and reads as the movement being broken rather than as the freeze never lifting.
        ///
        /// **Read off the replicated whole-second countdown, not the exact one.** This is asked on
        /// every peer — <see cref="Player.PlayerMovementPrediction"/> asks before it builds the input
        /// it sends — and the exact countdown only ever moves on the server. A client reading it
        /// would see zero from the first frame and never be frozen at all.
        /// </remarks>
        public bool IsHoldingPlayers => _deploying.Value && _remaining.Value > 0;

        /// <summary>
        /// The office is standing and the round may begin.
        /// </summary>
        /// <remarks>
        /// **Not the same as not deploying**, and the difference is the whole of a round that never
        /// starts: the deployment runs out and the flag stays up, because the office is still
        /// holding the players still. This is the question a caller waiting for the office actually
        /// has.
        ///
        /// Read off the exact countdown, which is authoritative on the server — the only place this
        /// is asked, because starting a round is a server decision.
        /// </remarks>
        public bool IsDeployed => _deploying.Value && _exactRemaining <= 0f;

        /// <summary>
        /// Whole seconds left on the deployment, or zero when it is not running.
        /// </summary>
        public int RemainingSeconds =>
            IsServerInitialized ? Mathf.CeilToInt(_exactRemaining) : _remaining.Value;

        /// <summary>
        /// How long a whole deployment takes.
        /// </summary>
        public float DeploySeconds => _deploySeconds;

        /// <summary>
        /// How long the screen is dark for, and how long before the descent starts.
        /// </summary>
        public float FlightDelaySeconds => _flightDelaySeconds;

        /// <summary>
        /// How long the descent takes.
        /// </summary>
        public float FlightSeconds => _flightSeconds;

        /// <summary>
        /// Whether players are being held still while the office is set up.
        /// </summary>
        /// <remarks>
        /// A static question with a static answer, because the thing that asks it — the movement
        /// replicate — runs every frame on every peer and does not want to null-check and reach
        /// through an instance to do it. **False when there is no layout in the scene**, which is
        /// what keeps a scene that has not been wired yet playable.
        /// </remarks>
        public static bool MovementIsFrozen => Instance != null && Instance.IsHoldingPlayers;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(OfficeLayout)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; the two would disagree about where the office goes.",
                    this);
            }

            Instance = this;

            /* Subscribed on every peer, not just the server: the flag is set by the server and
             * arrives on clients, and the restore has to happen wherever it lands.
             *
             * **The seed deliberately has no callback.** See Update, which watches the seed and the
             * player count together — one watcher for both means neither of them has to be the one
             * that arrives last, which is not something either of them can promise. */
            _deploying.OnChange += OnDeployingChanged;
        }

        public override void OnStopNetwork()
        {
            _deploying.OnChange -= OnDeployingChanged;

            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* A SyncVar on a scene NetworkObject survives between sessions — the component is the
             * same component — so a session stopped mid-deployment would come back with the office
             * frozen and the round unable to start. The same trap the scoreboard, the flow and the
             * customer's request list each have, and the same fix. */
            ClearDeployment();

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
        /// Server: sets the office up and holds the round until it is standing.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="MatchStarter"/> at the moment the players are moved, which is the
        /// moment the screen starts going dark. **The count goes out before the seed, and the seed
        /// before the flag**, so that a peer working its way through the values has everything it
        /// needs by the time the clock starts. Nothing depends on that order surviving the wire —
        /// see <see cref="_builtPlayers"/> — but the server's own build reads these on its next
        /// frame, and they should already be right by then.
        /// </remarks>
        /// <param name="players">How many people are in this round. Decides how much office is used.</param>
        [Server]
        public bool ServerDeploy(int players)
        {
            if (_deploying.Value)
                return false;

            _players.Value = Mathf.Max(players, 0);

            /* Rolled from one so that zero keeps meaning "nothing has been built" — see the seed's
             * remarks. A fixed-seed mode for reproducing a layout is the obvious next thing here,
             * and it is deliberately not built yet: nothing has asked for it, and a field nobody
             * sets is a field nobody tests. */
            _seed.Value = Random.Range(1, int.MaxValue);

            _exactRemaining = _deploySeconds;
            _remaining.Value = Mathf.CeilToInt(_deploySeconds);
            _sinceDeploy = 0f;
            _deploying.Value = true;

            return true;
        }

        /// <summary>
        /// Server: puts the office back the way it was authored.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="MatchFlow"/> when the round is cleared. **It only clears the flag**,
        /// and that is not a shortcut — the pieces are actually put back by every peer in
        /// <see cref="OnDeployingChanged"/>, because a server that moved them back on its own would
        /// leave every client's office standing on last round's anchors. The two would converge
        /// again at the next deployment, since that stages from the anchors rather than from where a
        /// piece happens to be, so the divergence would be invisible and real at the same time —
        /// which is the worst kind there is.
        ///
        /// **The authored position is what everything else is measured against.** A piece left on
        /// an anchor would be staged from the right place and still end up in the wrong office the
        /// moment anything else about the layout changed, because "where it was" is the one thing
        /// the layout reads that the seed does not determine.
        /// </remarks>
        [Server]
        public void ServerReset() => ClearDeployment();

        /// <summary>
        /// Puts every piece back where it was authored, switched on, with its colliders on.
        /// </summary>
        /// <remarks>
        /// **Runs on every peer, driven by the replicated flag.** Moving a piece is a local write
        /// with nothing behind it — nothing here has a NetworkTransform, which is the whole reason
        /// this class works off a seed — so a restore that only the server performed would be a
        /// restore no client ever saw.
        ///
        /// **Desks that were not used are switched back on.** A desk with no anchor left for it was
        /// turned off rather than left standing somewhere wrong, and turning it back on is what makes
        /// the authored scene the state a round starts from.
        /// </remarks>
        private void RestoreAuthored()
        {
            /* **_allStations rather than _stations.** A station this round switched off has to be
             * switched back on — see RestoreList — and it is the authored scene that every round
             * starts from, not the state the last one left. */
            RestoreList(_allStations);

            /* Scene-authored tables are legacy only; the live furniture is generated from anchors.
             * Destroying these first makes restart idempotent and prevents a second copy next round. */
            DestroyGeneratedTables();
        }

        private static void RestoreList(List<Piece> pieces)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];

                if (piece == null || piece.Transform == null)
                    continue;

                piece.Transform.gameObject.SetActive(true);
                piece.Transform.SetPositionAndRotation(piece.AuthoredPosition, piece.AuthoredRotation);
                SetColliders(piece.Transform, true);
            }
        }

        /// <summary>
        /// Forgets the deployment without moving anything.
        /// </summary>
        private void ClearDeployment()
        {
            _deploying.Value = false;
            _remaining.Value = 0;
            _exactRemaining = 0f;
            _sinceDeploy = 0f;
            _landed = false;
            _placements.Clear();
            DestroyGeneratedTables();

            /* Back to "nothing has been built", so that the next deployment builds even if it
             * somehow drew the same seed — and so that Update's check does not read a stale
             * agreement between this and a seed that is about to be replaced. */
            _builtSeed = 0;
        }

        /// <summary>
        /// Server: one look at the deployment clock.
        /// </summary>
        /// <remarks>
        /// Watched rather than driven from <c>Update</c>, for the reason every other clock in this
        /// project gives: a countdown on a frame callback is a countdown that runs at whatever rate
        /// the machine happens to be managing. **This only counts.** What happens when it reaches
        /// zero belongs to <see cref="MatchStarter"/>, which is watching for exactly that — the
        /// round beginning is that class's decision, not this one's.
        /// </remarks>
        private void ServerUpdate()
        {
            if (!_deploying.Value)
                return;

            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _exactRemaining = Mathf.Max(_exactRemaining - deltaTime, 0f);

            int whole = Mathf.CeilToInt(_exactRemaining);
            if (_remaining.Value != whole)
                _remaining.Value = whole;
        }

        /// <summary>
        /// Every peer: advances the descent.
        /// </summary>
        /// <remarks>
        /// Real time rather than unscaled, because this is an animation and should stop when the
        /// game is paused. The clock it advances is local and started by a replicated edge, so
        /// there is nothing here to keep in step with the server beyond the start.
        /// </remarks>
        private void Update()
        {
            if (!_deploying.Value)
                return;

            /* The office depends on two replicated values, and nothing guarantees the order they
             * arrive in — so it is rebuilt while either of them disagrees with what was last built,
             * rather than in the callback of one of them. See _builtPlayers.
             *
             * Checked before the clock advances, so a peer that has just found out about the office
             * stages it on the same frame rather than descending a piece that is still standing where
             * it was authored. Guarded on zero because "no layout" is a value the seed legitimately
             * has. */
            if (_seed.Value != 0 && (_builtSeed != _seed.Value || _builtPlayers != _players.Value))
                BuildLayout(_seed.Value);

            _sinceDeploy += Time.deltaTime;
            ApplyFlight();
        }

        /// <summary>
        /// Every peer: puts the pieces somewhere along their descent.
        /// </summary>
        /// <remarks>
        /// **Smoothstep rather than a plain lerp.** A constant-speed descent reads as an object
        /// being dragged; easing out at the bottom reads as one being set down, which is what the
        /// office is supposed to look like. Easing *in* at the top would read as a drop, which is
        /// the other thing this could have been and is not.
        /// </remarks>
        private void ApplyFlight()
        {
            if (_placements.Count == 0)
                return;

            float window = Mathf.Max(_flightSeconds, 0.01f);
            float t = Mathf.Clamp01((_sinceDeploy - _flightDelaySeconds) / window);

            /* The screen is still black. Nothing has been shown yet, so there is nothing to move —
             * and skipping the write means the first frame anybody sees is already the start of the
             * descent rather than a frame of pieces sitting still in the air. */
            if (t <= 0f)
                return;

            float eased = Mathf.SmoothStep(0f, 1f, t);

            for (int i = 0; i < _placements.Count; i++)
            {
                Placement placement = _placements[i];
                Transform piece = placement.Piece != null ? placement.Piece.Transform : null;

                if (piece == null)
                    continue;

                Vector3 position = Vector3.Lerp(placement.Staged, placement.Resting, eased);
                piece.SetPositionAndRotation(position, placement.Rotation);
            }

            if (t < 1f || _landed)
                return;

            /* Colliders come back on the frame the last piece lands, and not before. A piece coming
             * down through the room is a moving solid object the size of a desk, and the players are
             * standing in that room. */
            _landed = true;

            for (int i = 0; i < _placements.Count; i++)
            {
                if (_placements[i].Piece != null && _placements[i].Piece.Transform != null)
                    SetColliders(_placements[i].Piece.Transform, true);
            }
        }

        /// <summary>
        /// Starts the local clock that drives the descent, or puts the office back when it ends.
        /// </summary>
        /// <remarks>
        /// **Falling is the round being cleared, and only that.** The flag does not drop when the
        /// deployment runs out — the office stays standing, and stays where it is, until somebody
        /// asks for the next round. So this branch is a reset and nothing else.
        /// </remarks>
        private void OnDeployingChanged(bool previous, bool next, bool asServer)
        {
            if (next)
            {
                /* Restarted here rather than where the server set it, so that the descent is
                 * measured from when *this* peer found out. A client that started its clock when
                 * the server did would begin the descent while the screen was still going dark on
                 * its own machine. */
                _sinceDeploy = 0f;
                _landed = false;
                return;
            }

            /* The round is being put back, so the regions go with it: the office is about to be stood
             * in its authored state, and the anchors of the office that was played have no business
             * still being in the room for that. */
            TearDownRegions();
            RestoreAuthored();
        }

        /// <summary>
        /// Works out what goes where, and puts everything up in the air.
        /// </summary>
        /// <remarks>
        /// **Three passes, in an order that is a rule rather than a preference:**
        ///
        /// 1. **Every station anchor gets a station.** A station anchor is the level designer saying
        ///    "there is a machine here", so leaving one empty would be breaking that promise. It is
        ///    also what makes a region's minimum self-enforcing — see the class remarks.
        /// 2. **Stations with nowhere left go onto table anchors**, taking the desk that would have
        ///    been there.
        /// 3. **Desks fill whatever table anchors are left.** A desk with no anchor left is switched
        ///    off rather than left standing where it was authored, because the authored spot is a
        ///    place in some other arrangement and a desk sitting there would be furniture in a room
        ///    that is not that room.
        ///
        /// **Every pass draws from the same generator in the same order on every machine**, and every
        /// list it draws from was sorted first — see <see cref="_stations"/>.
        /// </remarks>
        /// <summary>
        /// One region of the office, and the anchors standing in it.
        /// </summary>
        /// <remarks>
        /// **The unit the layout is actually built in.** A region rolls its own arrangement, holds
        /// its own anchors, and is given its own share of the machines — so almost everything below
        /// is a loop over regions rather than a loop over anchors. The anchor lists are per region
        /// and not one flat pool because the rule that matters is per region: every region's fixed
        /// anchors are filled before any region gets a second helping.
        /// </remarks>
        private sealed class Region
        {
            public Vector3 Centre;
            public GameObject Instance;

            public readonly List<StationAnchor> StationAnchors = new();
            public readonly List<TableAnchor> TableAnchors = new();
        }

        /// <summary>
        /// Works out what goes where, and puts everything up in the air.
        /// </summary>
        /// <remarks>
        /// **Four steps, and the first one is what makes the rest possible:**
        ///
        /// 1. **Roll the regions.** Each one picks a pool, a candidate from it, a quarter turn and
        ///    whether to be mirrored, and stands its anchors up around its own centre.
        /// 2. **Share the machines out.** Every region's fixed anchors are filled, then what is left
        ///    over is spread as evenly as it divides.
        /// 3. **Fill each region.** Its own stations onto its own anchors, its own desks onto what is
        ///    left.
        /// 4. **Report.** Anything with nowhere to go, and anything that landed on somebody.
        ///
        /// **Every random draw comes off one generator in one fixed order**, and every list it draws
        /// from was sorted or is in a hierarchy order every peer has a copy of — see
        /// <see cref="_allStations"/>. That is the whole of what keeps four machines agreeing.
        /// </remarks>
        private void BuildLayout(int seed)
        {
            /* Marked first, so that every path out of here counts as "this seed has been dealt
             * with" — including the ones that place nothing. Otherwise a scene with no anchors would
             * rebuild the empty layout every frame for the whole deployment. */
            _builtSeed = seed;
            _builtPlayers = _players.Value;

            /* A seed and the player count can arrive in separate frames. If the first value caused
             * an early build, remove its local tables before the second build creates the corrected
             * office, or every correction would leave one duplicate table per anchor behind. */
            DestroyGeneratedTables();

            CapturePieces();
            ChooseStations();

            _placements.Clear();
            _landed = false;

            TearDownRegions();

            System.Random random = new(seed);

            RollRegions(random);

            int stationAnchors = 0;
            int tableAnchors = 0;

            for (int i = 0; i < _regions.Count; i++)
            {
                stationAnchors += _regions[i].StationAnchors.Count;
                tableAnchors += _regions[i].TableAnchors.Count;
            }

            /* Said out loud because it is the one line that makes a layout reproducible. Somebody
             * looking at an office they did not expect can read the seed off it, and the counts say
             * whether the region that ran is the one they were editing. */
            Debug.Log(
                $"{nameof(OfficeLayout)}: {_players.Value} player(s) — {_regions.Count} region(s), " +
                $"{stationAnchors} station anchors, {tableAnchors} table anchors, " +
                $"{_stations.Count} of {_allStations.Count} stations, seed {seed}.");

            if (_regions.Count == 0)
                return;

            if (stationAnchors == 0)
            {
                ReportOnce($"the regions that were rolled have no {nameof(StationAnchor)} objects in them, so no station can be placed. **Each prefab in the {nameof(_anchorGroups)} list is one whole candidate layout for one region**, and it needs a set of anchors drawn around its own origin");
                return;
            }

            if (stationAnchors < _regions.Count * 2)
            {
                ReportOnce($"the regions rolled {stationAnchors} station anchor(s) between {_regions.Count} of them, which is under two per region. **Every region is meant to have at least two** — see whether the candidate prefabs are drawn with fewer, or whether one of them has no anchors at all");
            }

            _freeStations.Clear();
            _freeStations.AddRange(_stations);

            ShareOutStations(random);
            FillRegions(random);

            /* After the fill, because the answer only exists once it has run. Said at all because it
             * is the one number that tells a room drawn too small from a room drawn right: a full
             * round of fifteen machines eats most of the table anchors, and a floor with too few of
             * them reports homeless stations instead. */
            Debug.Log($"{nameof(OfficeLayout)}: {_generatedTables.Count} table(s) generated, seed {seed}.");

            ReportPiecesOnSpawns();
        }

        /// <summary>
        /// Stands a rolled arrangement up in every region.
        /// </summary>
        /// <remarks>
        /// **The grid is worked out from the floor, not written down.** The regions are pushed to
        /// the edges of <see cref="_officeSize"/> and whatever they do not cover is the gap between
        /// them — so a bigger floor is a bigger aisle, and a smaller region is a wider one, without
        /// anybody editing a coordinate.
        ///
        /// **Anchors are posed one at a time rather than by turning the instance.** Mirroring could
        /// have been a negative scale on the parent, which is shorter and wrong: a negative scale is
        /// not a rotation, so every anchor under it would report a facing that is not the one it
        /// draws, and a station placed on one would face a direction nobody chose. Posing each
        /// anchor means the arithmetic is done once, here, where it can be read.
        ///
        /// The order is row by row and left to right, which is fixed — the generator is spent in
        /// that order on every peer.
        /// </remarks>
        private void RollRegions(System.Random random)
        {
            _regions.Clear();

            if (_officeCentre == null)
            {
                ReportOnce($"{nameof(_officeCentre)} is not set, so there is nowhere to build the regions");
                return;
            }

            if (_anchorGroups == null || _anchorGroups.Length == 0)
            {
                ReportOnce($"nothing in the {nameof(_anchorGroups)} list, so there are no arrangements to roll");
                return;
            }

            /* **The block of regions is centred, not pushed to the walls.** The gaps between regions
             * are what was asked for and whatever floor is left over is split evenly around the
             * outside — so the same two numbers describe a tight office and a roomy one, and there is
             * no separate margin to keep in step with them. */
            float stepX = _regionSize.x + _aisleGap;
            float stepZ = _regionSize.y + _aisleGap;

            float marginX = MarginFor(_officeSize.x, _regionSize.x, _regionsAcross, _aisleGap);
            float marginZ = MarginFor(_officeSize.y, _regionSize.y, _regionsDown, _aisleGap);

            if (marginX < 0f)
            {
                ReportOnce($"{_regionsAcross} regions {_regionSize.x} m wide with {_aisleGap} m between them do not fit across a {_officeSize.x} m floor, so they overlap. A smaller {nameof(_regionSize)}, a smaller {nameof(_aisleGap)}, or a bigger {nameof(_officeSize)}");
            }

            if (marginZ < 0f)
            {
                ReportOnce($"{_regionsDown} regions {_regionSize.y} m deep with {_aisleGap} m between them do not fit down a {_officeSize.y} m floor, so they overlap. A smaller {nameof(_regionSize)}, a smaller {nameof(_aisleGap)}, or a bigger {nameof(_officeSize)}");
            }

            float left = _officeCentre.position.x - _officeSize.x * 0.5f + marginX + _regionSize.x * 0.5f;
            float back = _officeCentre.position.z - _officeSize.y * 0.5f + marginZ + _regionSize.y * 0.5f;
            float level = _officeCentre.position.y;

            for (int down = 0; down < _regionsDown; down++)
            {
                for (int across = 0; across < _regionsAcross; across++)
                {
                    Region region = new()
                    {
                        Centre = new Vector3(left + across * stepX, level, back + down * stepZ),
                    };

                    GameObject pool = _anchorGroups[random.Next(_anchorGroups.Length)];

                    /* Both drawn whether or not they are used, so that a null slot in the pool list
                     * does not shift every later draw and deal two machines different offices. */
                    int turn = random.Next(4);
                    bool mirrored = random.Next(2) == 1;

                    if (!StandRegion(region, pool, turn, mirrored))
                        continue;

                    _regions.Add(region);
                }
            }
        }

        /// <summary>
        /// How much floor is left around the outside of the block of regions.
        /// </summary>
        /// <remarks>
        /// **Negative when they do not fit, and left negative.** Clamping it to zero would turn a
        /// room drawn too small into a room that quietly overlaps itself, which is a problem you can
        /// only find by looking; a negative margin is reported below and the regions still land
        /// where the numbers say, so the overlap is at least where it was asked for.
        /// </remarks>
        private static float MarginFor(float floor, float region, int count, float gap)
        {
            float spare = floor - count * region - (count - 1) * gap;
            return spare * 0.5f;
        }

        /// <summary>
        /// Puts one rolled arrangement up in one region.
        /// </summary>
        /// <returns>False when there was nothing to stand up.</returns>
        /// <remarks>
        /// **One prefab is one candidate layout.** The pool is a list of them — AnchorGroup1,
        /// AnchorGroup2, and so on — and a region rolls for which one it gets. Nothing is nested:
        /// adding a candidate is a new prefab in the scene's list rather than a new child in an
        /// existing one, so each is an ordinary prefab that can be opened, edited and duplicated
        /// like any other.
        ///
        /// **Its anchors should sit around the prefab's own origin.** The origin is what lands on
        /// the region's centre, so a layout drawn a few metres off to one side would hang out of its
        /// region and into the aisle.
        /// </remarks>
        private bool StandRegion(Region region, GameObject pool, int turn, bool mirrored)
        {
            if (pool == null)
            {
                ReportOnce($"one of the slots in the {nameof(_anchorGroups)} list is empty");
                return false;
            }

            GameObject instance = Instantiate(pool);
            instance.name = $"{pool.name} ({region.Centre.x:0.#}, {region.Centre.z:0.#})";

            /* Squared up before anything is measured, so that every offset read off it below is the
             * arrangement's own coordinate and nothing else. */
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            PoseAnchors(region, instance.transform, turn, mirrored);

            HideVisuals(instance);

            region.Instance = instance;
            return true;
        }

        /// <summary>
        /// Reused by <see cref="StandRegion"/> so choosing a candidate allocates nothing.
        /// </summary>
        private readonly List<StationAnchor> _posedStations = new();

        private readonly List<TableAnchor> _posedTables = new();

        /// <summary>
        /// Moves every anchor of a candidate to where the roll puts it.
        /// </summary>
        private void PoseAnchors(Region region, Transform candidate, int turn, bool mirrored)
        {
            region.StationAnchors.Clear();
            region.TableAnchors.Clear();
            _posedStations.Clear();
            _posedTables.Clear();

            candidate.GetComponentsInChildren(includeInactive: true, _posedStations);
            candidate.GetComponentsInChildren(includeInactive: true, _posedTables);

            for (int i = 0; i < _posedStations.Count; i++)
            {
                Transform anchor = _posedStations[i].transform;
                Place(anchor, candidate, region.Centre, turn, mirrored);
                region.StationAnchors.Add(_posedStations[i]);
            }

            for (int i = 0; i < _posedTables.Count; i++)
            {
                Transform anchor = _posedTables[i].transform;
                Place(anchor, candidate, region.Centre, turn, mirrored);
                region.TableAnchors.Add(_posedTables[i]);
            }

            /* Sorted after posing rather than before, so that which anchor is "first" is a position
             * and not a hierarchy order — the same reason every other list in this class is sorted.
             * It is the sort that makes the assignment after it reproducible. */
            region.StationAnchors.Sort(ByPositionOfStationAnchor);
            region.TableAnchors.Sort(ByPositionOfTableAnchor);
        }

        /// <summary>
        /// Puts one anchor at its place in a region.
        /// </summary>
        /// <remarks>
        /// **The turn and the mirror move the anchor and do not turn it.** Where a piece of furniture
        /// stands is part of the arrangement and varies; which way it faces is part of how the game
        /// is read, and the camera never moves — see <see cref="Camera.PlayerCameraFollow"/>, which
        /// writes no rotation at all and leaves the angle authored in the scene. So a desk turned a
        /// quarter turn with its region would be a desk the player reads from behind, and a mirrored
        /// one would be the same desk facing the other way for no reason anybody could see. Two
        /// arrangements that differ only by a turn would look like two arrangements drawn by somebody
        /// who could not decide which way round the office goes.
        ///
        /// **The offset is mirrored before it is turned**, because that is what "a mirrored copy of
        /// this layout, rotated a quarter turn" means. The facing takes no part in either.
        /// </remarks>
        private static void Place(Transform anchor, Transform candidate, Vector3 centre, int turn, bool mirrored)
        {
            Vector3 offset = candidate.InverseTransformPoint(anchor.position);

            if (mirrored)
                offset.x = -offset.x;

            float radians = turn * 90f * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);

            Vector3 turned = new(
                offset.x * cos + offset.z * sin,
                offset.y,
                -offset.x * sin + offset.z * cos);

            anchor.SetPositionAndRotation(centre + turned, anchor.rotation);
        }

        /// <summary>
        /// Works out how many machines each region gets.
        /// </summary>
        /// <remarks>
        /// **Every region's fixed anchors are filled before anybody gets a second helping.** That is
        /// the rule, and doing it in this order is what makes it true without a quota: the fixed
        /// anchors are the floor, and only what is left above that floor is shared out.
        ///
        /// **The leftovers divide unevenly, and the remainder is rotated by the seed.** Seven spare
        /// machines across four regions is two, two, two and one — and which region gets the one has
        /// to be decided by something. Giving it to the first region every round is a region that is
        /// always the fullest; drawing the starting point from the seed makes the unevenness move
        /// around while leaving the four of them within one machine of each other for ever.
        /// </remarks>
        private void ShareOutStations(System.Random random)
        {
            int fixedAnchors = 0;

            for (int i = 0; i < _regions.Count; i++)
                fixedAnchors += _regions[i].StationAnchors.Count;

            _stationsPerRegion.Clear();

            for (int i = 0; i < _regions.Count; i++)
                _stationsPerRegion.Add(_regions[i].StationAnchors.Count);

            int spare = _stations.Count - fixedAnchors;

            if (spare < 0)
            {
                ReportOnce($"the office has {_stations.Count} station(s) and the regions have {fixedAnchors} fixed {nameof(StationAnchor)} object(s) between them, so {fixedAnchors - _stations.Count} anchor(s) will stand empty. Add stations, or give the candidates fewer fixed anchors");
                return;
            }

            if (spare == 0)
                return;

            int each = spare / _regions.Count;
            int remainder = spare % _regions.Count;
            int start = random.Next(_regions.Count);

            for (int i = 0; i < _regions.Count; i++)
                _stationsPerRegion[i] += each;

            for (int i = 0; i < remainder; i++)
                _stationsPerRegion[(start + i) % _regions.Count] += 1;
        }

        /// <summary>
        /// How many machines each region is to hold. Parallel to <see cref="_regions"/>.
        /// </summary>
        private readonly List<int> _stationsPerRegion = new();

        /// <summary>
        /// Names the regions.
        /// </summary>
        private readonly List<Region> _regions = new();

        /// <summary>
        /// Fills every region: its own stations onto its own anchors, then its own desks.
        /// </summary>
        /// <remarks>
        /// **Each region is finished before the next is started**, so that a machine that cannot be
        /// placed is a machine in a region that has run out of room rather than one that lost a race
        /// against another region for the last anchor.
        /// </remarks>
        private void FillRegions(System.Random random)
        {
            int homelessStations = 0;
            int homelessTables = 0;

            for (int i = 0; i < _regions.Count; i++)
            {
                Region region = _regions[i];
                int allowance = i < _stationsPerRegion.Count ? _stationsPerRegion[i] : region.StationAnchors.Count;

                /* The fixed anchors. One station each, and every one of them gets something as long
                 * as there are stations left at all — a region's fixed anchor standing empty is a
                 * machine the level design promised. */
                for (int a = 0; a < region.StationAnchors.Count; a++)
                {
                    if (allowance <= 0)
                        break;

                    int chosen = ChooseStation(region.StationAnchors[a], random);

                    if (chosen < 0)
                        break;

                    Piece station = _freeStations[chosen];
                    _freeStations.RemoveAt(chosen);

                    Stage(station, region.StationAnchors[a].Position, region.StationAnchors[a].Rotation);
                    allowance--;
                }

                /* What is left of the allowance goes onto desks' places — the machines take the
                 * table's spot, and the table is one of the ones that does not get placed. */
                List<TableAnchor> spare = new(region.TableAnchors);

                while (allowance > 0 && spare.Count > 0 && _freeStations.Count > 0)
                {
                    int pick = random.Next(spare.Count);
                    TableAnchor anchor = spare[pick];
                    spare.RemoveAt(pick);

                    int chosen = random.Next(_freeStations.Count);
                    Piece station = _freeStations[chosen];
                    _freeStations.RemoveAt(chosen);

                    Stage(station, anchor.Position, anchor.Rotation);
                    allowance--;
                }

                if (allowance > 0)
                    homelessStations += allowance;

                /* Desks onto whatever table anchors are left. They are generated locally from one
                 * shared prefab, not read from scene instances. */
                while (spare.Count > 0)
                {
                    int pick = random.Next(spare.Count);
                    TableAnchor anchor = spare[pick];
                    spare.RemoveAt(pick);

                    GameObject table = CreateTable(anchor.Position, anchor.Rotation);
                    if (table == null)
                    {
                        homelessTables += spare.Count + 1;
                        break;
                    }

                    Stage(NewPiece(table.transform, null), anchor.Position, anchor.Rotation);
                }

                homelessTables += spare.Count;
            }

            if (homelessStations > 0)
            {
                ReportOnce($"{homelessStations} station(s) had nowhere to go — the regions ran out of {nameof(TableAnchor)} objects for them to take the place of. **Every region needs enough table anchors to absorb its share**: add more, or add fewer stations");
            }

            if (homelessTables > 0)
            {
                ReportOnce($"{homelessTables} table anchor(s) had no generated table, because {nameof(_tablePrefab)} is missing or a table could not be created");
            }
        }

        private GameObject CreateTable(Vector3 position, Quaternion rotation)
        {
            if (_tablePrefab == null)
                return null;

            GameObject table = Instantiate(_tablePrefab, position, rotation);
            table.name = $"{_tablePrefab.name} (generated table)";
            _generatedTables.Add(table);
            return table;
        }

        private void DestroyGeneratedTables()
        {
            for (int i = 0; i < _generatedTables.Count; i++)
            {
                GameObject table = _generatedTables[i];
                if (table == null)
                    continue;

                table.SetActive(false);
                Destroy(table);
            }

            _generatedTables.Clear();
        }

        /// <summary>
        /// Takes every region down.
        /// </summary>
        /// <remarks>
        /// **Switched off before being destroyed, and that is not tidiness.** <c>Destroy</c> is
        /// deferred to the end of the frame and the code that reads the anchors runs before that, so
        /// a region still active for one more frame would be read alongside the ones that replaced
        /// it — and the round would be laid out against both at once.
        /// </remarks>
        private void TearDownRegions()
        {
            for (int i = 0; i < _regions.Count; i++)
            {
                GameObject instance = _regions[i].Instance;

                if (instance == null)
                    continue;

                instance.SetActive(false);
                Destroy(instance);
            }

            _regions.Clear();
            _stationsPerRegion.Clear();
        }

        /// <summary>
        /// Picks a station for an anchor, or -1 when nothing fits.
        /// </summary>
        /// <remarks>
        /// **Reservoir sampling, so the choice is uniform in one pass.** Taking the first match
        /// would send every printer to whichever printer anchor happened to sort first, which is a
        /// layout that looks random for one round and then does not change. Drawing a random index
        /// needs the candidate count first, which means a second pass over the same list — and the
        /// single-pass form is both shorter and the one that consumes the generator in a way that
        /// depends only on the inputs, which is what keeps two machines agreeing.
        /// </remarks>
        private int ChooseStation(StationAnchor anchor, System.Random random)
        {
            int chosen = -1;
            int matches = 0;

            for (int i = 0; i < _freeStations.Count; i++)
            {
                if (!anchor.Accepts(_freeStations[i].Station))
                    continue;

                matches++;

                if (random.Next(matches) == 0)
                    chosen = i;
            }

            return chosen;
        }

        /// <summary>
        /// Puts a piece above a place, waiting to come down onto it.
        /// </summary>
        private void Stage(Piece piece, Vector3 resting, Quaternion rotation)
        {
            Placement placement = new()
            {
                Piece = piece,
                Resting = resting,
                Rotation = rotation,
                Staged = resting + Vector3.up * _stagingHeight,
            };

            _placements.Add(placement);

            piece.Transform.SetPositionAndRotation(placement.Staged, rotation);

            /* Off for the whole descent. A piece is a solid object the size of a desk, and this one
             * is about to travel through a room with people in it. */
            SetColliders(piece.Transform, false);
        }

        /// <summary>
        /// Makes a rolled arrangement invisible.
        /// </summary>
        /// <remarks>
        /// **A pool is a positioning aid, and nothing in it is meant to be seen.** The cubes the
        /// level designer puts each anchor on are there to be dragged around in the editor, and at
        /// runtime they are a field of boxes standing in the office.
        ///
        /// **The components are switched off rather than the objects deleted**, for two reasons. A
        /// cube carries a <c>BoxCollider</c> as well as a mesh, so hiding the mesh alone would leave
        /// an invisible wall across the room. And an anchor object may well *be* the cube — that is
        /// the natural way to place one — so deleting anything that has a renderer would delete the
        /// anchors too. Turning the renderer and the collider off works whichever of the two it is.
        /// </remarks>
        private static void HideVisuals(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = false;
            }
        }

        /// <summary>
        /// Turns every collider under an object on or off.
        /// </summary>
        private static void SetColliders(Transform root, bool enabled)
        {
            if (root == null)
                return;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = enabled;
            }
        }

        /// <summary>
        /// Fills the station and desk lists, remembering where each was authored.
        /// </summary>
        /// <remarks>
        /// **Sorted, and captured once.** Both matter — see <see cref="_stations"/> for the sorting,
        /// and the authored pose for why it is taken on the first layout and then kept: after a
        /// round the pieces are standing on anchors, so "where it was authored" is a question that
        /// can only be answered before anything has moved.
        /// </remarks>
        private void CapturePieces()
        {
            if (_allStations.Count == 0)
            {
                StationBase[] found = FindObjectsByType<StationBase>(FindObjectsInactive.Exclude);

                for (int i = 0; i < found.Length; i++)
                {
                    StationBase station = found[i];

                    /* **A customer is not furniture.**
                     *
                     * They are a StationBase because they carry a request the way a printer carries
                     * a job, which is what lets the round's clear put them back with the same one
                     * call as everything else — see CustomerSpawner, which reuses a handful of them
                     * rather than spawning any. But they are people standing at a counter, and the
                     * office being rearranged around them is not a thing that should happen to
                     * anybody: a shuffled customer would be delivered to from the wrong place, or
                     * from nowhere.
                     *
                     * **Colleague derives from Customer**, so this covers both. A third kind of
                     * person would need adding here — there is no marker to inherit, and that is the
                     * one place this class reaches into a type rather than into an interface. */
                    if (station == null || station is Customer)
                        continue;

                    _allStations.Add(NewPiece(station.transform, station));
                }

                _allStations.Sort(ByAuthoredPosition);
            }
        }

        /// <summary>
        /// Works out which stations this round is using, and switches the rest off.
        /// </summary>
        /// <remarks>
        /// **The office is furnished for its largest round and thinned for smaller ones.** A station
        /// is a scene object, so its number can only move down — everything the biggest round needs
        /// is placed, and a round that does not need all of it has the rest switched off before
        /// anything is laid out.
        ///
        /// **Switched off rather than left standing where it was authored**, because the authored
        /// spot is a place in the room: a printer sitting in it would be a machine on no anchor and
        /// in no layout, which is not a place a printer can be.
        ///
        /// **Zero players counts as one.** A round nobody has joined is still a round, and the
        /// console can start one — an office with no machines in it at all would read as the layout
        /// being broken rather than as nobody being here.
        /// </remarks>
        private void ChooseStations()
        {
            int players = Mathf.Max(_players.Value, 1);

            _stations.Clear();

            for (int i = 0; i < _allStations.Count; i++)
            {
                Piece piece = _allStations[i];
                StationBase station = piece.Station;

                bool used = station != null && station.MinimumPlayers <= players;

                if (used)
                    _stations.Add(piece);

                /* Written only when it would change something. SetActive is cheap but not free, and
                 * a round after a round of the same size would otherwise churn every object in the
                 * office for no reason. */
                if (piece.Transform != null && piece.Transform.gameObject.activeSelf != used)
                    piece.Transform.gameObject.SetActive(used);
            }
        }

        /// <summary>
        /// Makes a piece out of a transform, recording where it is now.
        /// </summary>
        private static Piece NewPiece(Transform transform, StationBase station) => new()
        {
            Transform = transform,
            Station = station,
            AuthoredPosition = transform.position,
            AuthoredRotation = transform.rotation,
        };

        /// <summary>
        /// Complains when a piece is about to land on somebody.
        /// </summary>
        /// <remarks>
        /// Said rather than prevented, because there is nothing sensible to do about it at runtime:
        /// moving the piece would break the layout the seed promised, and moving the player would
        /// put them somewhere the level designer did not choose. The only real fix is to move the
        /// anchor or the spawn, and that is a person with the scene open.
        ///
        /// **Position rather than a physics query.** Nothing has been placed yet in the sense
        /// physics cares about — the colliders are off while the office descends — and an overlap
        /// test that ran before the thing existed would answer no. Two coordinates and a distance is
        /// both simpler and correct here.
        /// </remarks>
        private void ReportPiecesOnSpawns()
        {
            CaptureSpawns();

            if (_spawns.Count == 0)
                return;

            float clearance = Mathf.Max(_spawnClearance, 0f);
            float sqrClearance = clearance * clearance;

            /* **All of them, not the first one.** Returning on the first clash reports one anchor per
             * run, which turns a two-minute tidy-up into one run per anchor — and the run is a whole
             * round. The list is short by construction: this is a mistake somebody makes by dragging
             * two things near each other, not a state that accumulates. */
            string list = string.Empty;
            int clashes = 0;

            for (int i = 0; i < _placements.Count; i++)
            {
                Vector3 landing = _placements[i].Resting;

                for (int j = 0; j < _spawns.Count; j++)
                {
                    Vector3 spawn = _spawns[j].position;
                    Vector3 offset = spawn - landing;

                    /* Flattened, because a spawn and an anchor are both places on a floor. A spawn
                     * marked a little high so the player drops in would otherwise read as clear by
                     * exactly the amount it was raised. */
                    offset.y = 0f;

                    float sqr = offset.sqrMagnitude;
                    if (sqr > sqrClearance)
                        continue;

                    clashes++;

                    list += $"\n    anchor {Describe(landing)} — {Mathf.Sqrt(sqr):0.##} m from spawn {Describe(spawn)}";
                }
            }

            if (clashes == 0)
                return;

            ReportOnce(
                $"{clashes} anchor(s) land within {clearance:0.#} m of a player spawn. Whoever is put there gets shoved when the office comes down, on the frame the round is supposed to start. Move those anchors or those spawns apart:{list}");
        }

        /// <summary>
        /// Fills <see cref="_spawns"/>.
        /// </summary>
        private void CaptureSpawns()
        {
            if (_spawns.Count > 0)
                return;

            TeamZone[] found = FindObjectsByType<TeamZone>(FindObjectsInactive.Exclude);

            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null && found[i].Spawn != null)
                    _spawns.Add(found[i].Spawn);
            }
        }

        /// <summary>
        /// A position, for a log line.
        /// </summary>
        private static string Describe(Vector3 position) =>
            $"({position.x:0.#}, {position.y:0.#}, {position.z:0.#})";

        /// <summary>
        /// Orders two pieces by where they were authored.
        /// </summary>
        /// <remarks>
        /// The one ordering every peer can agree on without talking to anybody. Positions are
        /// authored data, identical in every copy of the scene; the order a scene search returns
        /// them in is not, and a shuffle that ran over two different orders would deal two
        /// different offices.
        ///
        /// Ties are the one case where two machines could in principle disagree — two things
        /// authored at exactly the same coordinates. That is an authoring mistake rather than a
        /// state to resolve, and it is visible in the scene view.
        /// </remarks>
        private static int ByAuthoredPosition(Piece a, Piece b) =>
            ComparePositions(a != null ? a.AuthoredPosition : Vector3.zero,
                             b != null ? b.AuthoredPosition : Vector3.zero);

        private static int ByPositionOfStationAnchor(StationAnchor a, StationAnchor b) =>
            ComparePositions(a != null ? a.Position : Vector3.zero,
                             b != null ? b.Position : Vector3.zero);

        private static int ByPositionOfTableAnchor(TableAnchor a, TableAnchor b) =>
            ComparePositions(a != null ? a.Position : Vector3.zero,
                             b != null ? b.Position : Vector3.zero);

        private static int ComparePositions(Vector3 left, Vector3 right)
        {
            int compare = left.x.CompareTo(right.x);
            if (compare != 0)
                return compare;

            compare = left.z.CompareTo(right.z);
            if (compare != 0)
                return compare;

            return left.y.CompareTo(right.y);
        }

        /// <summary>
        /// Says once that something is wrong.
        /// </summary>
        private void ReportOnce(string reason)
        {
            if (_reported == reason)
                return;

            _reported = reason;

            Debug.LogError($"{nameof(OfficeLayout)} on {gameObject.name}: {reason}.", this);
        }
    }
}
