using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Npc
{
    /// <summary>
    /// Walks a fixed patrol loop and picks up loose objects she passes.
    /// </summary>
    /// <remarks>
    /// The pressure this applies is spatial, not random: her route is authored and her speed is
    /// constant, so a player who loses something to her has only themselves to blame. A random
    /// walk would read as bad luck instead, which is a different and much worse feeling to play
    /// against. Do not add wandering.
    ///
    /// She is a plain MonoBehaviour with no NetworkObject. Her patrol is deterministic — same
    /// waypoints, same speed, no input — so every peer can run it locally and they will agree
    /// closely enough to look right, without paying to replicate a transform nobody interacts
    /// with. Only the removal of objects has to be authoritative, so only that is gated on the
    /// server.
    ///
    /// She has no collider on purpose. She must not shove the objects she is about to collect,
    /// and a player being pushed around by the janitor is not a mechanic anyone asked for.
    ///
    /// Table tops and container interiors are safe by construction, not by inspection:
    /// anything placed on a surface is flagged with a cell, and container contents are entries
    /// in a list rather than objects in the world. Neither is visible here, which is the point.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Cleaner : MonoBehaviour
    {
        [Header("Patrol")]

        /// <summary>
        /// Waypoints she walks between, in order, looping back to the first.
        /// </summary>
        [Tooltip("Waypoints she walks between, in order, looping back to the first. Leave empty to keep her standing still.")]
        [SerializeField]
        private Transform[] _patrolPoints;

        /// <summary>
        /// How fast she walks, in metres per second.
        /// </summary>
        [Tooltip("How fast she walks, in metres per second. Constant: she never speeds up or pauses.")]
        [Min(0.01f)]
        [SerializeField]
        private float _moveSpeed = 1.5f;

        /// <summary>
        /// How fast she turns to face the way she is walking, in degrees per second.
        /// </summary>
        [Tooltip("How fast she turns to face the way she is walking, in degrees per second.")]
        [SerializeField]
        private float _turnSpeedDegrees = 360f;

        [Header("Cleaning")]

        /// <summary>
        /// How far from her own cell she sweeps, in cells.
        /// </summary>
        [Tooltip("How far from her own cell she sweeps, in cells. 1 is the agreed 3x3 block around her.")]
        [Min(0)]
        [SerializeField]
        private int _cleanRadiusCells = 1;

        /// <summary>
        /// How often she sweeps, in seconds.
        /// </summary>
        /// <remarks>
        /// Not every frame. The sweep is a full pass over every spawned object, and at walking
        /// speed she covers a fraction of a cell per interval — sweeping more often would find
        /// nothing extra, since nothing can enter the block and come to rest in between.
        /// </remarks>
        [Tooltip("How often she sweeps, in seconds.")]
        [Min(0.01f)]
        [SerializeField]
        private float _cleanInterval = 0.5f;

        /// <summary>
        /// Speed below which a loose object counts as sitting still.
        /// </summary>
        /// <remarks>
        /// Matches the settle speed objects use to decide they have stopped, so an object that
        /// is still travelling never looks at rest to her and vice versa.
        /// </remarks>
        [Tooltip("Speed below which a loose object counts as sitting still.")]
        [SerializeField]
        private float _motionThreshold = 0.15f;

        [Header("Gizmos")]

        /// <summary>
        /// Draws the patrol loop and the area she sweeps when selected.
        /// </summary>
        [Tooltip("Draws the patrol loop and the area she sweeps when selected.")]
        [SerializeField]
        private bool _drawGizmos = true;

        /// <summary>
        /// Waypoint she is currently walking towards.
        /// </summary>
        private int _patrolIndex;

        /// <summary>
        /// Seconds since the last sweep.
        /// </summary>
        private float _cleanTimer;

        /// <summary>
        /// Reused by the sweep. See <see cref="GrabbableSpawner.CollectSpawnedGrabbables"/>.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        private void Start()
        {
            /* Worth saying out loud: with no route she stands still, which looks exactly like a
             * broken script from inside the game. */
            if (_patrolPoints == null || _patrolPoints.Length == 0)
                Debug.LogWarning($"{nameof(Cleaner)} on {gameObject.name} has no patrol points, so she will not move.", this);
        }

        private void Update()
        {
            Patrol(Time.deltaTime);

            if (!InstanceFinder.IsServer)
                return;

            _cleanTimer += Time.deltaTime;
            if (_cleanTimer < _cleanInterval)
                return;

            _cleanTimer = 0f;
            Clean();
        }

        /// <summary>
        /// Walks her one step along the loop and points her the way she is going.
        /// </summary>
        /// <remarks>
        /// Runs on every peer. Her height is taken from her own transform rather than from the
        /// waypoints, so a route authored flat on the floor still works if she is ever placed
        /// on a raised platform.
        /// </remarks>
        private void Patrol(float deltaTime)
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0)
                return;

            Transform target = _patrolPoints[_patrolIndex];
            if (target == null)
            {
                AdvancePatrol();
                return;
            }

            Vector3 position = transform.position;
            Vector3 destination = target.position;
            destination.y = position.y;

            Vector3 offset = destination - position;
            float distance = offset.magnitude;

            if (distance <= _moveSpeed * deltaTime)
            {
                transform.position = destination;
                AdvancePatrol();
                return;
            }

            transform.position = position + (offset / distance) * (_moveSpeed * deltaTime);

            Vector3 heading = offset;
            heading.y = 0f;
            if (heading.sqrMagnitude > 1e-6f)
            {
                Quaternion look = Quaternion.LookRotation(heading, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    look,
                    _turnSpeedDegrees * deltaTime);
            }
        }

        /// <summary>
        /// Moves to the next waypoint, wrapping at the end of the loop.
        /// </summary>
        private void AdvancePatrol() => _patrolIndex = (_patrolIndex + 1) % _patrolPoints.Length;

        /// <summary>
        /// Server: destroys every loose object in the block around her.
        /// </summary>
        private void Clean()
        {
            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null || !manager.IsServerStarted)
                return;

            Vector3 position = transform.position;
            Vector2Int centre = WorldGrid.CellCoord(new Vector2(position.x, position.z));

            /* Snapshot first. Despawning while enumerating ServerManager.Objects.Spawned throws,
             * because that collection is a live view over a Dictionary and Despawn removes the
             * key synchronously. The buffer is our own list, so removing from the world while
             * walking it is safe. */
            GrabbableSpawner.CollectSpawnedGrabbables(manager, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
            {
                NetworkGrabbable grabbable = _scanBuffer[i];
                if (!IsCollectable(grabbable, centre))
                    continue;

                /* Destroy rather than pool: these objects carry per-life state — the placed
                 * cell, the settle timer, the payload — that a recycled instance would bring
                 * back with it. Passed explicitly so this does not depend on the prefab. */
                grabbable.NetworkObject.Despawn(DespawnType.Destroy);
            }

            _scanBuffer.Clear();
        }

        /// <summary>
        /// True when this object is loose on the ground inside the block she is sweeping.
        /// </summary>
        /// <remarks>
        /// Every test here is a state the object reports about itself, deliberately rather than
        /// a raycast. A downward probe would find the table under a dropped object, and working
        /// out whether an object is "on the ground" from geometry means re-deciding something
        /// the placement system already decided and recorded exactly.
        /// </remarks>
        private bool IsCollectable(NetworkGrabbable grabbable, Vector2Int centre)
        {
            if (grabbable == null || !grabbable.IsSpawned)
                return false;

            NetworkObject nob = grabbable.NetworkObject;
            if (nob == null)
                return false;

            /* A scene object despawns to SetActive(false) with no way back. Nothing should be
             * authoring grabbables directly in the scene, but the failure is silent and
             * permanent, so it is worth one check. */
            if (nob.IsSceneObject)
                return false;

            /* Held or thrown. Both are states where the object is visibly in use, and neither
             * is on the ground. */
            if (grabbable.State != GrabbableState.Idle)
                return false;

            /* Somewhere a surface claimed it — a table top, or anywhere else a SnapSurface
             * accepted it. This is the whole of the "tables are safe" rule. */
            if (grabbable.PlacedCell.HasValue)
                return false;

            /* Owned by a client, so that client is simulating it. The server's copy is
             * kinematic while that is true and would report zero velocity no matter how fast
             * the object is really moving. */
            if (nob.OwnerId >= 0)
                return false;

            /* Kinematic and server-owned is not a state a resting object should be in, so it
             * means something is driving it by transform and its velocity cannot be trusted.
             * Leave it alone: a missed pickup is a nuisance, a collected one is a lost item. */
            Rigidbody body = grabbable.Body;
            if (body == null || body.isKinematic)
                return false;

            /* Still travelling. This is what keeps a freshly spawned object from being eaten on
             * its way down, and what stops her taking something a player just kicked past. */
            if (body.linearVelocity.sqrMagnitude > _motionThreshold * _motionThreshold)
                return false;

            Vector3 objectPosition = grabbable.transform.position;
            Vector2Int cell = WorldGrid.CellCoord(new Vector2(objectPosition.x, objectPosition.z));

            return Mathf.Abs(cell.x - centre.x) <= _cleanRadiusCells
                && Mathf.Abs(cell.y - centre.y) <= _cleanRadiusCells;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_drawGizmos)
                return;

            if (_patrolPoints != null && _patrolPoints.Length > 0)
            {
                Gizmos.color = new Color(0.2f, 0.8f, 1f);

                for (int i = 0; i < _patrolPoints.Length; i++)
                {
                    Transform from = _patrolPoints[i];
                    if (from == null)
                        continue;

                    Gizmos.DrawWireSphere(from.position, 0.15f);

                    Transform to = _patrolPoints[(i + 1) % _patrolPoints.Length];
                    if (to != null)
                        Gizmos.DrawLine(from.position, to.position);
                }
            }

            /* The swept block is centred on her cell, not on her, so it snaps as she crosses a
             * cell boundary. Drawing it that way shows what she actually takes. */
            Vector3 position = transform.position;
            Vector2Int centre = WorldGrid.CellCoord(new Vector2(position.x, position.z));
            Vector3 cellCentre = WorldGrid.CellCentre(centre);

            float size = (_cleanRadiusCells * 2 + 1) * WorldGrid.CellSize;
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.75f);
            Gizmos.DrawWireCube(
                new Vector3(cellCentre.x, position.y, cellCentre.z),
                new Vector3(size, 0.05f, size));
        }
    }
}
