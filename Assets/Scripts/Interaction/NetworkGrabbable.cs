using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
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

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _state.OnChange += OnStateChanged;
            /* SyncVar.OnChange does not fire for the initial value, so a client that joins
             * while the object is already held would otherwise never apply the state. */
            ApplyColliderState();
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _state.OnChange -= OnStateChanged;
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
                Body.isKinematic = true;
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
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
