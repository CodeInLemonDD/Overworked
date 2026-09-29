using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using Overworked.Containers;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Turns paper and ink into printed sheets, and shares its output time between the players
    /// who fed it.
    /// </summary>
    /// <remarks>
    /// A machine standing in a cell, not furniture with a machine on it. The table that comes
    /// with the prefab is scenery; the machine's own root carries the input container, the
    /// placement blocker and the collider, and the Output child carries the second container
    /// and its view.
    ///
    /// Input arrives physically. There is no press that hands a station something — E with full
    /// hands is drop or throw, and the interaction never reaches a station while carrying — so
    /// feeding a machine means dropping or throwing the item into it, and the machine watches
    /// its own intake box for that. The box is a tunable volume rather than the grid cell for
    /// the same reason: a machine is wider than one cell, and an item is released at arm's
    /// length in front of the player rather than at their feet.
    ///
    /// Who fed an item travels in the entry as OwnerClientId, because it cannot be recovered
    /// later. A grabbable clears its holder the moment it comes to rest, so the drop is the
    /// last point at which anything knows. An item still owned by its thrower is credited from
    /// that; anything the machine saw someone carrying it remembers for a while, so an item
    /// that had to wait on a full machine still reaches the right queue.
    ///
    /// Making a sheet is not replicated. The progress is a server-side timer, and the only
    /// state other peers hold is the output container, which is a SyncList like any other.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Printer : StationBase
    {
        [Header("Containers")]

        /// <summary>
        /// The input container on this object: what players have fed in.
        /// </summary>
        [Tooltip("The input container on this object: the paper and ink players have fed in.")]
        [SerializeField]
        private ContainerBase _input;

        /// <summary>
        /// The output container on the Output child: printed sheets waiting to be taken.
        /// </summary>
        [Tooltip("The output container on the Output child: printed sheets waiting to be taken.")]
        [SerializeField]
        private ContainerBase _output;

        [Header("Recipe")]

        /// <summary>
        /// Payload index that counts as paper.
        /// </summary>
        [Tooltip("Payload index that counts as paper. Must match the index the paper box hands out.")]
        [SerializeField]
        private int _paperPayloadIndex;

        /// <summary>
        /// Payload index that counts as ink.
        /// </summary>
        [Tooltip("Payload index that counts as ink.")]
        [SerializeField]
        private int _inkPayloadIndex = 1;

        /// <summary>
        /// Payload index of a printed sheet.
        /// </summary>
        [Tooltip("Payload index of a printed sheet, as drawn on the machine and handed to the player.")]
        [SerializeField]
        private int _outputPayloadIndex = 2;

        /// <summary>
        /// Seconds of work per sheet.
        /// </summary>
        /// <remarks>
        /// Blind-set, like every other timing in the project. What it has to beat is the trip to
        /// the paper box and back, since that round trip is the real cost of a sheet.
        /// </remarks>
        [Tooltip("Seconds of work per printed sheet.")]
        [Min(0f)]
        [SerializeField]
        private float _secondsPerOutput = 3f;

        [Header("Intake")]

        /// <summary>
        /// Centre of the intake box, in this object's local space.
        /// </summary>
        /// <remarks>
        /// Local and above the base, not at the pivot. A station's root sits at the floor under
        /// the table, so a box centred on the pivot would swallow things at the player's feet
        /// and miss the machine.
        /// </remarks>
        [Tooltip("Centre of the intake box, in local space. A station's root sits at the floor, so this is raised above it.")]
        [SerializeField]
        private Vector3 _intakeCentre = new(0f, 0.6f, 0f);

        /// <summary>
        /// Half the size of the intake box.
        /// </summary>
        [Tooltip("Half the size of the intake box. Drawn as a gizmo when this object is selected.")]
        [SerializeField]
        private Vector3 _intakeHalfExtents = new(0.8f, 0.8f, 0.8f);

        /// <summary>
        /// How far past the intake box the machine keeps watching what a player is carrying.
        /// </summary>
        /// <remarks>
        /// This is what credits the feeder. The player drops an item from outside the box as
        /// often as into it, and by the time it has been swallowed the item no longer knows who
        /// was holding it — so the machine has to have noticed earlier, while it still did.
        /// </remarks>
        [Tooltip("How far past the intake box the machine keeps watching what a player is carrying, so a missed drop can still be credited.")]
        [Min(0f)]
        [SerializeField]
        private float _feederTrackMargin = 1.5f;

        [Header("Power")]

        /// <summary>
        /// Whether the machine runs.
        /// </summary>
        [Tooltip("Whether the machine runs. Always on this round; power zoning will drive this later.")]
        [SerializeField]
        private bool _isPowered = true;

        /// <summary>
        /// Per-faction rotation over the input container.
        /// </summary>
        private readonly PrinterQueue _queue = new();

        /// <summary>
        /// Server-only: who the machine last saw carrying each item near it.
        /// </summary>
        /// <remarks>
        /// Bounded by the tracking box, not by the whole scene: entries are dropped as objects
        /// leave the box, and again when they are destroyed. It exists only because the
        /// grabbable forgets its holder on release, and an item that had to wait for room would
        /// otherwise arrive with no owner and land in no one's queue.
        /// </remarks>
        private readonly Dictionary<NetworkGrabbable, int> _feeders = new();

        /// <summary>
        /// Reused by the intake scan. See <see cref="GrabbableSpawner.CollectSpawnedGrabbables"/>.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        /// <summary>
        /// Reused when pruning <see cref="_feeders"/>.
        /// </summary>
        private readonly List<NetworkGrabbable> _staleFeeders = new();

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// Server-only: true while a sheet is being made.
        /// </summary>
        private bool _crafting;

        /// <summary>
        /// Server-only: seconds of work banked on the current sheet.
        /// </summary>
        private float _craftSeconds;

        /// <summary>
        /// Server-only: the client whose job is being made, or -1.
        /// </summary>
        private int _craftOwnerClientId = -1;

        /// <summary>
        /// Whether the machine is running.
        /// </summary>
        public bool IsPowered => _isPowered;

        /// <summary>
        /// Server: turns the machine on or off.
        /// </summary>
        /// <remarks>
        /// The seam a power system will drive. Nothing calls it yet and the machine is authored
        /// powered, so this round it is effectively a constant.
        /// </remarks>
        [Server]
        public void ServerSetPowered(bool powered) => _isPowered = powered;

        /// <summary>
        /// Smallest input capacity that can hold two players' jobs at once.
        /// </summary>
        /// <remarks>
        /// Two entries per job, and the rotation only means anything while two owners can be
        /// queued together. See the warning in <see cref="OnStartServer"/> for what a smaller
        /// one does.
        /// </remarks>
        private const int TwoJobCapacity = 4;

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_input == null || _output == null)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} is missing its input or output container and will do nothing.",
                    this);
            }
            else if (_input == _output)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} has the same container wired as both input and output.",
                    this);
            }
            else if (!_input.IsUnlimited && _input.Capacity < TwoJobCapacity)
            {
                /* Worth shouting about, because the failure is permanent and looks like a bug
                 * in the machine rather than in the prefab. Nothing takes an item back out of
                 * the input — the machine only ever hands sheets out of the output — so a
                 * player who fills a small input with one kind of item leaves no room for the
                 * other kind, and the machine never runs again. An unlimited input cannot be
                 * filled, so it cannot get stuck; that is the intended wiring. */
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has an input capacity of {_input.Capacity}; " +
                    $"below {TwoJobCapacity} a single player can fill it with paper and stall the machine for good. " +
                    "Make the input container unlimited.",
                    this);
            }

            _queue.Reset();
            _feeders.Clear();

            /* Subscribed by hand rather than through TickNetworkBehaviour, which is where this
             * hook normally comes from: a station has to stay a plain NetworkBehaviour, and the
             * base class offers it. OnTick is no substitute — it runs two or three times a frame
             * and may drop ticks, which is no basis for a timer. */
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

            _queue.Reset();
            _feeders.Clear();

            base.OnStopServer();
        }

        /// <summary>
        /// Server: one frame of the machine.
        /// </summary>
        private void ServerUpdate()
        {
            if (!_isPowered)
                return;

            /* Unscaled, per the project's timer rule: this is a machine rate, and a dropped
             * frame or a paused editor must not change how long a sheet takes. */
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            /* Intake first, so something fed this frame can start a job this frame. */
            UpdateIntake();
            UpdateCrafting(deltaTime);
        }

        /// <summary>
        /// Server: swallows what has been dropped or thrown into the machine, and notes who is
        /// carrying what near it.
        /// </summary>
        private void UpdateIntake()
        {
            Vector3 trackHalfExtents = _intakeHalfExtents + Vector3.one * _feederTrackMargin;

            /* Snapshot first. Despawning while enumerating ServerManager.Objects.Spawned throws,
             * because that collection is a live view over a Dictionary and Despawn removes the
             * key synchronously. The buffer is our own list, so removing from the world while
             * walking it is safe. */
            GrabbableSpawner.CollectSpawnedGrabbables(NetworkManager, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
            {
                NetworkGrabbable grabbable = _scanBuffer[i];
                if (grabbable == null || !grabbable.IsSpawned)
                    continue;

                NetworkObject nob = grabbable.NetworkObject;
                if (nob == null)
                    continue;

                /* A scene object despawns to SetActive(false) with no way back. Nothing should
                 * be authoring grabbables in the scene, but the failure is silent and
                 * permanent, so it is worth one check. */
                if (nob.IsSceneObject)
                    continue;

                Vector3 position = grabbable.transform.position;

                if (!IsInsideBox(trackHalfExtents, _intakeCentre, position))
                {
                    /* Out of range, so forget it. Without this the machine would keep a record
                     * for every object ever carried past it. */
                    _feeders.Remove(grabbable);
                    continue;
                }

                if (grabbable.State == GrabbableState.Held)
                {
                    if (grabbable.HolderId >= 0)
                        _feeders[grabbable] = grabbable.HolderId;

                    continue;
                }

                TrySwallow(grabbable, position);
            }

            ForgetDestroyedFeeders();
            _scanBuffer.Clear();
        }

        /// <summary>
        /// Drops feeder records for objects that are gone.
        /// </summary>
        /// <remarks>
        /// The keys are Unity objects, which compare equal to null once destroyed but stay in
        /// the dictionary until removed. Collected first because the dictionary cannot be
        /// edited while it is being enumerated.
        /// </remarks>
        private void ForgetDestroyedFeeders()
        {
            if (_feeders.Count == 0)
                return;

            foreach (KeyValuePair<NetworkGrabbable, int> pair in _feeders)
            {
                if (pair.Key == null)
                    _staleFeeders.Add(pair.Key);
            }

            for (int i = 0; i < _staleFeeders.Count; i++)
                _feeders.Remove(_staleFeeders[i]);

            _staleFeeders.Clear();
        }

        /// <summary>
        /// Server: takes an item into the input container if it is in the intake box.
        /// </summary>
        private void TrySwallow(NetworkGrabbable grabbable, Vector3 position)
        {
            if (!IsInsideBox(_intakeHalfExtents, _intakeCentre, position))
                return;

            int payload = grabbable.PayloadIndex;
            if (!IsAcceptedInput(payload))
                return;

            /* No room: the item stays in the world, where its feeder can still pick it back up.
             * It is only swallowed when the machine can actually keep it, so nothing a player
             * paid for is ever destroyed for want of a slot. */
            if (_input == null || _input.IsFull)
                return;

            if (!_input.ServerTryAdd(ContainerEntry.ForEntity(payload, ResolveFeeder(grabbable))))
                return;

            _feeders.Remove(grabbable);

            /* Destroy rather than pool: these objects carry per-life state — the placed cell,
             * the settle timer, the payload — that a recycled instance would bring back with
             * it. Passed explicitly so this does not depend on the prefab. */
            grabbable.NetworkObject.Despawn(DespawnType.Destroy);
        }

        /// <summary>
        /// True when this payload is something the machine can use.
        /// </summary>
        /// <remarks>
        /// A printed sheet fed back in is not an input, so it is left lying on the machine
        /// rather than being eaten and printed again.
        /// </remarks>
        private bool IsAcceptedInput(int payloadIndex) =>
            payloadIndex >= 0
            && (payloadIndex == _paperPayloadIndex || payloadIndex == _inkPayloadIndex);

        /// <summary>
        /// Which client should be credited for an item.
        /// </summary>
        /// <returns>The client id, or -1 when nobody can be credited.</returns>
        private int ResolveFeeder(NetworkGrabbable grabbable)
        {
            if (_feeders.TryGetValue(grabbable, out int feeder) && feeder >= 0)
                return feeder;

            /* Still owned, which is the case for anything that arrives without the machine
             * having seen it carried — a throw from across the room, for instance. That client
             * is also the one simulating it. */
            return grabbable.NetworkObject.OwnerId;
        }

        /// <summary>
        /// True when a world position falls inside a box around this object.
        /// </summary>
        private bool IsInsideBox(Vector3 halfExtents, Vector3 centre, Vector3 worldPosition)
        {
            Vector3 offset = transform.InverseTransformPoint(worldPosition) - centre;

            return Mathf.Abs(offset.x) <= halfExtents.x
                && Mathf.Abs(offset.y) <= halfExtents.y
                && Mathf.Abs(offset.z) <= halfExtents.z;
        }

        /// <summary>
        /// Server: advances the sheet being made, or starts one.
        /// </summary>
        private void UpdateCrafting(float deltaTime)
        {
            if (_crafting)
            {
                if (_craftSeconds < _secondsPerOutput)
                    _craftSeconds = Mathf.Min(_craftSeconds + deltaTime, _secondsPerOutput);

                if (_craftSeconds >= _secondsPerOutput)
                    FinishCraft();

                return;
            }

            if (IsOutputBlocked())
                return;

            TryBeginCraft();
        }

        /// <summary>
        /// True when a finished sheet would have nowhere to go.
        /// </summary>
        /// <remarks>
        /// The output is the machine's own buffer, so a full one stalls the machine rather than
        /// spilling onto the floor. That is also the only back-pressure there is: keeping the
        /// machine fed and never emptying it is what the other player is up against.
        /// </remarks>
        private bool IsOutputBlocked() => _output == null || _output.IsFull;

        /// <summary>
        /// Server: charges the next owner for a job and starts work.
        /// </summary>
        private void TryBeginCraft()
        {
            if (_input == null || _output == null)
                return;

            /* An index used for both would make the machine wait forever for a second item that
             * can never be a different one. OnValidate says so out loud; this makes it inert
             * rather than destructive in the meantime. */
            if (_paperPayloadIndex < 0 || _inkPayloadIndex < 0 || _paperPayloadIndex == _inkPayloadIndex)
                return;

            int owner = _queue.SelectNextOwner(_input, _paperPayloadIndex, _inkPayloadIndex);
            if (owner < 0)
                return;

            /* Only charged once the machine is certain it can run the job, and for the whole
             * job at once: half a recipe consumed for nothing is a loss the player cannot see
             * coming. */
            if (!_queue.TryConsumeJob(_input, owner, _paperPayloadIndex, _inkPayloadIndex))
                return;

            _crafting = true;
            _craftSeconds = 0f;
            _craftOwnerClientId = owner;
        }

        /// <summary>
        /// Server: adds the finished sheet to the output.
        /// </summary>
        /// <remarks>
        /// Data is not built yet, so a job is paper and ink and nothing else. When documents
        /// exist, this is where the third requirement goes: the job only starts once the same
        /// owner's entries also hold a ContainerEntryKind.Data, and that entry is consumed with
        /// the other two in <see cref="PrinterQueue.TryConsumeJob"/>.
        /// </remarks>
        private void FinishCraft()
        {
            /* If the output filled up while this sheet was being made, the work is kept and the
             * sheet stays in the machine until there is room. Dropping it would destroy paper
             * and ink the player already paid. */
            if (!_output.ServerTryAdd(ContainerEntry.ForEntity(_outputPayloadIndex, _craftOwnerClientId)))
                return;

            _crafting = false;
            _craftSeconds = 0f;
            _craftOwnerClientId = -1;
        }

        /// <summary>
        /// Server: hands the player the top sheet from the output.
        /// </summary>
        /// <remarks>
        /// Long presses are ignored: taking a sheet and taking a stack of them would read as the
        /// same gesture with two meanings, and nothing here needs a second verb.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)
        {
            if (player == null || conn == null)
                return;
            if (_output == null || _output.Count == 0)
                return;

            /* Full hands: a sheet handed over now would be dropped or thrown by whatever the
             * player does next. Refusing is the only answer that cannot lose it. */
            if (NetworkGrabbable.IsHeldBy(NetworkManager, conn.ClientId))
                return;

            /* The newest sheet, which is the top of the pile. The view draws the front of the
             * list at the bottom, so taking from the back leaves the rest of the pile where the
             * player can see it instead of shuffling it down under their hand. */
            if (!_output.TryGetEntry(_output.Count - 1, out ContainerEntry entry))
                return;
            if (entry.Kind != (byte)ContainerEntryKind.Entity)
                return;

            /* Checked before anything is removed or spawned, so a scene with no spawner reports
             * the problem instead of quietly deleting a sheet. */
            GrabbableSpawner spawner = GrabbableSpawner.Instance;
            if (spawner == null || spawner.ObjectPrefab == null)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} found no {nameof(GrabbableSpawner)} to spawn from; the sheet was left in the machine.",
                    this);
                return;
            }

            /* Spawn before removing, so the failure above and any other one costs the machine a
             * sheet rather than the player one. */
            NetworkObject nob = GrabbableSpawner.SpawnGrabbable(
                entry.PayloadIndex,
                player.HandPosition,
                Quaternion.identity,
                conn);

            if (nob == null)
                return;

            _output.ServerTryRemoveLast();

            /* Marks the sheet held as well as handing it over. See ServerHandToPlayer: the
             * server's own copy of the state is what "hands empty", the collider switch and the
             * drop and throw guards all read, and a spawned object starts Idle with no holder. */
            player.ServerHandToPlayer(nob);
        }

        private void OnValidate()
        {
            _secondsPerOutput = Mathf.Max(0f, _secondsPerOutput);

            _intakeHalfExtents = new Vector3(
                Mathf.Max(0.01f, _intakeHalfExtents.x),
                Mathf.Max(0.01f, _intakeHalfExtents.y),
                Mathf.Max(0.01f, _intakeHalfExtents.z));

            if (_paperPayloadIndex >= 0 && _paperPayloadIndex == _inkPayloadIndex)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} uses payload {_paperPayloadIndex} for both paper and ink; no job can ever start.",
                    this);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            /* Two boxes: what is swallowed, and the larger one the machine only watches. The
             * gap between them is the margin that credits a feeder whose item landed short. */
            Gizmos.color = new Color(0.3f, 0.85f, 0.5f, 0.9f);
            Gizmos.DrawWireCube(_intakeCentre, _intakeHalfExtents * 2f);

            Gizmos.color = new Color(0.3f, 0.85f, 0.5f, 0.3f);
            Gizmos.DrawWireCube(_intakeCentre, (_intakeHalfExtents + Vector3.one * _feederTrackMargin) * 2f);

            Gizmos.matrix = previous;
        }
    }
}
