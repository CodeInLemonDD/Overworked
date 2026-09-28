using FishNet.Connection;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
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
            public ReplicateData(Vector2 input)
            {
                Input = input;
                _tick = 0;
            }

            /// <summary>
            /// Movement direction held this tick, in the XZ plane.
            /// </summary>
            public Vector2 Input;

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
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

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
        private void SetInputEnabled(bool isEnabled)
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
            return new ReplicateData(move);
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

                    forces = direction * _moveRate;
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
