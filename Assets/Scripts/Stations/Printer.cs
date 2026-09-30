using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Containers;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Turns paper and ink into printed sheets.
    /// </summary>
    /// <remarks>
    /// A machine standing in a cell, not furniture with a machine on it. The table that comes
    /// with the prefab is scenery; the machine's own root carries the paper slot, the placement
    /// blocker and the collider, and its children carry the ink slot, the output and the views.
    ///
    /// Input arrives physically. There is no press that hands a station something — E with full
    /// hands is drop or throw, and the interaction never reaches a station while carrying — so
    /// feeding a machine means dropping or throwing the item into it, and the machine watches
    /// its own intake box for that. The box is a tunable volume rather than the grid cell for
    /// the same reason: a machine is wider than one cell, and an item is released at arm's
    /// length in front of the player rather than at their feet.
    ///
    /// Paper and ink are a public pool and belong to nobody. Whoever carried a sheet over, the
    /// next sheet made uses it, so there is no queue to keep in order and nobody to rotate. The
    /// faction rotation this machine will need belongs one level up, to the documents that come
    /// out of it, and it arrives with the file queue: the algorithm written for it is in
    /// PrinterQueue's git history, waiting for that, and is deliberately not kept here as dead
    /// code in the meantime. Until the data layer exists, one player is enough to run it.
    ///
    /// Ink is spent by the sheet rather than consumed as an item. A cartridge is a count living
    /// in the ink slot until it runs dry, and it is the slot being full that stops a second one
    /// going in — "a cartridge cannot be swapped before it is used up" needs no rule of its own.
    ///
    /// Making a sheet is not replicated. The progress is a server-side timer, and the only
    /// state other peers hold is the output container, which is a SyncList like any other.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Printer : StationBase
    {
        /// <summary>
        /// Sheets of paper the machine is built to hold.
        /// </summary>
        /// <remarks>
        /// Nothing enforces this — the container refuses paper on its own capacity — but the
        /// machine is designed around the number and says so when the prefab disagrees.
        /// </remarks>
        private const int PaperCapacity = 6;

        /// <summary>
        /// Cartridges the ink slot is built to hold.
        /// </summary>
        private const int InkCapacity = 1;

        /// <summary>
        /// Sheets the pile holds.
        /// </summary>
        /// <remarks>
        /// Fixed by the animation rather than chosen: there are six pile slots and six Finished
        /// states, one per slot. An output of any other size would leave sheets with nowhere to
        /// be drawn, so the view checks the container against this.
        /// </remarks>
        public const int PileCapacity = 6;

        [Header("Containers")]

        /// <summary>
        /// The paper slot on this object.
        /// </summary>
        /// <remarks>
        /// Public stock rather than a queue. Whoever puts paper in, the next sheet takes it:
        /// paper is a shared resource and carries nobody's name, so there is nothing to
        /// ration and nobody to rotate. Its own container, separate from the ink, because a
        /// machine with six sheets of paper in it and no ink must still be able to take a
        /// cartridge — one full slot must never block the other.
        /// </remarks>
        [Tooltip("The paper slot. Capacity 6. Full means paper is refused, not that the machine stops.")]
        [SerializeField]
        private ContainerBase _paper;

        /// <summary>
        /// The ink slot on this object.
        /// </summary>
        /// <remarks>
        /// One cartridge, and the cartridge is a count rather than an item: it stays in the
        /// slot until it runs dry, then disappears and frees the slot for the next one. That
        /// the slot is full while a cartridge is in it is the whole of "a cartridge cannot be
        /// swapped before it is used up" — no rule has to be written for it.
        /// </remarks>
        [Tooltip("The ink slot. Capacity 1: one cartridge, which stays until it runs dry.")]
        [SerializeField]
        private ContainerBase _ink;

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
        [Tooltip("Seconds of work per printed sheet. Must match the length of the Printing clips, or the machine will visibly stall.")]
        [Min(0f)]
        [SerializeField]
        private float _secondsPerOutput = 2f;

        [Header("Ink")]

        /// <summary>
        /// Sheets one cartridge is good for.
        /// </summary>
        /// <remarks>
        /// Ink is spent by the sheet, not consumed as an item, because a cartridge is not one
        /// sheet's worth of anything — it is a battery. Modelling it as six separate ink items
        /// would mean six trips to the box and six entries competing for a slot, for a resource
        /// whose only interesting property is how long it lasts.
        /// </remarks>
        [Tooltip("Sheets one cartridge is good for.")]
        [Min(1)]
        [SerializeField]
        private int _printsPerCartridge = 8;

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

        [Header("Power")]

        /// <summary>
        /// Whether the machine runs.
        /// </summary>
        [Tooltip("Whether the machine runs. Always on this round; power zoning will drive this later.")]
        [SerializeField]
        private bool _isPowered = true;

        /// <summary>
        /// Reused by the intake scan. See <see cref="GrabbableSpawner.CollectSpawnedGrabbables"/>.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        /// <summary>
        /// 0 when nothing is being printed; otherwise the position in the pile that the sheet
        /// being printed will take. Replicated.
        /// </summary>
        /// <remarks>
        /// Written once when a job starts and then left alone until it finishes, even if the
        /// player takes sheets out while it runs. It is reset to 0 rather than advanced, and
        /// the animator reads that as "not N" rather than as "0" — see FinishCraft for why
        /// those are not the same thing here. The animation is two seconds of a sheet
        /// arriving and cannot be renumbered or restarted underneath itself, so a job that ends
        /// up landing lower in the pile than it announced still finishes as the state it began
        /// as, and the pile corrects itself afterwards.
        /// </remarks>
        private readonly SyncVar<int> _printingSlot = new(0);

        /// <summary>
        /// Payload index of the sheet being printed, or -1. Replicated.
        /// </summary>
        /// <remarks>
        /// What the machine shows coming out of itself, before the document has landed in the
        /// pile and can be read from there.
        /// </remarks>
        private readonly SyncVar<int> _printingPayload = new(-1);

        /// <summary>
        /// Sheets the cartridge in the ink slot still has in it. Replicated.
        /// </summary>
        /// <remarks>
        /// Derived from the slot rather than tracked beside it: zero while the slot is empty,
        /// refilled to <see cref="_printsPerCartridge"/> when a cartridge goes in, spent one
        /// sheet at a time, and the cartridge leaves the slot the moment it reaches zero. The
        /// slot and this number are never allowed to disagree about whether there is ink,
        /// because the only two places that change one change the other.
        ///
        /// Replicated because it is the only thing that can answer "how much ink is left". The
        /// cartridge is a single entry in a container of capacity one, so counting entries says
        /// nothing, and the number lives nowhere else a client could reach. Without this the
        /// machine's ink would be invisible to everyone including the player standing at it.
        /// </remarks>
        private readonly SyncVar<int> _printsRemaining = new(0);

        /// <summary>
        /// Server-only: true while a sheet is being made.
        /// </summary>
        private bool _crafting;

        /// <summary>
        /// Server-only: seconds of work banked on the current sheet.
        /// </summary>
        private float _craftSeconds;

        /// <summary>
        /// Whether the machine is running.
        /// </summary>
        public bool IsPowered => _isPowered;

        /// <summary>
        /// 0 when nothing is being printed; otherwise the pile position the sheet being printed
        /// will take.
        /// </summary>
        public int PrintingSlot => _printingSlot.Value;

        /// <summary>
        /// Payload index of the sheet being printed, or -1 when nothing is.
        /// </summary>
        public int PrintingPayload => _printingPayload.Value;

        /// <summary>
        /// Sheets the cartridge in the ink slot still has in it; zero when there is no cartridge.
        /// </summary>
        public int PrintsRemaining => _printsRemaining.Value;

        /// <summary>
        /// The container printed sheets stack in.
        /// </summary>
        /// <remarks>
        /// Exposed for the view that draws the pile. Handing out the machine's own reference
        /// rather than letting the view take a second one is what stops the two from ever
        /// pointing at different containers.
        /// </remarks>
        public ContainerBase Output => _output;

        /// <summary>
        /// Server: turns the machine on or off.
        /// </summary>
        /// <remarks>
        /// The seam a power system will drive. Nothing calls it yet and the machine is authored
        /// powered, so this round it is effectively a constant.
        /// </remarks>
        [Server]
        public void ServerSetPowered(bool powered) => _isPowered = powered;

        public override void OnStartServer()
        {
            base.OnStartServer();

            ValidateConfiguration();

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

            base.OnStopServer();
        }

        /// <summary>
        /// Reports wiring that would make the machine misbehave.
        /// </summary>
        /// <remarks>
        /// Slots rather than unlimited containers this round, and each has a size the machine
        /// is built around: paper is refused at six, a cartridge is the only thing that fits in
        /// the ink slot, and a cartridge with no sheets in it makes printing impossible. None of
        /// these stop the machine from running, so without a word here they would look like
        /// gameplay.
        /// </remarks>
        private void ValidateConfiguration()
        {
            if (_paper == null || _ink == null || _output == null)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} is missing one of its containers and will do nothing.",
                    this);
                return;
            }

            if (_paper == _ink || _paper == _output || _ink == _output)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} has the same container wired to more than one slot.",
                    this);
            }

            if (!_paper.IsUnlimited && _paper.Capacity != PaperCapacity)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has a paper capacity of {_paper.Capacity}; it is built around {PaperCapacity}.",
                    this);
            }

            if (!_ink.IsUnlimited && _ink.Capacity != InkCapacity)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has an ink capacity of {_ink.Capacity}; it is built around {InkCapacity}, one cartridge.",
                    this);
            }

            if (!_output.IsUnlimited && _output.Capacity != PileCapacity)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has an output capacity of {_output.Capacity}; the animation has {PileCapacity} pile slots.",
                    this);
            }

            if (_printsPerCartridge <= 0)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has a cartridge of {_printsPerCartridge} sheets, so no job can ever start.",
                    this);
            }
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
        /// Server: swallows the paper and cartridges lying in the intake box.
        /// </summary>
        private void UpdateIntake()
        {
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

                /* Anything still in someone's hands is left where it is. This is the whole of
                 * the rule that stops the machine taking a cartridge off a player who is merely
                 * walking past it, and it is why the box does not have to be small. */
                if (grabbable.State == GrabbableState.Held)
                    continue;

                TrySwallow(grabbable, grabbable.transform.position);
            }

            _scanBuffer.Clear();
        }

        /// <summary>
        /// Server: puts an item into its slot if it is lying in the intake box.
        /// </summary>
        /// <remarks>
        /// Dispatched by payload, and each slot is asked about its own room. Paper and ink are
        /// separate containers so that filling one cannot block the other: a machine holding six
        /// sheets and no ink must still take a cartridge, and that is the case this shape exists
        /// for. A printed sheet is neither, so feeding one back in leaves it lying on the
        /// machine to be picked up rather than eaten and printed again.
        /// </remarks>
        private void TrySwallow(NetworkGrabbable grabbable, Vector3 position)
        {
            if (!IsInsideBox(_intakeHalfExtents, _intakeCentre, position))
                return;

            int payload = grabbable.PayloadIndex;
            if (payload < 0)
                return;

            /* One index used for both would load paper into the ink slot and count it as a
             * cartridge. Refusing everything is the inert answer; OnValidate says so out loud. */
            if (_paperPayloadIndex == _inkPayloadIndex)
                return;

            ContainerBase slot;
            if (payload == _paperPayloadIndex)
                slot = _paper;
            else if (payload == _inkPayloadIndex)
                slot = _ink;
            else
                return;

            /* No room: the item stays in the world, where its owner can still pick it back up.
             * It is only swallowed when the machine can keep it, so nothing carried here is
             * destroyed for want of a slot — and a spare cartridge left lying on the machine
             * while the current one is still going is exactly what the player should see. */
            if (slot == null || slot.IsFull)
                return;

            /* OwnerClientId stays at -1 on purpose. Stock is public: paper and ink belong to
             * nobody, and the faction rotation that used to run here belongs to the file queue
             * instead, where each document will carry the faction that ordered it. */
            if (!slot.ServerTryAdd(ContainerEntry.ForEntity(payload)))
                return;

            /* A cartridge that has just gone in is a full one. This is the only place the ink
             * count rises; FinishCraft is the only place it falls. */
            if (slot == _ink)
                _printsRemaining.Value = _printsPerCartridge;

            /* Destroy rather than pool: these objects carry per-life state — the placed cell,
             * the settle timer, the payload — that a recycled instance would bring back with
             * it. Passed explicitly so this does not depend on the prefab. */
            grabbable.NetworkObject.Despawn(DespawnType.Destroy);
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

            TryBeginCraft();
        }

        /// <summary>
        /// True when a finished sheet would have nowhere to go.
        /// </summary>
        /// <remarks>
        /// The output is the machine's own buffer, so a full one stops the machine rather than
        /// spilling onto the floor, and the sheet already finished waits inside it until there
        /// is room. Nothing is lost while it waits; the machine simply stops taking paper.
        /// </remarks>
        private bool IsOutputBlocked() => _output == null || _output.IsFull;

        /// <summary>
        /// Server: takes one sheet of paper and one sheet's worth of ink, and starts work.
        /// </summary>
        /// <remarks>
        /// Both are checked before either is taken. A job that spent the paper and then found
        /// the cartridge empty would burn an item for nothing, which is the kind of loss a
        /// player cannot see coming.
        /// </remarks>
        private void TryBeginCraft()
        {
            if (_paper == null || _ink == null || _output == null)
                return;

            if (_paper.Count == 0 || _printsRemaining.Value <= 0 || IsOutputBlocked())
                return;

            if (!_paper.ServerTryRemoveFirst())
                return;

            /* Announced before the work starts rather than when it lands, because the animation
             * begins now and has to know which pile position and which document it is showing.
             * Both are then left alone for the rest of the job. */
            _printingSlot.Value = _output.Count + 1;
            _printingPayload.Value = _outputPayloadIndex;

            _crafting = true;
            _craftSeconds = 0f;
        }

        /// <summary>
        /// Server: adds the finished sheet to the output and charges it to the cartridge.
        /// </summary>
        /// <remarks>
        /// Data is not built yet, so a job is paper and ink and nothing else. When documents
        /// exist, this is where the third requirement goes, and where the file queue and the
        /// faction rotation plug in: the sheet produced here will belong to whoever ordered the
        /// document rather than to whoever carried the paper over. The rotation algorithm that
        /// used to sit in PrinterQueue is in this file's git history, waiting for that.
        /// </remarks>
        private void FinishCraft()
        {
            /* If the output filled up while this sheet was being made, the work is kept and the
             * sheet stays in the machine until there is room. Dropping it would destroy paper
             * and ink the player already paid for. */
            if (!_output.ServerTryAdd(ContainerEntry.ForEntity(_outputPayloadIndex)))
                return;

            _crafting = false;
            _craftSeconds = 0f;

            /* Dropped to 0 rather than straight to the next job's number. The reset can be
             * overwritten before anything observes it: the next job announces itself one frame
             * later, and a SyncVar is only sent when its tick comes round, so the two writes
             * routinely leave in the same packet — and a client's animator, which samples once
             * per rendered frame, then sees only the second one.
             *
             * That is why the transition out of Printing N tests Printing != N and not
             * Printing == 0. It says the same thing — this machine has stopped printing sheet
             * N — but it is true for both outcomes, 0 and N+1, so there is no value the client
             * has to have caught in time. Do not tidy it back to Equals 0: 0 is a value the
             * client often never receives, and the machine would sit in Printing N looping
             * forever. See the transition names in Printer.controller, which say so too. */
            _printingSlot.Value = 0;

            /* Charged on delivery rather than on completion, so the count and the slot only ever
             * move together. The cartridge leaves the slot the moment it is empty, which is what
             * frees it for the next one — and, with a capacity of one, what makes "a cartridge
             * cannot be swapped until it is used up" true without a rule of its own. */
            int remaining = _printsRemaining.Value - 1;
            _printsRemaining.Value = remaining;

            if (remaining > 0)
                return;

            _ink.ServerTryRemoveFirst();
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

        /// <summary>
        /// Clamps the authored values and says so when the recipe cannot work.
        /// </summary>
        /// <remarks>
        /// Overridden rather than hidden. NetworkBehaviour.OnValidate is virtual and calls
        /// TryAddNetworkObject, which is how the component finds its NetworkObject while the
        /// prefab is being built; a plain <c>void OnValidate</c> compiles with a warning and
        /// silently stops that from happening. Unlike Awake, the weaver does not rewrite this
        /// one — there is nothing to take over for it.
        /// </remarks>
        protected override void OnValidate()
        {
            base.OnValidate();

            _secondsPerOutput = Mathf.Max(0f, _secondsPerOutput);

            _intakeHalfExtents = new Vector3(
                Mathf.Max(0.01f, _intakeHalfExtents.x),
                Mathf.Max(0.01f, _intakeHalfExtents.y),
                Mathf.Max(0.01f, _intakeHalfExtents.z));

            _printsPerCartridge = Mathf.Max(0, _printsPerCartridge);

            if (_paperPayloadIndex >= 0 && _paperPayloadIndex == _inkPayloadIndex)
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} uses payload {_paperPayloadIndex} for both paper and ink; the machine will refuse both.",
                    this);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            /* The box that swallows. Anything matching an accepted payload resting inside it
             * ends up in a slot, so it is the one volume worth drawing. */
            Gizmos.color = new Color(0.3f, 0.85f, 0.5f, 0.9f);
            Gizmos.DrawWireCube(_intakeCentre, _intakeHalfExtents * 2f);

            Gizmos.matrix = previous;
        }
    }
}
