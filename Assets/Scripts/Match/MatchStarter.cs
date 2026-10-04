using System.Collections.Generic;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Interaction;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Decides when a match begins, and which side everybody is on.
    /// </summary>
    /// <remarks>
    /// Put on the same object as <see cref="ScoreBoard"/> — it is a NetworkBehaviour and needs a
    /// NetworkObject, and the board is the scene object that already has one and is already about
    /// the round rather than about any one machine.
    ///
    /// **Two zones, everyone standing in one, both sides the same size.** That is the condition,
    /// and it is the whole of the match-making: a player picks a side by walking onto it, and the
    /// match starts when the picking has settled. Nothing counts down while somebody is still
    /// walking, and nothing counts down while the sides are uneven — a 2v1 is not a match, it is a
    /// wait for one more person.
    ///
    /// **The countdown restarts rather than resumes.** Somebody stepping out and back in gets a
    /// full countdown, because the alternative is a countdown that has been eroded by a player
    /// pacing in and out of the zone, and the number on screen is the only thing anyone can see.
    ///
    /// **It is deliberately not the round's clock.** <see cref="ScoreBoard"/> owns that and knows
    /// nothing about zones; this knows nothing about time limits. The only thing they share is the
    /// moment the round begins, which is one call.
    ///
    /// **With fewer than two zones in the scene it starts anyway.** A scene that is still being
    /// laid out — no zones placed yet — would otherwise sit there forever with no way to tell that
    /// anything was wrong, and getting the round running badly is much better for finding out what
    /// else is broken than getting it not running at all. The console's <c>start</c> command does
    /// the same thing on purpose, for the same reason: one player cannot make two equal sides.
    /// </remarks>
    [DisallowMultipleComponent]
    public class MatchStarter : NetworkBehaviour
    {
        /// <summary>
        /// How long the countdown runs once the picking has settled.
        /// </summary>
        [Tooltip("How long the countdown runs once everyone is standing in a zone and the sides are even.")]
        [Min(0f)]
        [SerializeField]
        private float _countdownSeconds = 5f;

        /// <summary>
        /// How often to look at where everybody is standing, in seconds.
        /// </summary>
        /// <remarks>
        /// Ten times a second rather than every frame. Walking between two zones takes a second or
        /// two, so a tenth of a second of lag in noticing is invisible — and this walks every
        /// player and tests every zone, which is not work to do sixty times a second for an answer
        /// that changes twice a match.
        /// </remarks>
        [Tooltip("How often to look at where everybody is standing, in seconds.")]
        [Min(0.02f)]
        [SerializeField]
        private float _pollSeconds = 0.1f;

        /// <summary>
        /// The match is counting down. Replicated, so every peer can draw the number.
        /// </summary>
        private readonly SyncVar<bool> _counting = new(false);

        /// <summary>
        /// Whole seconds left on the countdown. Replicated.
        /// </summary>
        /// <remarks>
        /// Whole seconds rather than the exact value, unlike the round's clock. The two are shown
        /// differently: a round clock counts down continuously and is worth a smooth number, while
        /// this is a "3, 2, 1" and only ever changes once a second. Sending an integer means the
        /// number on screen is the number the server decided rather than a client's rounding of a
        /// value that arrived a fraction of a second ago — which is how a countdown ends up
        /// skipping a digit on one machine and not the other.
        /// </remarks>
        private readonly SyncVar<int> _countdown = new(0);

        /// <summary>
        /// The exact countdown on the server, between the once-a-second writes.
        /// </summary>
        private float _exactCountdown;

        /// <summary>
        /// Seconds until the next look at where everybody is.
        /// </summary>
        private float _timer;

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Set once a wiring complaint has been made, so it is not repeated on every poll.
        /// </summary>
        private string _reported;

        /// <summary>
        /// Reused by the scan, so a poll allocates nothing.
        /// </summary>
        private readonly List<PlayerInteraction> _players = new();

        /// <summary>
        /// Reused by the scan. Parallel to <see cref="_players"/>.
        /// </summary>
        private readonly List<int> _playerTeams = new();

        /// <summary>
        /// Reused by the scan: how many players are in each team's zone, indexed by team.
        /// </summary>
        private readonly List<int> _zoneCounts = new();

        /// <summary>
        /// Reused by the scan.
        /// </summary>
        private readonly List<TeamZone> _zones = new();

        /// <summary>
        /// The starter in the scene, or null before it has spawned.
        /// </summary>
        public static MatchStarter Instance { get; private set; }

        /// <summary>
        /// True while the pre-match countdown is running.
        /// </summary>
        /// <remarks>
        /// Read by whatever draws the number. Polled rather than evented, like the round clock and
        /// for the same reason: a countdown is a value something looks at every frame, and an event
        /// that fires once a second to redraw one digit is more machinery than the digit is worth.
        /// </remarks>
        public bool IsCountingDown => _counting.Value;

        /// <summary>
        /// Whole seconds left on the countdown, or zero when it is not running.
        /// </summary>
        public int CountdownRemaining => IsServerInitialized ? Mathf.CeilToInt(_exactCountdown) : _countdown.Value;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(MatchStarter)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; the match would start twice.",
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

            /* Subscribed by hand rather than through TickNetworkBehaviour, for the reason the
             * scoreboard and the printer both give: OnTick runs two or three times a frame and may
             * drop ticks, which is no basis for a countdown. */
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
        /// Server: one frame.
        /// </summary>
        /// <remarks>
        /// Two rates in one method, deliberately. The question "are the sides even" is asked ten
        /// times a second, because walking between two zones takes a second or two and nobody can
        /// see a tenth of a second of lag in noticing. The countdown itself is advanced every
        /// frame, because a number that only moved at the poll rate would visibly tick in steps.
        ///
        /// Keeping both here rather than in a poll and an <c>Update</c> is what makes the countdown
        /// have one owner: split across two methods, the one that resets it and the one that spends
        /// it can disagree about whether it is running.
        /// </remarks>
        private void ServerUpdate()
        {
            ScoreBoard scores = ScoreBoard.Instance;

            /* Nothing to decide once the round is running. The zones stay in the scene — they are
             * where the players are — but walking out of one after the match has begun is just
             * walking, not a change of side. */
            if (scores == null || scores.HasStarted)
                return;

            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            _timer -= deltaTime;
            if (_timer <= 0f)
            {
                _timer = _pollSeconds;
                Reconsider(scores);
            }

            if (!_counting.Value)
                return;

            _exactCountdown = Mathf.Max(_exactCountdown - deltaTime, 0f);

            /* Only the whole second goes on the wire, and only when it changes — so a five second
             * countdown is five writes rather than three hundred. */
            int whole = Mathf.CeilToInt(_exactCountdown);
            if (_countdown.Value != whole)
                _countdown.Value = whole;

            if (_exactCountdown > 0f)
                return;

            /* Checked again rather than trusted. The poll that started this ran up to a tenth of a
             * second ago, and in that tenth somebody can have stepped out of their zone — which
             * would start the match 2v1 for exactly as long as it takes the next poll to notice. */
            if (!SidesAreEven(scores.TeamCount))
            {
                CancelCountdown();
                return;
            }

            BeginMatch(scores);
        }

        /// <summary>
        /// Server: decides whether the countdown should be running.
        /// </summary>
        private void Reconsider(ScoreBoard scores)
        {
            CollectPlayers();

            if (_players.Count == 0)
                return;

            CollectZones();

            /* A scene still being laid out, or one where somebody deleted a zone. Starting anyway
             * beats waiting forever with nothing on screen to say why — see the class remarks. */
            if (!HasEveryTeamAZone(scores.TeamCount))
            {
                ReportOnce($"fewer than {scores.TeamCount} {nameof(TeamZone)} in the scene, so nobody can pick a side; starting without them");
                ServerForceStart();
                return;
            }

            if (!SidesAreEven(scores.TeamCount))
            {
                /* Cancelled and re-armed rather than merely paused, so that a player pacing in and
                 * out of a zone gets a full countdown each time instead of eroding one. The number
                 * on screen is the only thing anyone can see, and a number that resumes from
                 * wherever it was left is a number that means nothing. */
                CancelCountdown();
                _exactCountdown = _countdownSeconds;
                return;
            }

            if (_counting.Value)
                return;

            _exactCountdown = _countdownSeconds;
            _countdown.Value = Mathf.CeilToInt(_countdownSeconds);
            _counting.Value = true;
        }

        /// <summary>
        /// Server: everybody into a zone, and both sides the same size.
        /// </summary>
        /// <remarks>
        /// Reads the lists <see cref="CollectPlayers"/> and <see cref="CollectZones"/> filled, so it
        /// is only meaningful after both have run this frame. Every player has to be somewhere: a
        /// player standing outside both zones has not picked, and a match that started without them
        /// would be putting somebody on a side they never chose.
        /// </remarks>
        private bool SidesAreEven(int teamCount)
        {
            _zoneCounts.Clear();
            for (int team = 0; team < teamCount; team++)
                _zoneCounts.Add(0);

            for (int i = 0; i < _players.Count; i++)
            {
                int team = _playerTeams[i];

                /* -1 is "not in any zone", which is not a side and does not count towards one. */
                if (team < 0 || team >= teamCount)
                    return false;

                _zoneCounts[team] = _zoneCounts[team] + 1;
            }

            /* Two is the smallest match there is. One player alone in a zone would otherwise be a
             * 1-0 split that reads as even to a loop that only compares the counts against each
             * other — see the class remarks for why that is the console's job instead. */
            if (_players.Count < 2)
                return false;

            for (int team = 1; team < teamCount; team++)
            {
                if (_zoneCounts[team] != _zoneCounts[0])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Server: stops the countdown and puts the number back.
        /// </summary>
        private void CancelCountdown()
        {
            _counting.Value = false;
            _countdown.Value = 0;
        }

        /// <summary>
        /// Server: sends everybody to their side and starts the round.
        /// </summary>
        private void BeginMatch(ScoreBoard scores)
        {
            CancelCountdown();

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerInteraction player = _players[i];
                int team = _playerTeams[i];

                if (player == null)
                    continue;

                if (player.Team != team)
                    player.ServerSetTeam(team);

                TeamZone zone = FindZone(team);
                if (zone != null && zone.Spawn != null)
                    MoveTo(player, zone.Spawn.position);
            }

            scores.ServerBeginRound();
        }

        /// <summary>
        /// Server: starts the match now, sides by turns, wherever anybody happens to be standing.
        /// </summary>
        /// <remarks>
        /// The seam that makes the game playable by one person. The rule above needs two equal
        /// sides, and one player cannot make two equal sides — so a solo session would never begin,
        /// which is exactly the session somebody testing a printer is in. It is also what runs when
        /// the scene has no zones yet, for the same reason.
        ///
        /// By turns rather than by zone, because there is nothing to read a side off. The console
        /// command and the missing-zones path both land here, and after this the console's
        /// <c>team</c> command can still move anybody it likes.
        /// </remarks>
        [Server]
        public bool ServerForceStart()
        {
            ScoreBoard scores = ScoreBoard.Instance;
            if (scores == null || scores.HasStarted)
                return false;

            CollectPlayers();

            int teamCount = Mathf.Max(1, scores.TeamCount);
            int next = 0;

            _players.Sort(ByOwnerId);

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerInteraction player = _players[i];
                if (player == null)
                    continue;

                int team = next % teamCount;
                next++;

                if (player.Team != team)
                    player.ServerSetTeam(team);

                TeamZone zone = FindZone(team);
                if (zone != null && zone.Spawn != null)
                    MoveTo(player, zone.Spawn.position);
            }

            scores.ServerBeginRound();

            CancelCountdown();
            return true;
        }

        /// <summary>
        /// Puts a player somewhere, on the server.
        /// </summary>
        /// <remarks>
        /// The controller is switched off around the write for the reason the console's teleport
        /// gives: an enabled CharacterController overrides a transform write on the next move and
        /// the player stays where they were. Safe to do on the server because movement is
        /// reconciled from the server's position — the client is pulled to wherever this put it,
        /// which is the same mechanism that corrects any other disagreement.
        /// </remarks>
        private static void MoveTo(PlayerInteraction player, Vector3 position)
        {
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null)
                controller.enabled = false;

            player.transform.position = position;

            if (controller != null)
                controller.enabled = true;
        }

        /// <summary>
        /// Fills <see cref="_players"/>, and the zone each one is standing in.
        /// </summary>
        private void CollectPlayers()
        {
            _players.Clear();
            _playerTeams.Clear();

            PlayerInteraction[] found = FindObjectsByType<PlayerInteraction>(FindObjectsInactive.Exclude);

            for (int i = 0; i < found.Length; i++)
            {
                PlayerInteraction player = found[i];

                /* An object that is not spawned yet has no owner and no team to set. It is picked
                 * up on a later poll once FishNet has finished with it. */
                if (player == null || !player.IsSpawned || !player.IsServerInitialized)
                    continue;

                _players.Add(player);
                _playerTeams.Add(ZoneOf(player.transform.position));
            }
        }

        /// <summary>
        /// Refreshes the zone list.
        /// </summary>
        /// <remarks>
        /// Searched every poll rather than cached: a zone added while the game is running should
        /// start working, and the list is two objects long. A duplicate team number is reported once
        /// and then both are used anyway — the counts would be wrong, but refusing to run would be
        /// a worse answer to a mistake that is visible in the inspector.
        /// </remarks>
        private void CollectZones()
        {
            _zones.Clear();

            TeamZone[] found = FindObjectsByType<TeamZone>(FindObjectsInactive.Exclude);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null)
                    _zones.Add(found[i]);
            }

            for (int i = 0; i < _zones.Count; i++)
            {
                for (int j = i + 1; j < _zones.Count; j++)
                {
                    if (_zones[i].Team != _zones[j].Team)
                        continue;

                    ReportOnce($"two {nameof(TeamZone)} objects both say they are team {_zones[i].Team}, so that side counts double");
                    return;
                }
            }
        }

        /// <summary>
        /// Which zone a world position is in, or -1 for none.
        /// </summary>
        /// <remarks>
        /// The first match wins. Zones that overlap are a layout mistake rather than a state to
        /// resolve, and the first one found is at least the same one every poll — a player standing
        /// in an overlap would otherwise flicker between sides.
        /// </remarks>
        private int ZoneOf(Vector3 worldPosition)
        {
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_zones[i].Contains(worldPosition))
                    return _zones[i].Team;
            }

            return -1;
        }

        /// <summary>
        /// The zone for a team, or null.
        /// </summary>
        private TeamZone FindZone(int team)
        {
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_zones[i].Team == team)
                    return _zones[i];
            }

            return null;
        }

        /// <summary>
        /// True when every team has somewhere to stand.
        /// </summary>
        private bool HasEveryTeamAZone(int teamCount)
        {
            for (int team = 0; team < teamCount; team++)
            {
                if (FindZone(team) == null)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Says once that something is wrong.
        /// </summary>
        /// <remarks>
        /// Once per distinct complaint rather than once per poll. This runs ten times a second, so
        /// a scene with two zones on the same team would otherwise fill the console with the same
        /// line and bury everything else being said. There is no path that clears this: every
        /// complaint here is a setup mistake, and none of them can be fixed while the game runs.
        /// </remarks>
        private void ReportOnce(string reason)
        {
            if (_reported == reason)
                return;

            _reported = reason;

            Debug.LogError($"{nameof(MatchStarter)} on {gameObject.name}: {reason}.", this);
        }

        /// <summary>
        /// Orders two players by the connection they belong to.
        /// </summary>
        /// <remarks>
        /// Held as a field rather than written at the call site: a lambda passed to Sort is a new
        /// delegate every call, and this is the kind of thing that is easy to write and hard to
        /// notice.
        /// </remarks>
        private static readonly System.Comparison<PlayerInteraction> ByOwnerId = CompareByOwnerId;

        private static int CompareByOwnerId(PlayerInteraction a, PlayerInteraction b)
        {
            int left = a != null ? a.OwnerId : int.MaxValue;
            int right = b != null ? b.OwnerId : int.MaxValue;

            return left.CompareTo(right);
        }
    }
}
