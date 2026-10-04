using System.Collections.Generic;
using FishNet;
using Overworked.Interaction;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Player
{
    /// <summary>
    /// Puts each player on a team, in the order they arrived.
    /// </summary>
    /// <remarks>
    /// Until this existed, every player in the game was on team 0 and the only thing that could
    /// move them was the console's <c>team</c> command. That was enough to test one side's
    /// paperwork and nothing else: with both players on the same team there is no race for a
    /// customer, no "the other side got there first", and no way to see that the two teams'
    /// documents never meet. The round is built on two sides competing, so two sides have to exist
    /// without somebody typing a command every time an instance starts.
    ///
    /// **The order is connection order**, taken from the client id the server hands out. FishNet
    /// has no "a player spawned" event to hang this on — <c>ServerObjects</c> raises one for
    /// objects being destroyed and none for objects appearing — so this watches instead of
    /// listening. Watching on its own would make the order whichever player a scene search
    /// happened to reach first, so the list is sorted before anybody is assigned: within one
    /// pass, the lower client id takes the lower team.
    ///
    /// The order is only a tie-break. What actually hands out teams is a counter that only goes
    /// up, which is what makes the result alternate rather than depend on who was seen when: two
    /// players get 0 and 1 whichever of them the search found first, and a third gets 0 again.
    ///
    /// **A player is assigned once, and then never touched again.** That is what leaves the
    /// console's <c>team</c> command working: it is the override, and an assigner that kept
    /// re-asserting its own answer every quarter second would silently undo it. Remembering is
    /// therefore not an optimisation but the whole of how the two coexist.
    ///
    /// Deliberately not done here: keeping a team across a reconnect (the slot is given up when
    /// the player goes), spectator seats, any notion of a team having a colour or a name, and any
    /// balancing beyond strict alternation. A player who joins third gets team 0 and the sides are
    /// 2–1 until somebody else arrives; deciding what to do about that is match making, and this
    /// is not it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class TeamAssigner : MonoBehaviour
    {
        /// <summary>
        /// How often to look for players that have not been given a team.
        /// </summary>
        /// <remarks>
        /// A player spawns immediately on connecting, so the wait between arriving and being put
        /// on a side is this number plus a frame. A quarter of a second is short enough that
        /// nobody has picked anything up yet, and long enough that a scene search four times a
        /// second costs nothing — the same interval the customer spawner uses for the same reason.
        /// </remarks>
        [Tooltip("How often to look for players that have not been given a team, in seconds.")]
        [Min(0.05f)]
        [SerializeField]
        private float _pollSeconds = 0.25f;

        /// <summary>
        /// The assigner in the scene, or null before it has started.
        /// </summary>
        /// <remarks>
        /// Not read by anything yet. It is here for the same reason the other scene singletons are:
        /// a console command or a readout that wants to ask about teams should not have to search
        /// for the object to ask, and adding the property later means touching every caller.
        /// </remarks>
        public static TeamAssigner Instance { get; private set; }

        /// <summary>
        /// Reused by the scene search, so a poll allocates nothing but the array Unity hands back.
        /// </summary>
        private readonly List<PlayerInteraction> _players = new();

        /// <summary>
        /// Object ids of the players that have already been given a team.
        /// </summary>
        /// <remarks>
        /// Keyed by object id rather than by connection, because a connection id is reused and an
        /// object id is not: a player that leaves and comes back is a new object and gets a new
        /// team, which is the honest reading of "a team is not kept across a reconnect".
        /// </remarks>
        private readonly HashSet<int> _assigned = new();

        /// <summary>
        /// Reused by the prune, so it allocates nothing.
        /// </summary>
        private readonly List<int> _pruneBuffer = new();

        /// <summary>
        /// Orders two players by the connection they belong to.
        /// </summary>
        /// <remarks>
        /// Held as a field rather than written at the call site: a lambda passed to Sort is a new
        /// delegate every poll, and this is the kind of thing that is easy to write and hard to
        /// notice.
        /// </remarks>
        private static readonly System.Comparison<PlayerInteraction> ByOwnerId = CompareByOwnerId;

        /// <summary>
        /// The next team to hand out. Only ever goes up.
        /// </summary>
        private int _next;

        /// <summary>
        /// Seconds until the next look.
        /// </summary>
        private float _timer;

        /// <summary>
        /// Set once a complaint about the wiring has been made, so it is not repeated every poll.
        /// </summary>
        private bool _reported;

        private void OnEnable()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(TeamAssigner)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; players would be put on teams twice.",
                    this);
            }

            Instance = this;
        }

        private void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!InstanceFinder.IsServerStarted)
                return;

            /* Unscaled, matching every other timer in the project: this is a pacing delay, and a
             * paused editor must not turn it into something else. */
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _timer -= deltaTime;
            if (_timer > 0f)
                return;

            _timer = _pollSeconds;
            ServerAssign();
        }

        /// <summary>
        /// Server: puts a team on every player that does not have one yet.
        /// </summary>
        private void ServerAssign()
        {
            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null || scores.TeamCount <= 0)
            {
                /* Said once. This runs on a timer, so a scene missing its scoreboard would
                 * otherwise fill the console with the same line and bury everything else — and
                 * nothing here can be fixed while the game is running. */
                if (!_reported)
                {
                    _reported = true;
                    Debug.LogError(
                        $"{nameof(TeamAssigner)} on {gameObject.name} found no {nameof(ScoreBoard)} with teams on it, so there is no telling how many teams to hand out. Players stay on team 0.",
                        this);
                }

                return;
            }

            _players.Clear();
            _players.AddRange(FindObjectsByType<PlayerInteraction>(FindObjectsInactive.Exclude));

            /* Sorted so that two players discovered on the same pass are put on teams in client-id
             * order rather than in whatever order the search reached them. */
            _players.Sort(ByOwnerId);

            PruneAssigned();

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerInteraction player = _players[i];

                /* An object that is not spawned yet has no owner to read and no team to set — it
                 * is caught on a later pass, once FishNet has finished with it. */
                if (player == null || !player.IsSpawned || !player.IsServerInitialized)
                    continue;

                int objectId = player.NetworkObject.ObjectId;
                if (_assigned.Contains(objectId))
                    continue;

                _assigned.Add(objectId);

                int team = _next % scores.TeamCount;
                _next++;

                /* Written only when it differs, because this is a SyncVar and a host's own player
                 * goes through the same path as anybody else's. Setting it to what it already is
                 * would be a write per player per join for no change. */
                if (player.Team != team)
                    player.ServerSetTeam(team);
            }
        }

        /// <summary>
        /// Forgets the players that are no longer in the world.
        /// </summary>
        /// <remarks>
        /// Without this the set grows for the length of the session, which nobody would notice in
        /// a match that lasts five minutes — and which would make the object ids of a player who
        /// left and rejoined be remembered as assigned, so they would never be given a team again.
        /// That second one is silent and permanent, which is reason enough to do this properly
        /// rather than let it slide.
        ///
        /// A player object is spawned for as long as it exists, so asking the spawn collection is
        /// the same question as asking whether it is still in the scene, without a scene search.
        /// </remarks>
        private void PruneAssigned()
        {
            if (_assigned.Count == 0)
                return;

            var spawned = InstanceFinder.ServerManager.Objects.Spawned;
            _pruneBuffer.Clear();

            foreach (int objectId in _assigned)
            {
                if (!spawned.ContainsKey(objectId))
                    _pruneBuffer.Add(objectId);
            }

            for (int i = 0; i < _pruneBuffer.Count; i++)
                _assigned.Remove(_pruneBuffer[i]);

            _pruneBuffer.Clear();
        }

        private static int CompareByOwnerId(PlayerInteraction a, PlayerInteraction b)
        {
            int left = a != null ? a.OwnerId : int.MaxValue;
            int right = b != null ? b.OwnerId : int.MaxValue;

            return left.CompareTo(right);
        }
    }
}
