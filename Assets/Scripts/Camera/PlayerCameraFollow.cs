using FishNet.Object;
using UnityEngine;

namespace Overworked.Cameras
{
    /// <summary>
    /// Follows the locally owned player, preserving the offset and rotation authored in the scene.
    /// </summary>
    /// <remarks>
    /// The camera is deliberately not networked. Every client runs its own and follows the
    /// player it owns, which is why this only looks for an object it has ownership of.
    ///
    /// Rotation is never written here: the angle is authored on the camera in the scene and
    /// this component only moves it. Change the angle in the scene, not in code.
    /// </remarks>
    public class PlayerCameraFollow : MonoBehaviour
    {
        [Header("Target")]

        /// <summary>
        /// World-space offset from the player, matching the angle authored in the scene.
        /// </summary>
        [Tooltip("World-space offset from the player. Matches the camera angle authored in the scene.")]
        [SerializeField]
        private Vector3 _offset = new(0f, 5.65f, -2.875f);

        /// <summary>
        /// Only objects with this tag are considered as the follow target.
        /// </summary>
        [Tooltip("Only objects with this tag are considered as the follow target.")]
        [SerializeField]
        private string _targetTag = "Player";

        /// <summary>
        /// Child of the player to follow instead of the player itself.
        /// </summary>
        /// <remarks>
        /// The player root moves once per tick, so following it directly makes the camera
        /// step with the network tick. The graphical child is smoothed between ticks by a
        /// NetworkTickSmoother; following that instead keeps the character stationary on
        /// screen while the world moves smoothly. Falls back to the root if no such child
        /// exists, so the camera still works without a smoother set up.
        /// </remarks>
        [Tooltip("Child of the player to follow instead of the player itself. Falls back to the root if not found.")]
        [SerializeField]
        private string _targetChildName = "Graphical";

        /// <summary>
        /// Jump straight to the target on first acquisition instead of gliding across the map.
        /// </summary>
        [Tooltip("Jump straight to the target the first time it is acquired, instead of gliding across the map.")]
        [SerializeField]
        private bool _snapOnAcquire = true;

        [Header("Smoothing")]

        /// <summary>
        /// Approximate time to catch up to the target, in seconds. Zero follows rigidly.
        /// </summary>
        [Tooltip("Approximate time to catch up to the target, in seconds. Zero follows rigidly.")]
        [SerializeField]
        private float _smoothTime = 0.12f;

        /// <summary>
        /// Current smoothing velocity, owned by SmoothDamp.
        /// </summary>
        private Vector3 _velocity;

        /// <summary>
        /// Transform being followed; null until the local player spawns.
        /// </summary>
        private Transform _target;

        /// <summary>
        /// True once a target has been snapped to, so the snap only happens once.
        /// </summary>
        private bool _snappedToTarget;

        private void LateUpdate()
        {
            /* Runs in LateUpdate so the camera reads the player's position after movement
             * for this frame has been applied. */
            if (_target == null)
            {
                if (!TryAcquireTarget())
                    return;
            }

            Vector3 desired = _target.position + _offset;

            bool shouldSnap = _snapOnAcquire && !_snappedToTarget;
            if (shouldSnap || _smoothTime <= 0f)
            {
                transform.position = desired;
                _velocity = Vector3.zero;
                _snappedToTarget = true;
                return;
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                desired,
                ref _velocity,
                _smoothTime,
                Mathf.Infinity,
                Time.deltaTime);
        }

        /// <summary>
        /// Looks for the player this client owns.
        /// </summary>
        /// <remarks>
        /// Only called while there is no target, so the scan does not run every frame once
        /// the local player exists. Ownership is the discriminator: on a client exactly one
        /// player belongs to it, and on the host that is the host's own player.
        /// </remarks>
        /// <returns>True if a target was found.</returns>
        private bool TryAcquireTarget()
        {
            NetworkObject[] candidates = FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude);

            foreach (NetworkObject candidate in candidates)
            {
                if (!candidate.IsOwner)
                    continue;
                if (!candidate.gameObject.CompareTag(_targetTag))
                    continue;

                Transform root = candidate.transform;

                /* Prefer the smoothed graphical child when it exists. At acquisition the
                 * child still sits at the root's position, so snapping to it is correct. */
                Transform child = string.IsNullOrEmpty(_targetChildName)
                    ? null
                    : root.Find(_targetChildName);

                _target = child != null ? child : root;
                return true;
            }

            return false;
        }
    }
}
