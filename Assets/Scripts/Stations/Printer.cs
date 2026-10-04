using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Overworked.Containers;
using Overworked.Documents;
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
    /// next job uses it, so there is no material queue to keep in order and nobody to rotate.
    /// What the machine does queue is documents: the computer puts one at the back, the machine
    /// takes one off the front, first in first out.
    ///
    /// That queue is deliberately not rationed between players, and it is worth saying why. An
    /// earlier design rotated the *materials* by owner so that stuffing your own paper in could
    /// not starve anyone. Its premise was wrong — materials are shared and carry no owner to
    /// rotate — and its replacement rations nothing at all. A player who fills the queue does
    /// block the machine until their own jobs are done, which is a way of taking the machine
    /// rather than a way of breaking it: it drains by itself, and it costs them exactly what it
    /// costs everyone else. Accepted for now, and deliberately not overlooked. Do not bring the
    /// old rotation back; it is in this file's history if you want to read why it went.
    ///
    /// Ink is spent by the sheet rather than consumed as an item. A cartridge is a count living
    /// in the ink slot until it runs dry, and it is the slot being full that stops a second one
    /// going in — "a cartridge cannot be swapped before it is used up" needs no rule of its own.
    ///
    /// Making a sheet is not replicated. The progress is a server-side timer; what other peers
    /// hold is the announcement of what is being printed — which pile slot, which document — and
    /// the output container itself, which is a SyncList like any other.
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
        /// Roughly how many documents the job queue is expected to hold.
        /// </summary>
        /// <remarks>
        /// A suggestion rather than a rule, unlike the paper and ink sizes: a queue of three or
        /// of eight is a judgement about pacing, not a number the machine or the animation is
        /// built around. It is only used to say so when the queue is unbounded.
        /// </remarks>
        private const int SuggestedQueueCapacity = 5;

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

        /// <summary>
        /// The job queue: documents waiting to be printed, oldest at the front.
        /// </summary>
        /// <remarks>
        /// A plain container holding Data entries, not a queue type of its own. A queue is a
        /// container read from one end, and the computer, the console and this machine all
        /// already speak ContainerBase — inventing a fourth kind of list would mean a fourth
        /// serialisation path to get right and a fourth place for the entry format to drift.
        ///
        /// Consumed from the front, so it is first in, first out. The order is the only fairness
        /// there is, and it is enough: nothing here needs to know who asked for a document,
        /// because the document itself does not carry a player either. Who ordered it becomes a
        /// question worth asking when requests exist, and the answer will live on the request.
        ///
        /// A capacity of about four to six is what this is built around. It is a small number on
        /// purpose: the queue is a buffer between two machines, not a warehouse, and a long one
        /// would let one player park a great deal of work in a machine they are not standing at.
        /// </remarks>
        [Tooltip("The job queue: documents waiting to be printed. Capacity 4 to 6 is what this is built around.")]
        [SerializeField]
        private ContainerBase _queue;

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
        /// Id of the document being printed, or -1. Replicated.
        /// </summary>
        /// <remarks>
        /// The id rather than the payload, because the payload cannot tell Excel 1 from Excel 2:
        /// they are the same template with a different number drawn on it, and the number is the
        /// whole of what the machine is showing. A peer resolves this through
        /// <see cref="Documents.DocumentStore"/> and gets the appearance, the number and the
        /// team together, from the one place that holds them.
        ///
        /// Never reset, and never written on its own. What means "nothing is being printed" is
        /// <see cref="_printingSlot"/> being zero, and a reader gates on that before it looks
        /// this up, so an id left over from the last job is never read as a current one. Clearing
        /// it as well would be a write of a value nothing needs, and the pair is only
        /// trustworthy because it is written together — two fields written separately are two
        /// fields that can be observed out of step.
        /// </remarks>
        private readonly SyncVar<int> _printingDocument = new(-1);

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
        /// Id of the document being printed, or -1 when nothing is. Resolve it through
        /// <see cref="Documents.DocumentStore"/> for anything beyond "is a job running".
        /// </summary>
        public int PrintingDocument => _printingDocument.Value;

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
        /// The job queue: documents waiting to be printed, oldest at the front.
        /// </summary>
        /// <remarks>
        /// Exposed for the computer, which puts a document in it from across the room. Handing out
        /// the machine's own container rather than letting the caller keep a second reference is
        /// the same arrangement as <see cref="Output"/>, and it is what keeps the capacity check
        /// the container already does from being written a second time at the call site.
        ///
        /// Nothing else about a request goes through here: whether the document exists, whether the
        /// player is still standing at the computer and whether there is room are all settled
        /// before anything is added.
        ///
        /// What goes in is a <c>ContainerEntry.ForData</c> and nothing else, and that belongs on
        /// the open surface rather than only in the implementation, because three separate callers
        /// can now reach this container and none of them reads the machine's internals. Anything
        /// else is dropped with a complaint when it reaches the front — a queue holding something
        /// the machine cannot print is a machine stalled for the rest of the round with nothing on
        /// screen to say why. The check lives in the machine rather than here, since a container
        /// cannot refuse what it does not recognise.
        /// </remarks>
        public ContainerBase Queue => _queue;

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
            /* Named one by one rather than as "one of its containers". This fires when a prefab
             * predates a slot being added -- which is exactly when whoever sees it is staring at
             * an Inspector trying to work out which box is empty. A message that will not say
             * which one sends them to the source instead, and the source is four hundred lines
             * away from the field. */
            string missing = MissingContainers();
            if (missing != null)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} has nothing assigned to {missing}, so it will do nothing. Assign it on the prefab.",
                    this);
                return;
            }

            if (_paper == _ink || _paper == _output || _paper == _queue
                || _ink == _output || _ink == _queue || _output == _queue)
            {
                Debug.LogError(
                    $"{nameof(Printer)} on {gameObject.name} has the same container wired to more than one slot.",
                    this);
            }

            if (_queue.IsUnlimited)
            {
                /* The queue is the one container here that is meant to fill up. Its capacity is
                 * the only bound on how much work one player can park in a machine they are not
                 * standing at, and an unbounded queue turns "the machine is busy for a while"
                 * into "the machine is gone for the round". */
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} has an unlimited job queue; it is built around a capacity of about {SuggestedQueueCapacity}.",
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
        /// The names of the container fields with nothing assigned, or null when all four are set.
        /// </summary>
        /// <remarks>
        /// Only reached from the failure path at startup, so the list is built fresh rather than
        /// kept in a buffer: a field held for a call that happens once per machine is state to
        /// read past for no gain.
        /// </remarks>
        private string MissingContainers()
        {
            List<string> missing = new();

            if (_paper == null)
                missing.Add(nameof(_paper));

            if (_ink == null)
                missing.Add(nameof(_ink));

            if (_output == null)
                missing.Add(nameof(_output));

            if (_queue == null)
                missing.Add(nameof(_queue));

            return missing.Count == 0 ? null : string.Join(", ", missing);
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
            /* Snapshot first, and only the loose ones that are in the box. Despawning while
             * enumerating ServerManager.Objects.Spawned throws, because that collection is a live
             * view over a Dictionary and Despawn removes the key synchronously — and both the
             * checks that decide what counts as loose and the box itself are shared with every
             * other intake in the game, so they live in IntakeVolume rather than here. */
            IntakeVolume.CollectInside(NetworkManager, transform, _intakeCentre, _intakeHalfExtents, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
                TrySwallow(_scanBuffer[i]);

            _scanBuffer.Clear();
        }

        /// <summary>
        /// Server: puts an item into its slot.
        /// </summary>
        /// <remarks>
        /// "In the box" is already settled — <see cref="IntakeVolume.CollectInside"/> only hands
        /// over things that are. What is left is this machine's own question, and it is asked
        /// twice over: which slot, and is there room.
        ///
        /// Dispatched by payload, and each slot is asked about its own room. Paper and ink are
        /// separate containers so that filling one cannot block the other: a machine holding six
        /// sheets and no ink must still take a cartridge, and that is the case this shape exists
        /// for. A printed sheet is neither, so feeding one back in leaves it lying on the
        /// machine to be picked up rather than eaten and printed again.
        /// </remarks>
        private void TrySwallow(NetworkGrabbable grabbable)
        {
            if (grabbable == null)
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

            /* Stock is a shared pool: paper and ink belong to nobody, and who fed them in
             * changes nothing. Which side asked for a print belongs to the document in the job
             * queue, not to the material. */
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
        /// Server: takes a document, a sheet of paper and a sheet's worth of ink, and starts work.
        /// </summary>
        /// <remarks>
        /// All three are checked before any of them is spent. The order they are spent in matters
        /// more than it looks: the document is the only one of the three that cannot be replaced
        /// by walking to the paper box, so it is read without being taken, then the paper is
        /// taken, and only then is the document removed. Nothing between those steps can fail on
        /// a server — the checks above hold until this method returns — but if something ever
        /// does, this order loses a sheet of paper rather than a document.
        /// </remarks>
        private void TryBeginCraft()
        {
            if (_paper == null || _ink == null || _output == null || _queue == null)
                return;

            if (_paper.Count == 0 || _printsRemaining.Value <= 0 || _queue.Count == 0 || IsOutputBlocked())
                return;

            if (!TryReadJob(out int documentId))
                return;

            if (!_paper.ServerTryRemoveFirst())
                return;

            if (!_queue.ServerTryRemoveFirst())
                return;

            /* Announced before the work starts rather than when it lands, because the animation
             * begins now and has to know which pile position and which document it is showing.
             * Both are written together, once, and then left alone for the rest of the job. */
            _printingSlot.Value = _output.Count + 1;
            _printingDocument.Value = documentId;

            _crafting = true;
            _craftSeconds = 0f;
        }

        /// <summary>
        /// Server: reads the document at the front of the queue without taking it.
        /// </summary>
        /// <remarks>
        /// The queue is a plain container, so nothing stops a caller from putting something in it
        /// that is not a document — an entity entry, or an id the store has never heard of.
        /// Neither can happen from the computer or the console, and both would leave the machine
        /// stalled behind a job it can never start, silently and for the rest of the round. That
        /// is a bad enough failure to be worth the few lines it takes to turn it into a skip: the
        /// entry is dropped with a complaint and the next one is tried on the following frame.
        ///
        /// Reads rather than takes, so that a queue with nothing usable in it costs nothing. The
        /// caller spends the paper first and takes the document only once it is certain.
        /// </remarks>
        /// <returns>False when there is nothing at the front that can be printed.</returns>
        private bool TryReadJob(out int documentId)
        {
            documentId = -1;

            if (_queue == null || !_queue.TryGetEntry(0, out ContainerEntry entry))
                return false;

            /* Nothing can be read before the store is up, and waiting for it costs a frame while
             * treating it as a bad entry would throw the document away. The two are deliberately
             * not folded together. */
            DocumentStore store = DocumentStore.Instance;
            if (store == null)
                return false;

            ContainerEntryKind kind = (ContainerEntryKind)entry.Kind;
            if (kind == ContainerEntryKind.Data && store.TryGet(entry.DataId, out DocumentRecord _))
            {
                documentId = entry.DataId;
                return true;
            }

            Debug.LogWarning(
                $"{nameof(Printer)} on {gameObject.name} dropped a queue entry that is not a printable document (kind {kind}, id {entry.DataId}).",
                this);

            _queue.ServerTryRemoveFirst();
            return false;
        }

        /// <summary>
        /// Server: adds the finished sheet to the output and charges it to the cartridge.
        /// </summary>
        /// <remarks>
        /// The sheet goes in as a Data entry pointing at the document, never as an entity
        /// carrying the document's number. A document has one representation and this is it;
        /// an entity with a number bolted on would be a second spelling of the same thing, and
        /// the next module to read it would have to guess which spelling was authoritative.
        /// What the entity is — which payload, which number, which team — is the store's to
        /// answer, and the sheet only has to say which document it is.
        /// </remarks>
        private void FinishCraft()
        {
            /* If the output filled up while this sheet was being made, the work is kept and the
             * sheet stays in the machine until there is room. Dropping it would destroy paper
             * and ink the player already paid for. */
            if (!_output.ServerTryAdd(ContainerEntry.ForData(_printingDocument.Value)))
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
            if (entry.Kind != (byte)ContainerEntryKind.Data)
                return;

            /* Resolved before anything is spawned. The sheet is handed over wearing the
             * document's appearance, number and team, all of which come from the store, and a
             * sheet that cannot say which document it is would lose its number the moment it
             * went into a folder. Refusing leaves it in the machine, where it can be taken once
             * the store is reachable. */
            DocumentStore store = DocumentStore.Instance;
            if (store == null
                || !store.TryGet(entry.DataId, out DocumentRecord document)
                || !store.TryGetSpec(entry.DataId, out DocumentCatalogue.Spec spec))
            {
                Debug.LogWarning(
                    $"{nameof(Printer)} on {gameObject.name} could not resolve document {entry.DataId}; the sheet was left in the machine.",
                    this);
                return;
            }

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
                spec.PayloadIndex,
                player.HandPosition,
                Quaternion.identity,
                conn,
                document.Number,
                document.Team,
                entry.DataId);

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
