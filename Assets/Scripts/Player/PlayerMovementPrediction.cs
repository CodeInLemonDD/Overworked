using FishNet.Connection;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using Overworked.Match;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overworked.Player
{
    /// <summary>
    /// Host-authoritative player movement with client-side prediction and rollback.
    /// </summary>
    /// <remarks>
    /// Authority is host-side: the owner samples input and sends it up, the server
    /// simulates, and the resulting state is sent back for the client to reconcile
    /// against. The owner predicts locally so its own movement feels immediate.
    ///
    /// This works with FishNet's state forwarding, which requires that this component
    /// be paired with a CharacterController on the same object and that no
    /// NetworkTransform is present. See the prefab setup notes.
    /// </remarks>
    public class PlayerMovementPrediction : TickNetworkBehaviour
    {
        #region Types.
        /// <summary>
        /// Input for a single tick.
        /// </summary>
        public struct ReplicateData : IReplicateData
        {
            public ReplicateData(Vector2 input, float speedMultiplier)
            {
                Input = input;
                SpeedMultiplier = speedMultiplier;
                _tick = 0;
            }

            /// <summary>
            /// Movement direction held this tick, in the XZ plane.
            /// </summary>
            public Vector2 Input;

            /// <summary>
            /// Fraction of the normal move rate to apply this tick; 0.3 to 1.
            /// </summary>
            /// <remarks>
            /// Carried here rather than read from PlayerStamina inside the replicate method.
            /// A replicate is re-run verbatim when the owner is rolled back, so anything it
            /// reads live would be the current frame's value applied to a tick from the past:
            /// the server and the owner would then walk different distances for that tick and
            /// the character would visibly snap back and forth. Travelling with the input
            /// keeps the value attached to the tick it belongs to.
            ///
            /// Must be public — the weaver only serializes public fields, and a private one
            /// would compile and then silently never leave this machine. The same reason
            /// rules out a property.
            /// </remarks>
            public float SpeedMultiplier;

            /// <summary>
            /// Tick is set at runtime. There is no need to manually assign this value.
            /// </summary>
            private uint _tick;

            /// <summary>
            /// Value types only, so there is nothing to release.
            /// </summary>
            public void Dispose() { }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        /// <summary>
        /// Authoritative state, used to roll the owner back after a server correction.
        /// </summary>
        public struct ReconcileData : IReconcileData
        {
            public ReconcileData(Vector3 localPosition, float yaw, float verticalVelocity)
            {
                LocalPosition = localPosition;
                Yaw = yaw;
                VerticalVelocity = verticalVelocity;

                _tick = 0;
            }

            /// <summary>
            /// Position of the character.
            /// </summary>
            public Vector3 LocalPosition;

            /// <summary>
            /// Facing in degrees around the Y axis.
            /// </summary>
            public float Yaw;

            /// <summary>
            /// Current vertical velocity.
            /// </summary>
            /// <remarks>
            /// Required. Without it a replay would re-apply gravity from a stale
            /// velocity and the character's height would drift on every reconcile.
            /// </remarks>
            public float VerticalVelocity;

            /// <summary>
            /// Tick is set at runtime. There is no need to manually assign this value.
            /// </summary>
            private uint _tick;

            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }
        #endregion

        [Header("Input")]

        /// <summary>
        /// Assign Assets/InputSystem_Actions.inputactions.
        /// </summary>
        [Tooltip("Assign Assets/InputSystem_Actions.inputactions.")]
        [SerializeField]
        private InputActionAsset _inputActions;

        [Header("Movement")]

        /// <summary>
        /// How quickly to move, in units per second.
        /// </summary>
        [Tooltip("How quickly to move, in units per second.")]
        [SerializeField]
        private float _moveRate = 4f;

        /// <summary>
        /// How quickly to turn towards the movement direction, in degrees per second.
        /// </summary>
        [Tooltip("How quickly to turn towards the movement direction, in degrees per second.")]
        [SerializeField]
        private float _rotationRate = 720f;

        /// <summary>
        /// Downward speed is clamped here so a long fall cannot outrun collision.
        /// </summary>
        [Tooltip("Terminal downward speed.")]
        [SerializeField]
        private float _terminalVelocity = -40f;

        /// <summary>
        /// Per-object copy of the assigned action asset.
        /// </summary>
        /// <remarks>
        /// The asset itself is shared, so this object must not enable or disable
        /// maps through it. See OnStartNetwork.
        /// </remarks>
        private InputActionAsset _runtimeActions;

        /// <summary>
        /// Action map holding the movement actions.
        /// </summary>
        private InputActionMap _playerActionMap;

        /// <summary>
        /// Move action; Value/Vector2.
        /// </summary>
        private InputAction _moveAction;

        /// <summary>
        /// Source of the per-tick speed multiplier. Optional.
        /// </summary>
        /// <remarks>
        /// Resolved by component rather than through an Inspector field, so putting the
        /// component on the prefab is the whole of the wiring. Missing is not an error: the
        /// player simply never slows down, which is how this class behaved before stamina
        /// existed.
        /// </remarks>
        private PlayerStamina _stamina;

        /// <summary>
        /// Reference to the CharacterController component.
        /// </summary>
        private CharacterController _characterController;

        /// <summary>
        /// Current vertical velocity.
        /// </summary>
        private float _verticalVelocity;

        /// <summary>
        /// Current facing, in degrees around the Y axis.
        /// </summary>
        private float _yaw;

        /// <summary>
        /// Last data which was supplied during replicate outside of reconcile.
        /// </summary>
        private ReplicateData _lastTickedReplicateData = default;

        private void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _stamina = GetComponent<PlayerStamina>();
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            /* Warned about rather than defaulted silently: without it the speed multiplier
             * stays at 1 forever, which looks exactly like a stamina system that is not
             * working, and there is nothing on screen to say which of the two it is. */
            if (_stamina == null)
                Debug.LogWarning($"{nameof(PlayerMovementPrediction)} on {gameObject.name} has no {nameof(PlayerStamina)}; the player will never slow down.", this);

            if (_inputActions == null)
            {
                Debug.LogError($"{nameof(PlayerMovementPrediction)} on {gameObject.name} has no InputActionAsset assigned; movement will not respond to input.", this);
                return;
            }

            /* Every player object needs its own copy of the action asset. The asset
             * is a shared ScriptableObject, so enabling or disabling a map through it
             * affects every player in the scene: a second player spawning (or
             * despawning) would enable or disable input for the first one, and with
             * one owner and one non-owner the last call always wins, which leaves
             * everyone unable to move. Instantiating gives each object an independent
             * map. This is the documented approach for running one action set
             * multiple times in parallel. */
            _runtimeActions = Instantiate(_inputActions);

            _playerActionMap = _runtimeActions.FindActionMap("Player", throwIfNotFound: true);
            /* Move is declared as a Value action with a Vector2 control type, bound to
             * the gamepad left stick and a WASD composite. */
            _moveAction = _playerActionMap.FindAction("Move", throwIfNotFound: true);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _playerActionMap?.Disable();
            _lastTickedReplicateData.Dispose();

            /* The action asset was instantiated per object rather than referenced, so
             * release it; otherwise every spawn would leak one asset for the session. */
            if (_runtimeActions != null)
            {
                Destroy(_runtimeActions);
                _runtimeActions = null;
            }

            _playerActionMap = null;
            _moveAction = null;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            SetInputEnabled(IsOwner);
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);

            /* Must stay symmetric with OnStartClient: if this only ever enabled,
             * a client that loses ownership would keep sampling input and building
             * replicate data it is no longer allowed to send. */
            SetInputEnabled(IsOwner);
        }

        /// <summary>
        /// Enables or disables polling of the movement actions.
        /// </summary>
        /// <remarks>
        /// Actions are not enabled by loading the asset; the enable state is not
        /// serialized on the asset, so it must be set explicitly at runtime.
        /// </remarks>
        public void SetInputEnabled(bool isEnabled)
        {
            if (_playerActionMap == null)
                return;

            if (isEnabled)
                _playerActionMap.Enable();
            else
                _playerActionMap.Disable();
        }

        protected override void TimeManager_OnTick()
        {
            PerformReplicate(BuildMoveData());
        }

        protected override void TimeManager_OnPostTick()
        {
            CreateReconcile();
        }

        /// <summary>
        /// Returns replicate data to send as the controller.
        /// </summary>
        private ReplicateData BuildMoveData()
        {
            /* Only the controller needs to build move data. */
            if (!IsOwner)
                return default;

            // Sampled on the tick, not per-frame: replicate is tick driven.
            Vector2 move = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;

            /* **The office is being set up, so nobody walks.** Zeroed here rather than by disabling
             * the input action or switching this component off, because this is the one place the
             * decision travels: whatever leaves here is what the server simulates, so a frozen
             * player is frozen on every machine rather than only on their own. Switching the
             * component off would also stop the reconcile, which would leave them able to drift.
             *
             * Zero input still lets gravity run inside the replicate, which is what keeps them
             * standing on the floor rather than hovering while they wait. And it costs no stamina:
             * PlayerStamina measures displacement and not key presses, which is the same rule that
             * stops a player walking into a wall from tiring themselves out. */
            if (OfficeLayout.MovementIsFrozen)
                move = Vector2.zero;

            /* Sampled on the tick as well, and for the same reason: the value that goes out
             * has to be the one that belongs to this tick, not one read back later during a
             * replay. With no stamina component this is a constant 1. */
            float speedMultiplier = _stamina != null ? _stamina.SpeedMultiplier : 1f;

            return new ReplicateData(move, speedMultiplier);
        }

        /// <summary>
        /// Creates a reconcile that is sent to clients.
        /// </summary>
        public override void CreateReconcile()
        {
            /* Both the server and client should create reconcile data. The client
             * will use their copy as a fallback if they do not get data from the
             * server, such as a dropped packet. */
            ReconcileData rd = new(transform.localPosition, _yaw, _verticalVelocity);
            PerformReconcile(rd);
        }

        [Replicate]
        private void PerformReplicate(ReplicateData rd, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            // Always use the tickDelta as your delta when performing actions inside replicate.
            float delta = (float)TimeManager.TickDelta;
            bool useDefaultForces = false;

            /* When client only, run some checks to further predict the owner's
             * future movement. This keeps the object more in line with real-time
             * by guessing what the input might be before it is received. */
            if (!IsServerStarted && !IsOwner)
            {
                if (state.ContainsTicked())
                {
                    _lastTickedReplicateData.Dispose();
                    _lastTickedReplicateData = rd;
                }
                else if (state.IsFuture())
                {
                    /* Predict up to one tick further. Guessing any more than that
                     * raises the chance of guessing wrong; smoothing covers it when
                     * we do. */
                    if (rd.GetTick() - _lastTickedReplicateData.GetTick() > 1)
                    {
                        useDefaultForces = true;
                    }
                    else
                    {
                        rd.Dispose();
                        rd = _lastTickedReplicateData;
                    }
                }
            }

            Vector3 forces;

            if (useDefaultForces)
            {
                /* CharacterControllers are problematic with colliders: passing
                 * Vector3.zero into Move risks other colliders clipping through the
                 * controller, and combined with reconciles that is practically
                 * guaranteed. An insignificant force makes the controller update
                 * properly instead. */
                forces = new(0f, -1f, 0f);
            }
            else
            {
                /* Anything which can affect the predicted state must happen inside
                 * replicate, otherwise the owner's prediction and the server's
                 * authoritative run diverge and every reconcile is a correction.
                 * Physics.gravity is a project constant, identical on every machine,
                 * so it is safe to read here. */
                _verticalVelocity += Physics.gravity.y * delta;
                if (_verticalVelocity < _terminalVelocity)
                    _verticalVelocity = _terminalVelocity;

                Vector2 input = rd.Input;

                if (input.sqrMagnitude > 0.0001f)
                {
                    Vector3 direction = new Vector3(input.x, 0f, input.y).normalized;

                    /* Facing is derived from the input stream, which makes it a pure
                     * function of replicated data: the server and every spectator
                     * arrive at the same value without spending bandwidth, and a
                     * replay rolls it back correctly. Never derive it from
                     * transform.rotation, which a previous reconcile may have set. */
                    float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                    _yaw = Mathf.MoveTowardsAngle(_yaw, targetYaw, _rotationRate * delta);
                    transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);

                    /* Fatigue slows the walk, not the turn: a tired player who also turned
                     * like a barge would be fighting the camera as well as the clock. */
                    forces = direction * (_moveRate * ResolveSpeedMultiplier(rd.SpeedMultiplier));
                }
                else
                {
                    forces = Vector3.zero;
                }

                forces.y = _verticalVelocity;
            }

            _characterController.Move(forces * delta);

            /* Keeps the controller glued to the ground instead of micro-bouncing on
             * it. The official demo omits this because its jump force lifts the
             * character clear of the ground; with no jump we need it. */
            if (!useDefaultForces && _characterController.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -1f;
        }

        /// <summary>
        /// Clamps a speed multiplier arriving from the network into the range this component
        /// is willing to move at.
        /// </summary>
        /// <remarks>
        /// Runs on every replay, so it doubles as the guard against a malformed value. A
        /// multiplier of zero — which is exactly what a default ReplicateData carries, and
        /// that is what a non-owner builds — or a NaN would otherwise freeze the character,
        /// and anything above 1 would let a modified client outrun everyone.
        /// </remarks>
        private static float ResolveSpeedMultiplier(float value)
        {
            /* Written as a positive test on purpose: NaN compares false against everything,
             * so a missing or malformed value and a zero both land in this branch instead of
             * needing a separate case. */
            if (!(value > 0f) || value > 1f)
                return 1f;

            return Mathf.Max(value, PlayerStamina.MinSpeedMultiplier);
        }

        [Reconcile]
        private void PerformReconcile(ReconcileData rd, Channel channel = Channel.Unreliable)
        {
            _verticalVelocity = rd.VerticalVelocity;
            _yaw = rd.Yaw;

            /* It is VERY important to disable the CharacterController component
             * before updating its position. Without the disable/enable work-around
             * below, the Transform shows the correct position but the physics for
             * the CharacterController remains at its prior position until the next
             * simulate. */
            _characterController.enabled = false;

            transform.localPosition = rd.LocalPosition;
            transform.localRotation = Quaternion.Euler(0f, rd.Yaw, 0f);

            _characterController.enabled = true;
        }
    }
}
