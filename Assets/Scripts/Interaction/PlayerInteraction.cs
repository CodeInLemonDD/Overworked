using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Utility.Template;
using Overworked.Stations;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overworked.Interaction
{
    /// <summary>
    /// Grab, carry, grid-place and throw. This is the interaction brain and it owns every
    /// interaction RPC in the project.
    /// </summary>
    /// <remarks>
    /// All RPCs live here rather than on the grabbable because a ServerRpc defaults to
    /// RequireOwnership, and this object is always owned by its own client — whereas a
    /// grabbable's ownership moves to whoever is holding it, and to nobody once placed.
    ///
    /// The carry transform is written here, on the owner, once per rendered frame, rather
    /// than on the tick: the player root is tick-stepped and jumps during rollbacks, so
    /// anything driven from it would visibly detach from the hands.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlayerInteraction : TickNetworkBehaviour
    {
        [Header("Input")]

        /// <summary>
        /// Assign Assets/InputSystem_Actions.inputactions.
        /// </summary>
        [Tooltip("Assign Assets/InputSystem_Actions.inputactions.")]
        [SerializeField]
        private InputActionAsset _inputActions;

        [Header("References")]

        /// <summary>
        /// Where a carried object is held, and the origin for pickup detection and throws.
        /// </summary>
        [Tooltip("Where a carried object is held. Must be a child of the smoothed graphical node.")]
        [SerializeField]
        private HoldPoint _holdPoint;

        [Header("Pickup")]

        /// <summary>
        /// How far from the hold point an object can be picked up.
        /// </summary>
        [Tooltip("How far from the hold point an object can be picked up.")]
        [SerializeField]
        private float _pickupRadius = 1.1f;

        /// <summary>
        /// Half-angle of the forward sector an object must be inside to be picked up.
        /// </summary>
        [Tooltip("Half-angle of the forward sector an object must be inside to be picked up.")]
        [Range(0f, 180f)]
        [SerializeField]
        private float _pickupHalfAngle = 60f;

        /// <summary>
        /// How far above or below the hold point an object may be and still be picked up.
        /// </summary>
        [Tooltip("How far above or below the hold point an object may be and still be picked up.")]
        [SerializeField]
        private float _pickupMaxHeightDelta = 1f;

        /// <summary>
        /// Slack added to every server-side range check.
        /// </summary>
        /// <remarks>
        /// The client aims from the smoothed graphical pose while the server holds the
        /// tick-stepped root pose, so the two genuinely disagree by a small amount while
        /// moving. This absorbs that without letting a client reach meaningfully further.
        /// </remarks>
        [Tooltip("Slack added to every server-side range check.")]
        [SerializeField]
        private float _serverRangeTolerance = 0.25f;

        [Header("Releasing")]

        /// <summary>
        /// How far from the player an object may be released and still be accepted.
        /// </summary>
        /// <remarks>
        /// The object is released at arm's length, so the point it leaves the hand from is
        /// not where the player is standing. This is generous on purpose; the check exists to
        /// stop a client releasing somewhere it could not plausibly reach, not to be tight.
        /// </remarks>
        [Tooltip("How far from the player an object may be released and still be accepted.")]
        [SerializeField]
        private float _placeReach = 0.8f;

        [Header("Throw")]

        /// <summary>
        /// Presses at or under this duration place; longer ones throw.
        /// </summary>
        [Tooltip("Presses at or under this duration place the object; longer ones throw it.")]
        [SerializeField]
        private float _tapMaxSeconds = 0.25f;

        /// <summary>
        /// Throw speed with no charge.
        /// </summary>
        [Tooltip("Throw speed with no charge.")]
        [SerializeField]
        private float _throwMinSpeed = 6f;

        /// <summary>
        /// Throw speed at full charge.
        /// </summary>
        [Tooltip("Throw speed at full charge.")]
        [SerializeField]
        private float _throwMaxSpeed = 14f;

        /// <summary>
        /// Hold duration that reaches full charge.
        /// </summary>
        [Tooltip("Hold duration that reaches full charge.")]
        [SerializeField]
        private float _throwFullChargeSeconds = 0.7f;

        /// <summary>
        /// How far above horizontal the object is launched, in degrees.
        /// </summary>
        [Tooltip("How far above horizontal the object is launched, in degrees.")]
        [Range(-45f, 60f)]
        [SerializeField]
        private float _throwAngleDegrees = 10f;

        /// <summary>
        /// Maximum random spin applied on release, in degrees per second.
        /// </summary>
        [Tooltip("Maximum random spin applied on release, in degrees per second.")]
        [SerializeField]
        private float _throwSpin = 6f;

        /// <summary>
        /// How long to wait for a pickup request to be answered before giving up.
        /// </summary>
        [Tooltip("How long to wait for a pickup request to be answered before giving up.")]
        [SerializeField]
        private float _requestTimeoutSeconds = 1f;

        [Header("Kicking")]

        /// <summary>
        /// How far from the player an object is still pushed, in world units.
        /// </summary>
        [Tooltip("How far from the player an object is still pushed, in world units.")]
        [SerializeField]
        private float _kickRadius = 0.7f;

        /// <summary>
        /// How far above or below the player an object can be and still be pushed.
        /// </summary>
        /// <remarks>
        /// Stops the player sweeping a table clean just by walking past it.
        /// </remarks>
        [Tooltip("How far above or below the player an object can be and still be pushed.")]
        [SerializeField]
        private float _kickVerticalRange = 0.45f;

        /// <summary>
        /// Force applied to an object the player walks into.
        /// </summary>
        [Tooltip("Force applied to an object the player walks into.")]
        [SerializeField]
        private float _kickForce = 12f;

        /// <summary>
        /// Fraction of the kick applied upward, so objects skip rather than slide.
        /// </summary>
        [Tooltip("Fraction of the kick applied upward, so objects skip rather than slide.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _kickLift = 0.15f;

        /// <summary>
        /// How far from the player a station can be and still be operated.
        /// </summary>
        /// <remarks>
        /// Longer than the pickup radius because a machine is a large object. A station's pivot
        /// sits at the base of the table it stands on, and the player stands at the edge, so a
        /// radius tuned for reaching an object on the floor would put most machines out of range.
        /// </remarks>
        [Tooltip("How far from the player a station can be and still be operated.")]
        [SerializeField]
        private float _stationReach = 2.5f;

        /// <summary>
        /// Half-angle of the forward sector a station must be inside to be operated.
        /// </summary>
        [Tooltip("Half-angle of the forward sector a station must be inside to be operated.")]
        [Range(0f, 180f)]
        [SerializeField]
        private float _stationHalfAngle = 75f;

        /// <summary>
        /// Presses at or over this duration are sent to a station as a long press.
        /// </summary>
        [Tooltip("Presses at or over this duration are sent to a station as a long press.")]
        [SerializeField]
        private float _stationLongPressSeconds = 0.3f;

        /// <summary>
        /// Per-object copy of the assigned action asset. See PlayerMovementPrediction for
        /// why the shared asset must not be used directly.
        /// </summary>
        private InputActionAsset _runtimeActions;

        /// <summary>
        /// Action map holding the interaction actions.
        /// </summary>
        private InputActionMap _playerActionMap;

        /// <summary>
        /// Interact action; Button, bound to E and the gamepad north button.
        /// </summary>
        private InputAction _interactAction;

        /// <summary>
        /// Reused buffer for the pickup sphere query.
        /// </summary>
        private readonly Collider[] _overlapBuffer = new Collider[16];

        /// <summary>
        /// The object currently held, or null.
        /// </summary>
        private NetworkObject _carried;

        /// <summary>
        /// The grabbable component of <see cref="_carried"/>.
        /// </summary>
        private NetworkGrabbable _carriedGrabbable;

        /// <summary>
        /// Whether a carry is in progress.
        /// </summary>
        /// <remarks>
        /// Deliberately a plain bool rather than a test on <see cref="_carried"/>. Unity makes
        /// a destroyed object compare equal to null, so an object destroyed while it is being
        /// held — which a round reset or a container does — reads as "not carrying" and skips
        /// the release entirely, stranding the throw arc on screen. This flag outlives the
        /// object it refers to.
        /// </remarks>
        private bool _isCarrying;

        /// <summary>
        /// Arc showing where a throw would go. Local only.
        /// </summary>
        private ThrowTrajectoryPreview _preview;

        /// <summary>
        /// An object a pickup has been requested for but not yet granted.
        /// </summary>
        private NetworkObject _requested;

        /// <summary>
        /// True while a pickup request is outstanding.
        /// </summary>
        private bool _requestPending;

        /// <summary>
        /// When the outstanding pickup request was sent.
        /// </summary>
        private float _requestSentTime;

        /// <summary>
        /// The station the current Interact press is aimed at, or null.
        /// </summary>
        private NetworkObject _stationTarget;

        /// <summary>
        /// When the current station press began.
        /// </summary>
        private float _stationPressTime;

        /// <summary>
        /// When the current Interact press began.
        /// </summary>
        private float _pressTime;

        /// <summary>
        /// True while an Interact press is being held on a carried object.
        /// </summary>
        private bool _charging;

        /// <summary>
        /// The transform a carried object is driven to.
        /// </summary>
        private Transform HoldTransform => _holdPoint != null ? _holdPoint.transform : transform;

        private void Awake()
        {
            /* LateUpdate only: everything here runs off input and the smoothed pose, not
             * off the tick. TimeManager.OnLateUpdate is the last LateUpdate of the frame,
             * after the camera has already read the same pose. */
            SetTickCallbacks(TickCallback.LateUpdate);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            if (_inputActions == null)
            {
                Debug.LogError($"{nameof(PlayerInteraction)} on {gameObject.name} has no InputActionAsset assigned; interaction will not respond to input.", this);
                return;
            }

            /* A shared asset would let one player's Disable() kill another player's input. */
            _runtimeActions = Instantiate(_inputActions);

            _playerActionMap = _runtimeActions.FindActionMap("Player", throwIfNotFound: true);
            _interactAction = _playerActionMap.FindAction("Interact", throwIfNotFound: true);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _playerActionMap?.Disable();

            if (_runtimeActions != null)
            {
                Destroy(_runtimeActions);
                _runtimeActions = null;
            }

            _playerActionMap = null;
            _interactAction = null;

            /* The preview is a runtime-created object, not a child of this one, so it has
             * to be released explicitly or it outlives the player. */
            if (_preview != null)
            {
                Destroy(_preview.gameObject);
                _preview = null;
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            SetInputEnabled(IsOwner);
            MakeGrabbablesPassThrough();
        }

        /// <summary>
        /// Makes this player's capsule pass through every grabbable already in the scene.
        /// </summary>
        /// <remarks>
        /// The reverse direction is handled by NetworkGrabbable.OnStartClient. Both are needed
        /// because either the players or the objects can be spawned first, and only the later
        /// one can see the earlier one.
        /// </remarks>
        private void MakeGrabbablesPassThrough()
        {
            CharacterController controller = GetComponent<CharacterController>();
            if (controller == null)
                return;

            NetworkGrabbable[] grabbables = FindObjectsByType<NetworkGrabbable>(FindObjectsInactive.Exclude);

            foreach (NetworkGrabbable grabbable in grabbables)
                grabbable.IgnoreCollisionWith(controller);
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);

            SetInputEnabled(IsOwner);
        }

        /// <summary>
        /// Enables or disables polling of the interaction actions.
        /// </summary>
        public void SetInputEnabled(bool isEnabled)
        {
            if (_playerActionMap == null)
                return;

            if (isEnabled)
                _playerActionMap.Enable();
            else
                _playerActionMap.Disable();
        }

        /// <summary>
        /// Pushes objects the player is standing in.
        /// </summary>
        /// <remarks>
        /// Distance-based rather than collision-based, because the player and the objects are
        /// made to ignore each other outright: a CharacterController steps up onto anything
        /// shorter than its step offset, so leaving collisions on lets players stack objects
        /// and walk over tables. With collisions off there is no collision callback to hang
        /// this on, so proximity is measured directly instead.
        ///
        /// FixedUpdate so the push is applied at a stable rate, not once per rendered frame.
        /// </remarks>
        private void FixedUpdate()
        {
            KickNearby();
        }

        /// <summary>
        /// Pushes any loose object close enough to be walked into.
        /// </summary>
        private void KickNearby()
        {
            if (!IsSpawned || NetworkManager == null)
                return;

            /* Mirrors FishNet's own idiom for reaching the spawned set from either role. */
            IReadOnlyDictionary<int, NetworkObject> spawned = NetworkManager.IsHostStarted
                ? NetworkManager.ServerManager.Objects.Spawned
                : NetworkManager.ClientManager.Objects.Spawned;

            Vector3 origin = transform.position;

            foreach (NetworkObject nob in spawned.Values)
            {
                if (nob == null)
                    continue;

                NetworkGrabbable grabbable = nob.GetComponent<NetworkGrabbable>();
                if (grabbable == null || grabbable == _carriedGrabbable)
                    continue;
                if (grabbable.State == GrabbableState.Held)
                    continue;

                /* Non-kinematic is exactly the peer that simulates this object; a kinematic
                 * copy would discard the push, so there is no point computing one. */
                Rigidbody body = grabbable.Body;
                if (body == null || body.isKinematic)
                    continue;

                Vector3 to = grabbable.transform.position - origin;

                if (Mathf.Abs(to.y) > _kickVerticalRange)
                    continue;

                to.y = 0f;
                float distance = to.magnitude;
                if (distance > _kickRadius)
                    continue;

                Vector3 direction = distance > 0.0001f ? to / distance : transform.forward;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f)
                    continue;

                direction.Normalize();

                body.AddForce(direction * _kickForce + Vector3.up * (_kickForce * _kickLift), ForceMode.Force);
            }
        }

        protected override void TimeManager_OnLateUpdate()
        {
            if (!IsOwner)
                return;

            /* Ownership is the single source of truth for whether an object is still
             * carried. It goes away when the server places the object, when someone else
             * takes it, and when the object despawns. The destroyed case is why the carry
             * flag is checked first: a destroyed object compares equal to null, so relying on
             * _carried alone would read "nothing is carried" and never release. */
            if (_isCarrying && (_carried == null || !_carried.IsOwner))
                ReleaseCarry();

            ResolvePendingRequest();

            /* Ahead of the branch below, so a press that began at a station is still finished
             * when a carry starts in between. The release edge is the only chance to send it, and
             * the carrying path never looks at it. */
            UpdateStationPress();

            if (_isCarrying)
                UpdateCarry();
            else
                UpdatePickupInput();
        }

        /// <summary>
        /// Handles the press that starts a pickup.
        /// </summary>
        private void UpdatePickupInput()
        {
            _charging = false;

            if (_interactAction == null || _requestPending)
                return;
            if (!_interactAction.WasPressedThisFrame())
                return;

            NetworkObject best = FindBestPickup();
            if (best != null)
            {
                _requested = best;
                _requestPending = true;
                _requestSentTime = Time.time;

                CmdRequestPickup(best);
                return;
            }

            /* Nothing to pick up, so the press belongs to whatever station is in front. Grabbing
             * keeps priority on purpose: a machine standing next to a table would otherwise make
             * everything on that table silently unpickable, and the player would have no way to
             * tell why. */
            _stationTarget = FindStationInFront();
            if (_stationTarget != null)
                _stationPressTime = Time.time;
        }

        /// <summary>
        /// Sends an outstanding station press when the key comes up.
        /// </summary>
        /// <remarks>
        /// Sent on release rather than on press, so the station learns how long the key was held.
        /// The target is cleared either way, because the release is the only chance to send it.
        /// </remarks>
        private void UpdateStationPress()
        {
            if (_stationTarget == null)
                return;
            if (_interactAction == null || !_interactAction.WasReleasedThisFrame())
                return;

            bool longPress = (Time.time - _stationPressTime) >= _stationLongPressSeconds;

            CmdInteractWith(_stationTarget, longPress);
            _stationTarget = null;
        }

        /// <summary>
        /// Finds the nearest station inside the forward sector.
        /// </summary>
        /// <remarks>
        /// Deliberately mirrors FindBestPickup: the player has one idea of "the thing in front of
        /// me", and two different rules for it would read as the game guessing. Distance is taken
        /// to the nearest point on the collider rather than to the station's pivot, because that
        /// pivot sits at the base of the table and a wide machine would otherwise be unreachable
        /// from the far end of its own frontage.
        /// </remarks>
        private NetworkObject FindStationInFront()
        {
            Vector3 origin = transform.position;

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();

            int count = Physics.OverlapSphereNonAlloc(origin, _stationReach, _overlapBuffer, ~0, QueryTriggerInteraction.Ignore);

            float minDot = Mathf.Cos(_stationHalfAngle * Mathf.Deg2Rad);
            NetworkObject best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                Collider candidate = _overlapBuffer[i];
                if (candidate == null)
                    continue;

                StationBase station = candidate.GetComponentInParent<StationBase>();
                if (station == null || !station.IsSpawned)
                    continue;

                Vector3 to = candidate.ClosestPoint(origin) - origin;
                to.y = 0f;

                float distance = to.magnitude;
                if (distance > _stationReach)
                    continue;
                if (distance > 0.0001f && Vector3.Dot(forward, to / distance) < minDot)
                    continue;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = station.NetworkObject;
            }

            return best;
        }

        /// <summary>
        /// Drives the carried object and handles the place/throw press.
        /// </summary>
        private void UpdateCarry()
        {
            NetworkGrabbable grabbable = _carriedGrabbable;
            if (grabbable == null)
            {
                ReleaseCarry();
                return;
            }

            /* ConfigureComponents leaves the owner's Rigidbody dynamic because an owner is
             * assumed to be a controller. This object is teleported to the hold point every
             * frame instead, and teleporting a dynamic body makes the solver read a huge
             * implied velocity. Kinematic is the correct mode for a transform-driven body. */
            Rigidbody body = grabbable.Body;
            if (body != null && !body.isKinematic)
            {
                /* Zeroed first: Unity will not set a velocity on a kinematic body, and logging a
                 * warning per frame for a body that is about to be made kinematic is noise. */
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }

            Transform hold = HoldTransform;

            /* Rotation is pinned to identity while held so the object stays upright and
             * axis-aligned — the same orientation it will have once snapped to a cell. It
             * only tumbles freely once it is loose again. */
            _carried.transform.SetPositionAndRotation(hold.position, Quaternion.identity);

            if (_interactAction == null)
                return;

            if (_interactAction.WasPressedThisFrame())
            {
                _pressTime = Time.time;
                _charging = true;
            }
            else if (_charging && _interactAction.WasReleasedThisFrame())
            {
                _charging = false;

                float heldSeconds = Time.time - _pressTime;
                if (heldSeconds <= _tapMaxSeconds)
                    Drop();
                else
                    Throw(heldSeconds);

                /* The object has left the hand; there is no arc to show. */
                return;
            }

            /* Called after the press handling so _charging is current for this frame. The arc
             * is a charge indicator, so it only appears while a press is actually held —
             * showing it the instant an object is picked up would just be noise. */
            UpdatePreview();
        }

        /// <summary>
        /// Draws the arc a throw would follow at the current charge.
        /// </summary>
        /// <remarks>
        /// Purely local and purely cosmetic, but it has to be built from the same velocity
        /// the throw will actually use — otherwise the arc would lie about where the object
        /// goes.
        /// </remarks>
        private void UpdatePreview()
        {
            if (!_charging)
            {
                if (_preview != null)
                    _preview.Hide();
                return;
            }

            if (_preview == null)
                _preview = ThrowTrajectoryPreview.Create();

            Transform hold = HoldTransform;

            /* The arc reflects the charge built so far, so it starts short and grows for as
             * long as the press is held. */
            float charge = Mathf.InverseLerp(_tapMaxSeconds, _throwFullChargeSeconds, Time.time - _pressTime);

            _preview.Show(hold.position, ComputeThrowVelocity(hold.forward, charge), transform);
        }

        /// <summary>
        /// Velocity a throw would launch the object with.
        /// </summary>
        /// <remarks>
        /// Shared by the throw itself and the arc preview so the two cannot drift apart.
        /// </remarks>
        private Vector3 ComputeThrowVelocity(Vector3 direction, float charge)
        {
            float speed = Mathf.Lerp(_throwMinSpeed, _throwMaxSpeed, charge);

            /* Flattened first so the launch angle is measured from horizontal rather than
             * from whatever pitch the smoothed hold point happens to carry. */
            Vector3 flat = direction;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f)
                flat = Vector3.forward;
            flat.Normalize();

            float radians = _throwAngleDegrees * Mathf.Deg2Rad;

            return flat * (Mathf.Cos(radians) * speed) + Vector3.up * (Mathf.Sin(radians) * speed);
        }

        /// <summary>
        /// Ends the carry locally. Does not change any replicated state.
        /// </summary>
        private void ReleaseCarry()
        {
            _carried = null;
            _carriedGrabbable = null;
            _isCarrying = false;
            _charging = false;

            if (_preview != null)
                _preview.Hide();
        }

        /// <summary>
        /// Clears an outstanding pickup request once it is answered or times out.
        /// </summary>
        private void ResolvePendingRequest()
        {
            if (!_requestPending)
                return;

            if (_requested == null || !_requested.IsSpawned)
            {
                _requested = null;
                _requestPending = false;
                return;
            }

            if (_requested.IsOwner)
            {
                NetworkGrabbable grabbable = _requested.GetComponent<NetworkGrabbable>();
                if (grabbable != null)
                {
                    _carried = _requested;
                    _carriedGrabbable = grabbable;
                    _isCarrying = true;
                }

                _requested = null;
                _requestPending = false;
                return;
            }

            if (Time.time - _requestSentTime > _requestTimeoutSeconds)
            {
                _requested = null;
                _requestPending = false;
            }
        }

        /// <summary>
        /// Picks the nearest grabbable inside the forward sector.
        /// </summary>
        /// <remarks>
        /// OverlapSphere returns an unordered set, so "nearest" has to be an explicit
        /// minimum. Filtering is by component rather than by layer because the project
        /// defines no custom layers.
        /// </remarks>
        private NetworkObject FindBestPickup()
        {
            /* Measured from the player root, not from the hold point. The hold point sits
             * 0.6m in front of the player, so measuring from it puts anything nearer than
             * that BEHIND the origin and the sector test rejects it — which made objects at
             * the player's feet unpickable. The server also checks against the root, so the
             * two sides now use identical geometry. */
            Vector3 origin = transform.position;

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();

            int count = Physics.OverlapSphereNonAlloc(origin, _pickupRadius, _overlapBuffer, ~0, QueryTriggerInteraction.Ignore);

            float minDot = Mathf.Cos(_pickupHalfAngle * Mathf.Deg2Rad);
            NetworkObject best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                Collider candidate = _overlapBuffer[i];
                if (candidate == null)
                    continue;

                NetworkGrabbable grabbable = candidate.GetComponentInParent<NetworkGrabbable>();
                if (grabbable == null || !grabbable.IsSpawned)
                    continue;
                if (grabbable.State != GrabbableState.Idle)
                    continue;

                Vector3 to = grabbable.transform.position - origin;
                if (Mathf.Abs(to.y) > _pickupMaxHeightDelta)
                    continue;

                to.y = 0f;
                float distance = to.magnitude;
                if (distance > _pickupRadius)
                    continue;
                if (distance > 0.0001f && Vector3.Dot(forward, to / distance) < minDot)
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = grabbable.NetworkObject;
                }
            }

            return best;
        }

        /// <summary>
        /// Releases the carried object where it stands.
        /// </summary>
        /// <remarks>
        /// Dropping needs no surface and no cell. The object simply becomes loose and physics
        /// takes over; if it comes to rest on a table, NetworkGrabbable's settle capture
        /// tidies it onto the nearest grid cell afterwards. That is the intended behaviour —
        /// you can put something down anywhere, and the grid is a consequence of where it
        /// landed rather than a requirement for putting it down.
        /// </remarks>
        private void Drop()
        {
            NetworkObject carried = _carried;
            NetworkGrabbable grabbable = _carriedGrabbable;
            if (carried == null || grabbable == null)
                return;

            Vector3 position = carried.transform.position;

            Rigidbody body = grabbable.Body;
            if (body != null)
            {
                body.isKinematic = false;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            ReleaseCarry();

            CmdDropObject(carried, position);
        }

        /// <summary>
        /// Applies the throw locally and tells the server the object is loose.
        /// </summary>
        /// <remarks>
        /// The velocity is applied only on the owner. Everyone else follows this object's
        /// client-authoritative NetworkTransform, so they do not need it.
        /// </remarks>
        private void Throw(float heldSeconds)
        {
            NetworkObject carried = _carried;
            NetworkGrabbable grabbable = _carriedGrabbable;
            if (carried == null || grabbable == null)
                return;

            float charge = Mathf.InverseLerp(_tapMaxSeconds, _throwFullChargeSeconds, heldSeconds);

            /* Aim with the smoothed forward, not the root's. The player aims at what they
             * can see, and the smoothed pose is what is drawn; the root lags behind by up to
             * ~47 degrees during a fast turn at the configured rotation rate. */
            Vector3 velocity = ComputeThrowVelocity(HoldTransform.forward, charge);

            Rigidbody body = grabbable.Body;
            if (body != null)
            {
                body.isKinematic = false;
                body.linearVelocity = velocity;
                body.angularVelocity = new Vector3(
                    Random.Range(-_throwSpin, _throwSpin),
                    Random.Range(-_throwSpin, _throwSpin),
                    Random.Range(-_throwSpin, _throwSpin));
            }

            ReleaseCarry();

            CmdNotifyThrown(carried);
        }

        /// <summary>
        /// Where something should be placed when it is handed straight to this player.
        /// </summary>
        /// <remarks>
        /// Read on the server, so it is the server's copy of the player. The owner's own copy runs
        /// a frame or so ahead, which does not matter for an object that is about to be driven to
        /// the hold point every frame anyway.
        /// </remarks>
        public Vector3 HandPosition => HoldTransform.position;

        /// <summary>
        /// Server: makes this player start carrying an object.
        /// </summary>
        /// <remarks>
        /// The object must already be spawned and owned by this player's connection. This is for
        /// anything generated straight into someone's hands — a box handing out a sheet, a printer
        /// handing over a finished document — and it is deliberately not the pickup path: there is
        /// nothing on the ground to request, so the client cannot start the carry itself.
        ///
        /// Marking the object held is part of this, not a step for the caller to remember. A
        /// freshly spawned object is Idle with no holder, and every server-side rule reads that
        /// copy rather than the client's: "hands empty" would never become false and a second
        /// request would be served, the carried object would keep its colliders and be captured
        /// onto grid cells as it was walked around, and both the drop and the throw check for Held
        /// and would refuse — leaving it stuck in the player's hands for good.
        /// </remarks>
        [Server]
        public void ServerHandToPlayer(NetworkObject target)
        {
            if (target == null || !target.IsSpawned)
                return;
            if (!Owner.IsValid)
                return;

            NetworkGrabbable grabbable = target.GetComponent<NetworkGrabbable>();
            if (grabbable != null)
                grabbable.ServerSetHeld(Owner.ClientId);

            RpcHandToPlayer(Owner, target);
        }

        /// <summary>
        /// Client: starts carrying an object the server put in this player's hands.
        /// </summary>
        [TargetRpc]
        private void RpcHandToPlayer(NetworkConnection conn, NetworkObject target)
        {
            if (target == null)
                return;

            NetworkGrabbable grabbable = target.GetComponent<NetworkGrabbable>();
            if (grabbable == null)
                return;

            /* Any pickup still in flight is abandoned. The hands are occupied now, and leaving the
             * request open would let it land later and overwrite this. */
            _requested = null;
            _requestPending = false;

            _carried = target;
            _carriedGrabbable = grabbable;
            _isCarrying = true;
            _charging = false;
        }

        /// <summary>
        /// Server: validates a station interaction and lets the station carry it out.
        /// </summary>
        /// <remarks>
        /// Declared here rather than on the station because a ServerRpc defaults to requiring
        /// ownership: this object is always owned by its own client, while a station is owned by
        /// nobody and would reject the call outright. Everything past the range check belongs to
        /// the station, including whether the player's hands are in the right state for it.
        /// </remarks>
        [ServerRpc]
        private void CmdInteractWith(NetworkObject stationObject, bool longPress, NetworkConnection caller = null)
        {
            if (stationObject == null || caller == null || !caller.IsActive)
                return;
            if (!stationObject.IsSpawned)
                return;

            StationBase station = stationObject.GetComponentInChildren<StationBase>();
            if (station == null)
                return;

            /* Range is checked against the server's own player transform, never against anything
             * the client reported. */
            Vector3 origin = transform.position;
            Vector3 to = station.transform.position - origin;
            to.y = 0f;

            float reach = station.InteractReach + _serverRangeTolerance;
            if (to.sqrMagnitude > reach * reach)
                return;

            station.ServerInteract(this, caller, longPress);
        }

        /// <summary>
        /// Server: validates a pickup request and hands over ownership.
        /// </summary>
        [ServerRpc]
        private void CmdRequestPickup(NetworkObject target, NetworkConnection caller = null)
        {
            if (target == null || caller == null || !caller.IsActive)
                return;
            if (!target.IsSpawned)
                return;
            /* Someone else already holds, threw, or claimed it. */
            if (target.Owner.IsValid && target.Owner != caller)
                return;

            NetworkGrabbable grabbable = target.GetComponent<NetworkGrabbable>();
            if (grabbable == null || grabbable.State != GrabbableState.Idle)
                return;

            /* Range is checked against the server's own player transform, never against
             * anything the client reported. */
            Vector3 origin = transform.position;
            origin.y = 0f;

            Vector3 to = target.transform.position - origin;
            to.y = 0f;

            float reach = _pickupRadius + _serverRangeTolerance;
            if (to.sqrMagnitude > reach * reach)
                return;

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f && to.sqrMagnitude > 0.0001f)
            {
                float minDot = Mathf.Cos((_pickupHalfAngle + 15f) * Mathf.Deg2Rad);
                if (Vector3.Dot(forward.normalized, to.normalized) < minDot)
                    return;
            }

            grabbable.ServerSetHeld(caller.ClientId);
            target.GiveOwnership(caller);
        }

        /// <summary>
        /// Server: records that a carried object was put down.
        /// </summary>
        /// <remarks>
        /// No cell is involved: dropping is allowed anywhere. Whether the object ends up on
        /// the grid is decided later, from where it actually comes to rest, by
        /// NetworkGrabbable's settle capture.
        ///
        /// Ownership stays with the dropper, matching a throw. The capture takes it back once
        /// the object settles, which is also when the server takes over its simulation.
        /// </remarks>
        [ServerRpc]
        private void CmdDropObject(NetworkObject target, Vector3 position, NetworkConnection caller = null)
        {
            if (target == null || caller == null || !caller.IsActive)
                return;
            if (target.Owner != caller)
                return;

            NetworkGrabbable grabbable = target.GetComponent<NetworkGrabbable>();
            if (grabbable == null || grabbable.State != GrabbableState.Held)
                return;

            /* Reach check against the server's own player transform, never against anything
             * the client reported. Generous, because the object is released at arm's length. */
            Vector3 flat = transform.position;
            flat.y = 0f;

            Vector3 toDrop = position - flat;
            toDrop.y = 0f;

            float reach = _pickupRadius + _placeReach + _serverRangeTolerance;
            if (toDrop.sqrMagnitude > reach * reach)
                return;

            grabbable.ServerSetFree();
        }

        /// <summary>
        /// Server: records that a thrown object is loose again.
        /// </summary>
        /// <remarks>
        /// Ownership is deliberately left alone: the thrower keeps it and keeps simulating,
        /// which is what gives them immediate feedback on the throw.
        /// </remarks>
        [ServerRpc]
        private void CmdNotifyThrown(NetworkObject target, NetworkConnection caller = null)
        {
            if (target == null || caller == null || !caller.IsActive)
                return;
            if (target.Owner != caller)
                return;

            NetworkGrabbable grabbable = target.GetComponent<NetworkGrabbable>();
            if (grabbable == null || grabbable.State != GrabbableState.Held)
                return;

            grabbable.ServerSetFree();
        }

    }
}
