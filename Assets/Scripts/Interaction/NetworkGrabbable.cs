using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using Overworked.Containers;
using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// What a grabbable is currently doing.
    /// </summary>
    public enum GrabbableState : byte
    {
        /// <summary>Resting on a surface or on the ground; free for anyone to take.</summary>
        Idle = 0,

        /// <summary>In the hands of <see cref="NetworkGrabbable.HolderId"/>.</summary>
        Held = 1,

        /// <summary>Thrown and still in flight, or settled after a throw.</summary>
        Free = 2,
    }

    /// <summary>
    /// Makes this object grabbable, and replicates who is holding it.
    /// </summary>
    /// <remarks>
    /// This component holds no input and sends no RPCs. The player drives everything; this
    /// only owns the replicated state and reacts to it. That split matters because all
    /// interaction RPCs are declared on the player, which is always owned by its own client,
    /// whereas this object's ownership moves around.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkGrabbable : NetworkBehaviour
    {
        [Header("Payload")]

        /// <summary>
        /// Where payload prefabs are looked up.
        /// </summary>
        [Tooltip("Where payload prefabs are looked up. Must be the same catalogue the container views use.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        /// <summary>
        /// Parent that holds the payload. Falls back to this transform.
        /// </summary>
        [Tooltip("Parent that holds the payload. Falls back to this transform.")]
        [SerializeField]
        private Transform _payloadRoot;

        [Header("Geometry")]

        /// <summary>
        /// Bypass the measured bottom offset and use <see cref="_manualBottomOffsetY"/>.
        /// </summary>
        [Tooltip("Bypass the measured bottom offset and use the manual value below.")]
        [SerializeField]
        private bool _useManualBottomOffset;

        /// <summary>
        /// Distance from the root down to the lowest point of the colliders.
        /// </summary>
        [Tooltip("Distance from the root down to the lowest point of the colliders.")]
        [SerializeField]
        private float _manualBottomOffsetY;

        /// <summary>
        /// How far below the probe point a surface is still found.
        /// </summary>
        [Tooltip("How far below the probe point a surface is still found.")]
        [SerializeField]
        private float _captureProbeDistance = 3f;

        /// <summary>
        /// How far above the object to start the downward surface probe.
        /// </summary>
        [Tooltip("How far above the object to start the downward surface probe.")]
        [SerializeField]
        private float _captureProbeLift = 1f;

        /// <summary>
        /// How far above a surface an object may be and still be captured onto it.
        /// </summary>
        /// <remarks>
        /// Measured from the object's own bottom to the surface, not from its root. Anything
        /// higher than this is passing over and is left alone to keep flying.
        /// </remarks>
        [Tooltip("How far above a surface an object may be and still be captured onto it.")]
        [SerializeField]
        private float _captureHeight = 0.5f;

        /// <summary>
        /// How far below a surface an object may be and still be captured onto it.
        /// </summary>
        /// <remarks>
        /// Stops an object lying on the floor underneath a table from being yanked up onto
        /// the tabletop: a downward probe from above finds the table, and without a lower
        /// bound that would read as a valid capture.
        /// </remarks>
        [Tooltip("How far below a surface an object may be and still be captured onto it.")]
        [SerializeField]
        private float _captureDepth = 0.25f;

        /// <summary>
        /// Speed below which a loose object counts as having stopped moving.
        /// </summary>
        [Tooltip("Speed below which a loose object counts as having stopped moving.")]
        [SerializeField]
        private float _settleSpeed = 0.15f;

        /// <summary>
        /// How long an object must stay below the settle speed before it is released.
        /// </summary>
        [Tooltip("How long an object must stay below the settle speed before it is released.")]
        [SerializeField]
        private float _settleSeconds = 0.35f;

        /// <summary>
        /// Who is holding this, or -1. Replicated.
        /// </summary>
        private readonly SyncVar<int> _holderId = new(-1);

        /// <summary>
        /// Current state. Replicated.
        /// </summary>
        private readonly SyncVar<byte> _state = new();

        /// <summary>
        /// Which payload this object wears, or -1 for the prefab's own authored look.
        /// Replicated as an index so every peer resolves it through the same catalogue.
        /// </summary>
        private readonly SyncVar<int> _payloadIndex = new(-1);

        /// <summary>
        /// The document number this object carries, or -1. Replicated.
        /// </summary>
        /// <remarks>
        /// Part of the payload's appearance but deliberately not part of the payload. The number
        /// has no ceiling, so making it an index into the catalogue would mean a prefab per
        /// number and a catalogue that grows for the life of the game. A PayloadLabel on the
        /// payload prefab decides what to do with it; a payload with no number ignores it.
        /// </remarks>
        private readonly SyncVar<int> _variantNumber = new(-1);

        /// <summary>
        /// The team the document on this object belongs to, or -1. Replicated.
        /// </summary>
        /// <remarks>
        /// A value rather than a second payload index, for the same reason: the two teams'
        /// documents differ by a text colour, not by a model.
        /// </remarks>
        private readonly SyncVar<int> _variantTeam = new(-1);

        /// <summary>
        /// The payload instance currently attached, or null.
        /// </summary>
        /// <remarks>
        /// Held so a variant change can reach its labels without tearing the payload down and
        /// building it again — which would also re-measure the geometry and move the transform.
        /// </remarks>
        private GameObject _payload;

        /// <summary>
        /// Server-only: the cell this object was last placed on. Not replicated; clients
        /// re-derive placement from a raycast, and only the server needs to arbitrate races.
        /// </summary>
        private Vector2Int? _placedCell;

        /// <summary>
        /// Distance from the root down to the lowest point of the colliders, measured once.
        /// </summary>
        private float _measuredBottomOffsetY;

        /// <summary>
        /// Every collider on this object and its children, cached at Awake.
        /// </summary>
        private Collider[] _colliders;

        /// <summary>
        /// Server-only: position at the previous FixedUpdate, used to measure whether the
        /// object has stopped moving.
        /// </summary>
        private Vector3 _lastPosition;

        /// <summary>
        /// Server-only: true once <see cref="_lastPosition"/> holds a real sample.
        /// </summary>
        private bool _hasLastPosition;

        /// <summary>
        /// Server-only: how long the object has been moving slower than the settle speed.
        /// </summary>
        private float _settleTimer;

        /// <summary>
        /// Current state.
        /// </summary>
        public GrabbableState State => (GrabbableState)_state.Value;

        /// <summary>
        /// Who is holding this, or -1.
        /// </summary>
        public int HolderId => _holderId.Value;

        /// <summary>
        /// Which payload this object wears, or -1 when it is using the prefab's authored look.
        /// </summary>
        public int PayloadIndex => _payloadIndex.Value;

        /// <summary>
        /// The document number this object carries, or -1.
        /// </summary>
        public int VariantNumber => _variantNumber.Value;

        /// <summary>
        /// The team the document on this object belongs to, or -1.
        /// </summary>
        public int VariantTeam => _variantTeam.Value;

        /// <summary>
        /// Server-only: the cell this object was last placed on.
        /// </summary>
        public Vector2Int? PlacedCell => _placedCell;

        /// <summary>
        /// How far below the root the object's lowest point sits.
        /// </summary>
        /// <remarks>
        /// Placement adds this to the surface height so the object rests on the surface
        /// rather than sinking into it. It is measured rather than authored so new object
        /// prefabs need no code change.
        /// </remarks>
        public float BottomOffsetY => _useManualBottomOffset ? _manualBottomOffsetY : _measuredBottomOffsetY;

        /// <summary>
        /// World-space size of the object's colliders, measured at Awake.
        /// </summary>
        /// <remarks>
        /// Used to size the placement preview so it matches the object that will land there.
        /// </remarks>
        public Vector3 ColliderSize { get; private set; }

        /// <summary>
        /// The Rigidbody on this object.
        /// </summary>
        public Rigidbody Body { get; private set; }

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            _colliders = GetComponentsInChildren<Collider>(includeInactive: true);

            MeasureGeometry();
        }

        /// <summary>
        /// Re-caches the colliders and re-measures everything derived from them.
        /// </summary>
        /// <remarks>
        /// Awake measures once, which is only correct while the colliders never change. A
        /// payload swap replaces them, so the bottom offset, the collider size and the held
        /// collider set all have to be redone together — otherwise placement lands at the
        /// old height and the held object keeps colliding with things.
        /// </remarks>
        public void RefreshGeometry()
        {
            _colliders = GetComponentsInChildren<Collider>(includeInactive: true);

            MeasureGeometry();
            ApplyColliderState();
        }

        /// <summary>
        /// Server: chooses which payload this object wears.
        /// </summary>
        /// <remarks>
        /// Deliberately not marked [Server]. That attribute compiles down to a check on
        /// IsServerInitialized, which is false between GetPooledInstantiated and Spawn —
        /// exactly the window this has to be callable in. The payload has to travel in the
        /// spawn message, because applying it later means the swap happens to an object that
        /// is already moving, and re-measuring the geometry briefly moves the transform to
        /// the origin to measure it. That is harmless at spawn and visible mid-flight.
        ///
        /// Set this before spawning. Every peer applies it as it spawns, because
        /// SyncVar.OnChange does not fire for the initial value.
        /// </remarks>
        public void ServerSetPayload(int payloadIndex)
        {
            /* IsServerStarted rather than IsServerInitialized, and not the [Server] attribute:
             * that attribute compiles to an IsServerInitialized check, which is false for this
             * object until it has been spawned. IsServer is the socket state and is true, but it
             * is deprecated and simply forwards to this. */
            if (!FishNet.InstanceFinder.IsServerStarted)
            {
                Debug.LogWarning($"{nameof(ServerSetPayload)} was called on a peer that is not the server; ignored.", this);
                return;
            }

            _payloadIndex.Value = payloadIndex;
        }

        /// <summary>
        /// Server: sets the number and team drawn on this object, for payloads that show them.
        /// </summary>
        /// <remarks>
        /// Set before spawning, like the payload index and for the same reason: the values have
        /// to travel in the spawn message, because SyncVar.OnChange does not fire for an initial
        /// value. Payloads with no label ignore both.
        ///
        /// Not marked [Server] — see ServerSetPayload.
        /// </remarks>
        public void ServerSetVariant(int number, int team)
        {
            if (!FishNet.InstanceFinder.IsServerStarted)
            {
                Debug.LogWarning($"{nameof(ServerSetVariant)} was called on a peer that is not the server; ignored.", this);
                return;
            }

            _variantNumber.Value = number;
            _variantTeam.Value = team;
        }

        /// <summary>
        /// Attaches the payload named by the current index, on every peer.
        /// </summary>
        /// <remarks>
        /// Index -1 leaves the prefab exactly as authored, which is what objects spawn as
        /// until something asks for a particular look. Only a real index clears the authored
        /// children, so the default costs nothing and needs no prefab restructuring.
        ///
        /// This assumes a fresh instance. A pooled object would come back with its authored
        /// children already gone and, at index -1, would keep whichever payload it wore in
        /// its previous life — which is one of the reasons every despawn passes
        /// DespawnType.Destroy rather than relying on the prefab's default.
        /// </remarks>
        private void ApplyPayload()
        {
            int index = _payloadIndex.Value;
            if (index < 0)
                return;

            GameObject prefab = _catalogue != null ? _catalogue.Get(index) : null;
            if (prefab == null)
            {
                Debug.LogWarning(
                    $"{nameof(NetworkGrabbable)} on {gameObject.name} has no payload for index {index}" +
                    (_catalogue == null ? "; no catalogue is assigned." : "."),
                    this);
                return;
            }

            Transform root = _payloadRoot != null ? _payloadRoot : transform;

            /* Detach the old look before destroying it. Destroy is deferred to the end of the
             * frame, so anything left parented would still be found by the geometry sweep
             * below and would contribute its collider to the measurements. Deactivating as
             * well closes the window where a detached copy is still queryable. */
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                GameObject old = root.GetChild(i).gameObject;
                old.SetActive(false);
                old.transform.SetParent(null, worldPositionStays: false);
                Destroy(old);
            }

            _payload = Instantiate(prefab, root);
            _payload.transform.localPosition = Vector3.zero;
            _payload.transform.localRotation = Quaternion.identity;
            _payload.transform.localScale = Vector3.one;

            ApplyVariant(_payload);
            RefreshGeometry();
        }

        /// <summary>
        /// Writes the document number and team into every label on a payload.
        /// </summary>
        /// <remarks>
        /// Separate from building the payload so a number change does not rebuild it: the two
        /// are independent, and rebuilding would also re-measure the geometry, which moves the
        /// transform to the origin while it measures.
        ///
        /// A payload with no PayloadLabel — blank paper, an ink cartridge — has nothing to
        /// update, which is not an error.
        /// </remarks>
        private void ApplyVariant(GameObject payload)
        {
            if (payload == null)
                return;

            foreach (PayloadLabel label in payload.GetComponentsInChildren<PayloadLabel>(includeInactive: true))
                label.SetVariant(_variantNumber.Value, _variantTeam.Value);
        }

        /// <summary>
        /// Applies a variant change, once per change.
        /// </summary>
        /// <remarks>
        /// Same duplicate as every other SyncType: a host sees its own write and the echoed read.
        /// </remarks>
        private void OnVariantChanged(int prev, int next, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            ApplyVariant(_payload);
        }

        /// <summary>
        /// Applies a payload change, once per change.
        /// </summary>
        /// <remarks>
        /// Same duplicate as every other SyncType: a host sees its own write and the echoed
        /// read, and letting both through would tear the payload down and rebuild it twice.
        /// </remarks>
        private void OnPayloadChanged(int prev, int next, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            ApplyPayload();
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _state.OnChange += OnStateChanged;
            _payloadIndex.OnChange += OnPayloadChanged;
            _variantNumber.OnChange += OnVariantChanged;
            _variantTeam.OnChange += OnVariantChanged;

            /* SyncVar.OnChange does not fire for the initial value, so a client that joins
             * while the object is already held would otherwise never apply the state. The
             * payload index has exactly the same gap, and by this point a client has already
             * been handed its sync values. */
            ApplyColliderState();
            ApplyPayload();
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _state.OnChange -= OnStateChanged;
            _payloadIndex.OnChange -= OnPayloadChanged;
            _variantNumber.OnChange -= OnVariantChanged;
            _variantTeam.OnChange -= OnVariantChanged;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            MakePlayersPassThrough();
        }

        /// <summary>
        /// Makes every CharacterController in the scene pass through this object.
        /// </summary>
        /// <remarks>
        /// A CharacterController steps up onto anything shorter than its step offset, and a
        /// resting object is only 0.125 tall — so players could stack objects and walk over
        /// tables. This is not fixable by tuning the offset alone, so the player and the
        /// object are made to ignore each other outright. The push that used to come out of
        /// the collision is applied by PlayerInteraction instead.
        ///
        /// Called from both directions, here and from PlayerInteraction, because either can
        /// spawn first.
        /// </remarks>
        public void MakePlayersPassThrough()
        {
            CharacterController[] controllers = FindObjectsByType<CharacterController>(FindObjectsInactive.Exclude);

            foreach (CharacterController controller in controllers)
                IgnoreCollisionWith(controller);
        }

        /// <summary>
        /// Makes <paramref name="other"/> pass through every collider on this object.
        /// </summary>
        public void IgnoreCollisionWith(Collider other)
        {
            if (other == null)
                return;

            foreach (Collider candidate in _colliders)
            {
                if (candidate == null)
                    continue;

                Physics.IgnoreCollision(candidate, other, true);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* A fresh sample is needed per spawn; a pooled or respawned object must not be
             * measured against the position it held in a previous life. */
            _hasLastPosition = false;
            _settleTimer = 0f;

            NetworkManager.ServerManager.OnRemoteConnectionState += ServerManager_OnRemoteConnectionState;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            if (NetworkManager != null)
                NetworkManager.ServerManager.OnRemoteConnectionState -= ServerManager_OnRemoteConnectionState;
        }

        /// <summary>
        /// Measures the collider geometry relative to the root.
        /// </summary>
        /// <remarks>
        /// Measured at the origin with identity rotation so the results do not depend on the
        /// authored position, on the prefab's scale, or on whatever rotation a previous throw
        /// left behind. Placement always writes identity rotation, which is what keeps these
        /// constants valid.
        /// </remarks>
        private void MeasureGeometry()
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;

            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Physics.SyncTransforms();

            bool any = false;
            Bounds bounds = new(Vector3.zero, Vector3.zero);

            foreach (Collider candidate in _colliders)
            {
                if (candidate == null)
                    continue;

                if (!any)
                {
                    bounds = candidate.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(candidate.bounds);
                }
            }

            transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();

            if (!any)
            {
                _measuredBottomOffsetY = 0f;
                ColliderSize = Vector3.zero;
                return;
            }

            _measuredBottomOffsetY = -bounds.min.y;
            ColliderSize = bounds.size;
        }

        /// <summary>
        /// Applies the collider state for the current <see cref="State"/>.
        /// </summary>
        /// <remarks>
        /// Colliders are disabled while held on every peer, not just the holder's. A held
        /// object must not push the holder's CharacterController, must not be hit by the
        /// holder's own placement raycast, and must not shove tables around — and the same
        /// is true of a remote player's controller, which is still simulated locally under
        /// state forwarding.
        /// </remarks>
        private void ApplyColliderState()
        {
            bool held = State == GrabbableState.Held;

            foreach (Collider candidate in _colliders)
            {
                if (candidate == null)
                    continue;

                candidate.enabled = !held;
            }
        }

        private void OnStateChanged(byte prev, byte next, bool asServer) => ApplyColliderState();

        /// <summary>
        /// Captures the object onto the grid cell beneath it whenever it is close enough to a
        /// surface, on every peer that simulates it.
        /// </summary>
        /// <remarks>
        /// The test is height, not speed. An object within capture range of a table belongs on
        /// it whether it was set down or thrown there, and requiring it to come to rest first
        /// made both cases feel broken: a placed object took a visible moment to snap, and one
        /// thrown across a table at low height sailed on instead of landing.
        ///
        /// Only peers that simulate the object act. The server owns the state change; the
        /// owning client snaps its own copy at the same time so the thrower does not keep
        /// flying it and fight the correction coming back.
        /// </remarks>
        private void FixedUpdate()
        {
            if (!IsSpawned)
                return;
            if (!IsServerStarted && !IsOwner)
                return;

            Vector3 position = transform.position;

            float speed = 0f;
            if (_hasLastPosition)
                speed = (position - _lastPosition).magnitude / Time.fixedDeltaTime;

            _lastPosition = position;
            _hasLastPosition = true;

            /* Capture first: it is height-based and immediate, and takes precedence over the
             * resting transition below. */
            if (TryFindCapture(position, out Vector2Int cell, out Vector3 target))
            {
                bool alreadyThere = State == GrabbableState.Idle && PlacedCell.HasValue && PlacedCell.Value == cell;
                if (!alreadyThere)
                {
                    if (IsServerStarted)
                        ServerCapture(cell, target);
                    else
                        ApplyPredictedCapture(target);

                    return;
                }
            }

            if (IsServerStarted)
                UpdateResting(speed);
        }

        /// <summary>
        /// Server: releases an object that has come to rest away from any surface.
        /// </summary>
        /// <remarks>
        /// Separate from capture because it answers a different question. Capture is about
        /// where the object is; this is about the object being done moving. Without it, an
        /// object that lands on the floor keeps the Free state — and Free is not pickable, so
        /// it would sit there looking grabbable and refuse to be picked up. It would also stay
        /// owned by whoever threw it, which locks everyone else out.
        ///
        /// Speed has to come from the transform delta: while a client still owns this object
        /// the server's copy is kinematic, so its linearVelocity reads as zero and it would
        /// look permanently at rest.
        /// </remarks>
        private void UpdateResting(float speed)
        {
            if (State != GrabbableState.Free || speed > _settleSpeed)
            {
                _settleTimer = 0f;
                return;
            }

            _settleTimer += Time.fixedDeltaTime;
            if (_settleTimer < _settleSeconds)
                return;

            _settleTimer = 0f;

            RemoveOwnership();
            ServerSetResting();
        }

        /// <summary>
        /// Works out whether the object is close enough to a surface to be placed on it.
        /// </summary>
        private bool TryFindCapture(Vector3 position, out Vector2Int cell, out Vector3 target)
        {
            cell = default;
            target = default;

            if (State == GrabbableState.Held)
                return false;

            if (!SnapSurface.TryFind(position, _captureProbeLift, _captureProbeDistance, transform, out SnapSurface surface, out float surfaceY))
                return false;

            /* Height is measured from the object's own bottom to the surface. The lower bound
             * matters: an object lying on the floor under a table would otherwise be yanked
             * up onto the tabletop, because the probe from above finds the table. */
            float heightAboveSurface = (position.y - BottomOffsetY) - surfaceY;
            if (heightAboveSurface > _captureHeight || heightAboveSurface < -_captureDepth)
                return false;

            Vector2 positionXZ = new(position.x, position.z);
            cell = WorldGrid.CellCoord(positionXZ);
            Vector2 centreXZ = WorldGrid.CellCentreXZ(positionXZ);

            /* Re-probe at the cell centre. Rejecting here means the cell does not actually
             * cover a surface, so the object is left where it is rather than being shoved
             * onto a neighbouring cell. */
            float probeY = position.y + _captureProbeLift;
            if (!surface.TrySample(new Vector3(centreXZ.x, 0f, centreXZ.y), probeY, _captureProbeLift + _captureProbeDistance, transform, out float cellSurfaceY))
                return false;

            target = new Vector3(centreXZ.x, cellSurfaceY + BottomOffsetY, centreXZ.y);
            return true;
        }

        /// <summary>
        /// Server: snaps the object onto a cell and records it.
        /// </summary>
        private void ServerCapture(Vector2Int cell, Vector3 target)
        {
            NetworkManager manager = NetworkManager;
            if (manager == null)
                return;

            if (IsCellOccupied(manager, cell, this))
                return;

            /* Ownership returns to the server first: the server cannot hold a transform that a
             * client still owns and keeps overwriting. It also puts the object under server
             * simulation from here on, which is what a resting object wants. */
            RemoveOwnership();

            transform.SetPositionAndRotation(target, Quaternion.identity);

            if (Body != null)
            {
                Body.isKinematic = false;
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }

            ServerSetPlaced(cell);
        }

        /// <summary>
        /// Client: matches the snap the server is about to apply.
        /// </summary>
        /// <remarks>
        /// Without this the thrower's own copy keeps simulating, and the object visibly fights
        /// the server's correction for a round trip before settling. Frozen rather than dropped
        /// because ownership is about to leave this client — until it does, this copy must stop
        /// moving so it cannot overwrite the snap on its way out.
        /// </remarks>
        private void ApplyPredictedCapture(Vector3 target)
        {
            if (Body != null)
            {
                /* Zeroed before switching to kinematic, not after: Unity refuses to set a
                 * velocity on a kinematic body and logs a warning for each attempt, and both of
                 * the writes below would be refused. */
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                Body.isKinematic = true;
            }

            transform.SetPositionAndRotation(target, Quaternion.identity);
        }

        /// <summary>
        /// True when another grabbable is already registered to this cell.
        /// </summary>
        /// <remarks>
        /// Derived from the spawned objects themselves rather than a separate registry, so
        /// there is no second copy of the truth to leak or fall out of sync when an object
        /// despawns or its owner disconnects.
        /// </remarks>
        public static bool IsCellOccupied(NetworkManager manager, Vector2Int cell, NetworkGrabbable ignore)
        {
            if (manager == null)
                return false;

            foreach (NetworkObject spawned in manager.ServerManager.Objects.Spawned.Values)
            {
                if (spawned == null)
                    continue;

                NetworkGrabbable other = spawned.GetComponent<NetworkGrabbable>();
                if (other == null || other == ignore)
                    continue;
                if (other.PlacedCell.HasValue && other.PlacedCell.Value == cell)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Server: true when the given connection is holding anything.
        /// </summary>
        /// <remarks>
        /// Derived from the objects themselves for the same reason as <see cref="IsCellOccupied"/>:
        /// there is no per-player carry flag on the server, and a second copy of that answer would
        /// be one more thing to keep in step. Stations use this for "hands empty", which is a
        /// condition several of them share and none of them should be re-deriving.
        ///
        /// This only reads the collection, so unlike a despawn pass it needs no snapshot first.
        /// </remarks>
        public static bool IsHeldBy(NetworkManager manager, int clientId)
        {
            if (manager == null || clientId < 0)
                return false;

            foreach (NetworkObject spawned in manager.ServerManager.Objects.Spawned.Values)
            {
                if (spawned == null)
                    continue;

                NetworkGrabbable grabbable = spawned.GetComponent<NetworkGrabbable>();
                if (grabbable == null)
                    continue;
                if (grabbable.State == GrabbableState.Held && grabbable.HolderId == clientId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Server: marks this object as held by a connection.
        /// </summary>
        [Server]
        public void ServerSetHeld(int holderClientId)
        {
            _placedCell = null;
            _holderId.Value = holderClientId;
            _state.Value = (byte)GrabbableState.Held;
        }

        /// <summary>
        /// Server: marks this object as resting, but not on a grid cell.
        /// </summary>
        /// <remarks>
        /// Needed because a thrown object that settles on the floor never snaps to a cell,
        /// and leaving it in Free would keep it out of the pickup whitelist — it would look
        /// like a resting object but be unpickable.
        /// </remarks>
        [Server]
        public void ServerSetResting()
        {
            _placedCell = null;
            _holderId.Value = -1;
            _state.Value = (byte)GrabbableState.Idle;
        }

        /// <summary>
        /// Server: marks this object as thrown or otherwise loose.
        /// </summary>
        [Server]
        public void ServerSetFree()
        {
            _placedCell = null;
            _holderId.Value = -1;
            _state.Value = (byte)GrabbableState.Free;
        }

        /// <summary>
        /// Server: marks this object as resting on a grid cell.
        /// </summary>
        [Server]
        public void ServerSetPlaced(Vector2Int cell)
        {
            _placedCell = cell;
            _holderId.Value = -1;
            _state.Value = (byte)GrabbableState.Idle;
        }

        /// <summary>
        /// Releases an object whose owner disconnected.
        /// </summary>
        /// <remarks>
        /// Objects owned by a disconnecting client are normally despawned for everyone. This
        /// prefab opts out of that with _preventDespawnOnDisconnect, because a thrower keeps
        /// ownership indefinitely — so without this handler, a player leaving would delete
        /// every object they had ever thrown. Set the state back to free instead, and drop
        /// ownership so the server takes over simulation.
        /// </remarks>
        private void ServerManager_OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
                return;
            if (OwnerId != args.ConnectionId)
                return;

            ServerSetFree();
            RemoveOwnership();
        }
    }
}
