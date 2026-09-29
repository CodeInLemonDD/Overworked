using FishNet.Connection;
using FishNet.Utility.Template;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overworked.Player
{
    /// <summary>
    /// How long the player can keep moving before fatigue slows them down.
    /// </summary>
    /// <remarks>
    /// Two layers, as designed: 精力 is the cap the bar starts at and returns to, 体力 is the
    /// current value, and only 体力 has any effect. This round its one consumer is movement
    /// speed, through <see cref="SpeedMultiplier"/>.
    ///
    /// Simulated on the owner only, and never sent as a sync type. The single value the rest
    /// of the game needs — the speed multiplier — is carried inside
    /// <see cref="PlayerMovementPrediction.ReplicateData"/> instead, so that a rollback replay
    /// re-uses the multiplier that belonged to the tick being replayed rather than whatever
    /// the current frame happens to hold. Reading live state from inside a [Replicate] method
    /// is the one way to make the two machines disagree about where the character is; see that
    /// file for the consuming half of the arrangement.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlayerStamina : TickNetworkBehaviour
    {
        /// <summary>
        /// Slowest the player can be moved by fatigue, as a fraction of the normal rate.
        /// </summary>
        /// <remarks>
        /// An empty bar means slow, never stopped. A character that refused to move at all
        /// would read as a broken controller rather than as exhaustion, and the player would
        /// have no way to tell which of the two they were looking at.
        /// </remarks>
        public const float MinSpeedMultiplier = 0.3f;

        [Header("Input")]

        /// <summary>
        /// Assign Assets/InputSystem_Actions.inputactions.
        /// </summary>
        [Tooltip("Assign Assets/InputSystem_Actions.inputactions.")]
        [SerializeField]
        private InputActionAsset _inputActions;

        [Header("Stamina")]

        /// <summary>
        /// 精力: the bar's maximum, and the value it recovers to.
        /// </summary>
        [Tooltip("精力: the bar's maximum, and the value it recovers to.")]
        [SerializeField]
        private float _staminaCap = 100f;

        /// <summary>
        /// 体力 spent per second while a movement direction is held.
        /// </summary>
        [Tooltip("体力 spent per second while a movement direction is held.")]
        [SerializeField]
        private float _drainPerSecond = 12f;

        /// <summary>
        /// Seconds of standing still before the bar returns to the cap.
        /// </summary>
        [Tooltip("Seconds of standing still before the bar returns to the cap.")]
        [SerializeField]
        private float _recoveryDelay = 2f;

        /// <summary>
        /// Fraction of the cap below which movement starts to slow down.
        /// </summary>
        /// <remarks>
        /// Only the bottom slice of the bar costs speed. Losing a sliver should not change how
        /// the character handles, or the slowdown reads as an input fault rather than as
        /// fatigue and the player cannot tell when it is safe to stop.
        /// </remarks>
        [Tooltip("Fraction of the cap below which movement starts to slow down.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _slowBelowRatio = 0.35f;

        /// <summary>
        /// 体力 remaining.
        /// </summary>
        public float Stamina => _stamina;

        /// <summary>
        /// 精力: the value <see cref="Stamina"/> is measured against.
        /// </summary>
        public float StaminaCap => _staminaCap;

        /// <summary>
        /// Fraction of the normal move rate the player is currently allowed.
        /// </summary>
        /// <remarks>
        /// Always 1 on instances this client does not own — only the owner simulates stamina.
        /// </remarks>
        public float SpeedMultiplier => _speedMultiplier;

        /// <summary>
        /// The stamina of the player this client owns, or null when there is none.
        /// </summary>
        /// <remarks>
        /// A HUD must not reach for this with FindObjectOfType: every client holds a copy of
        /// this component for every player in the scene, and which one comes back first is
        /// arbitrary, so a bar built that way silently displays a remote player's stamina —
        /// which is always full. This is the one that belongs to the local player.
        /// </remarks>
        public static PlayerStamina Local { get; private set; }

        /// <summary>
        /// Per-object copy of the assigned action asset.
        /// </summary>
        /// <remarks>
        /// The asset itself is shared, so this object must not enable or disable maps through
        /// it. See OnStartNetwork.
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
        /// 体力 remaining.
        /// </summary>
        private float _stamina;

        /// <summary>
        /// Seconds spent standing still since the last movement input.
        /// </summary>
        private float _idleSeconds;

        /// <summary>
        /// Cached result of the last <see cref="RefreshSpeedMultiplier"/>.
        /// </summary>
        private float _speedMultiplier = 1f;

        private void Awake()
        {
            /* Once a frame, and — under TimeManager's default order — ahead of the tick, so
             * the value a tick reads is the one this frame just produced. Stamina is measured
             * in wall-clock seconds of input rather than in ticks, and is only ever sampled
             * on the tick by PlayerMovementPrediction, so it does not need to be tick
             * aligned. */
            SetTickCallbacks(TickCallback.Update);

            _stamina = _staminaCap;
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            /* A pooled object keeps its fields across spawns, so reset here rather than
             * trusting Awake to have run on this particular one. */
            _stamina = _staminaCap;
            _idleSeconds = 0f;
            _speedMultiplier = 1f;

            if (_inputActions == null)
            {
                Debug.LogError($"{nameof(PlayerStamina)} on {gameObject.name} has no InputActionAsset assigned; stamina will stay full and never slow the player down.", this);
                return;
            }

            /* Every player object needs its own copy of the action asset. The asset is a
             * shared ScriptableObject, so enabling or disabling a map through it affects
             * every player in the scene: a second player spawning (or despawning) would
             * enable or disable input for the first one, and with one owner and one
             * non-owner the last call always wins, which leaves everyone unable to move.
             * Instantiating gives each object an independent map. */
            _runtimeActions = Instantiate(_inputActions);

            _playerActionMap = _runtimeActions.FindActionMap("Player", throwIfNotFound: true);
            /* Move is declared as a Value action with a Vector2 control type, bound to the
             * gamepad left stick and a WASD composite. */
            _moveAction = _playerActionMap.FindAction("Move", throwIfNotFound: true);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            if (Local == this)
                Local = null;

            _playerActionMap?.Disable();

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
            SetLocal();
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);

            /* Must stay symmetric with OnStartClient: if this only ever enabled, a client
             * that loses ownership would keep draining stamina it is no longer simulating. */
            SetInputEnabled(IsOwner);
            SetLocal();
        }

        /// <summary>
        /// Publishes or withdraws this instance as <see cref="Local"/>.
        /// </summary>
        private void SetLocal()
        {
            if (IsOwner)
                Local = this;
            else if (Local == this)
                Local = null;
        }

        /// <summary>
        /// Enables or disables polling of the movement actions.
        /// </summary>
        /// <remarks>
        /// Actions are not enabled by loading the asset; the enable state is not serialized on
        /// the asset, so it must be set explicitly at runtime.
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

        protected override void TimeManager_OnUpdate()
        {
            /* Only the owner has the input, and only the owner's value is ever sent. Every
             * other copy of this component — on the server for a remote player, and on every
             * observer — just sits at the cap. */
            if (!IsOwner)
                return;

            /* Scaled time, unlike the network timers in CONSTRAINTS #6: stamina is a gameplay
             * quantity, so it should freeze along with the game rather than keep draining
             * behind a pause. */
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;

            if (IsMoving())
            {
                /* Standing still is the only thing that recovers stamina, so any movement
                 * input at all restarts the wait rather than merely pausing it. */
                _idleSeconds = 0f;
                _stamina = Mathf.Max(0f, _stamina - (_drainPerSecond * deltaTime));
            }
            else
            {
                /* Clamped rather than left to grow: it is only ever compared against the
                 * delay, and the clamp is what makes a delay of zero recover immediately. */
                _idleSeconds = Mathf.Min(_idleSeconds + deltaTime, _recoveryDelay);

                if (_idleSeconds >= _recoveryDelay)
                    _stamina = _staminaCap;
            }

            RefreshSpeedMultiplier();
        }

        /// <summary>
        /// True while a movement direction is held.
        /// </summary>
        /// <remarks>
        /// Same action and same threshold as <see cref="PlayerMovementPrediction"/>: anything
        /// above it is full-speed movement there, so anything above it costs stamina here.
        /// Holding a direction against a wall still counts — the player is pushing, not
        /// resting, and rewarding them for leaning on geometry would turn the bar into a
        /// button-mashing puzzle.
        /// </remarks>
        private bool IsMoving()
        {
            if (_moveAction == null)
                return false;

            return _moveAction.ReadValue<Vector2>().sqrMagnitude > 0.0001f;
        }

        /// <summary>
        /// Recomputes <see cref="SpeedMultiplier"/> from the current stamina.
        /// </summary>
        private void RefreshSpeedMultiplier()
        {
            if (_staminaCap <= 0f || _slowBelowRatio <= 0f)
            {
                _speedMultiplier = 1f;
                return;
            }

            float ratio = Mathf.Clamp01(_stamina / _staminaCap);

            _speedMultiplier = ratio >= _slowBelowRatio
                ? 1f
                : Mathf.Lerp(MinSpeedMultiplier, 1f, ratio / _slowBelowRatio);
        }
    }
}
