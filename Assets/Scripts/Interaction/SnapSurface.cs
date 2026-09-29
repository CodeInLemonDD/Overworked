using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// Marks a surface that objects may be placed on, and answers where that surface is.
    /// </summary>
    /// <remarks>
    /// Holds no footprint data on purpose. Everything is resolved by raycasting downward and
    /// checking whether the hit belongs to this object, so a table that spans several grid
    /// cells — or a stack of them — works without any change here.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SnapSurface : MonoBehaviour
    {
        /// <summary>
        /// Minimum Y of the surface normal for a hit to count as a top face.
        /// </summary>
        [Tooltip("Minimum Y of the surface normal for a hit to count as a top face.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _minSurfaceNormalY = 0.9f;

        /// <summary>
        /// How far below the probe point a surface is still found.
        /// </summary>
        [Tooltip("How far below the probe point a surface is still found.")]
        [SerializeField]
        private float _raycastDistance = 3f;

        /// <summary>
        /// Whether objects may be placed on this surface at all.
        /// </summary>
        [Tooltip("Off = this whole surface refuses placement. For a table that is scenery or " +
                 "otherwise out of play; to block a single occupied cell, put a PlacementBlocker " +
                 "on the object standing there instead.")]
        [SerializeField]
        private bool _acceptsObjects = true;

        /// <summary>
        /// Minimum Y of the surface normal for a hit to count as a top face.
        /// </summary>
        public float MinSurfaceNormalY => _minSurfaceNormalY;

        /// <summary>
        /// How far below the probe point a surface is still found.
        /// </summary>
        public float RaycastDistance => _raycastDistance;

        /// <summary>
        /// Whether objects may be placed on this surface at all.
        /// </summary>
        public bool AcceptsObjects => _acceptsObjects;

        /// <summary>
        /// Returns true when the given collider belongs to this surface.
        /// </summary>
        public bool Owns(Collider candidate) =>
            candidate != null && candidate.transform.IsChildOf(transform);

        /// <summary>
        /// Returns true when a hit on the given collider must not be treated as this surface.
        /// </summary>
        /// <remarks>
        /// Ownership alone is not enough to call a hit placeable. A fixed prop standing on a table
        /// is parented to the table, so it is "owned" by the same SnapSurface as the tabletop and
        /// answers with its own top face as the surface height — which is how objects used to get
        /// yanked up onto whatever was standing on the table. A machine sitting in a cell is the
        /// same shape of problem.
        ///
        /// The hit cannot tell itself apart from the tabletop, so anything on the path from the
        /// hit up to this surface that carries a <see cref="PlacementBlocker"/> blocks it. The
        /// walk covers this surface's own transform too, so a blocker on the root that holds both
        /// the collider and the SnapSurface blocks as well.
        /// </remarks>
        private bool IsBlocked(Collider candidate)
        {
            if (!_acceptsObjects)
                return true;

            for (Transform t = candidate.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<PlacementBlocker>() != null)
                    return true;
                if (t == transform)
                    break;
            }

            return false;
        }

        /// <summary>
        /// Casts down at the given XZ and reports the surface height, but only if the hit is
        /// this surface and is facing up.
        /// </summary>
        /// <param name="worldXZ">Position to probe; its Y is ignored.</param>
        /// <param name="probeY">Height to start the cast from.</param>
        /// <param name="distance">How far down to look.</param>
        /// <param name="ignore">Colliders belonging to this transform are skipped.</param>
        /// <param name="surfaceY">Height of the surface at that XZ.</param>
        /// <returns>True when this surface was hit from above.</returns>
        public bool TrySample(Vector3 worldXZ, float probeY, float distance, Transform ignore, out float surfaceY)
        {
            surfaceY = 0f;

            Vector3 origin = new(worldXZ.x, probeY, worldXZ.z);
            if (!TryRaycastDown(origin, distance, ignore, out RaycastHit hit))
                return false;
            if (!Owns(hit.collider))
                return false;
            if (IsBlocked(hit.collider))
                return false;
            if (hit.normal.y < _minSurfaceNormalY)
                return false;

            surfaceY = hit.point.y;
            return true;
        }

        /// <summary>
        /// Casts down at the given world position and returns whichever surface is below it.
        /// </summary>
        /// <param name="worldPosition">Position to probe from.</param>
        /// <param name="probeLift">How far above <paramref name="worldPosition"/> to start the cast.</param>
        /// <param name="distance">How far below the probe point to look.</param>
        /// <param name="ignore">Colliders belonging to this transform are skipped.</param>
        /// <param name="surface">The surface that was hit, if any.</param>
        /// <param name="surfaceY">Height of the surface at that XZ.</param>
        public static bool TryFind(
            Vector3 worldPosition,
            float probeLift,
            float distance,
            Transform ignore,
            out SnapSurface surface,
            out float surfaceY)
        {
            surface = null;
            surfaceY = 0f;

            Vector3 origin = new(worldPosition.x, worldPosition.y + probeLift, worldPosition.z);
            if (!TryRaycastDown(origin, probeLift + distance, ignore, out RaycastHit hit))
                return false;

            surface = hit.collider.GetComponentInParent<SnapSurface>();
            if (surface == null)
                return false;
            if (hit.normal.y < surface.MinSurfaceNormalY)
            {
                surface = null;
                return false;
            }

            /* Checked here rather than only at the call sites: stations pick their candidate
             * cells through this method, and a cell that is refused on one path but accepted on
             * the other is exactly how a machine ends up with objects stacked on top of it. */
            if (surface.IsBlocked(hit.collider))
            {
                surface = null;
                return false;
            }

            surfaceY = hit.point.y;
            return true;
        }

        /// <summary>
        /// Buffer reused by <see cref="TryRaycastDown"/>.
        /// </summary>
        /// <remarks>
        /// Static because the probe may be called from a static context. Only ever touched on
        /// the main thread, which is where all physics queries have to run anyway.
        /// </remarks>
        private static readonly RaycastHit[] ProbeHits = new RaycastHit[8];

        /// <summary>
        /// Returns the closest downward hit that does not belong to <paramref name="ignore"/>.
        /// </summary>
        /// <remarks>
        /// A plain Raycast cannot be used here: an object resting on a surface has its own
        /// colliders enabled, so a ray cast down from above it hits the object itself first
        /// and the surface underneath is never seen. The object has no SnapSurface, so that
        /// first hit would read as "no surface here" and nothing would ever snap.
        /// </remarks>
        private static bool TryRaycastDown(Vector3 origin, float distance, Transform ignore, out RaycastHit closest)
        {
            closest = default;

            int count = Physics.RaycastNonAlloc(origin, Vector3.down, ProbeHits, distance, ~0, QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = ProbeHits[i];

                if (ignore != null && hit.collider.transform.IsChildOf(ignore))
                    continue;
                if (hit.distance >= bestDistance)
                    continue;

                bestDistance = hit.distance;
                closest = hit;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Warns in the editor when the table is not authored on a grid centre.
        /// </summary>
        /// <remarks>
        /// The snap itself is exact; the authoring is not, and a table half a metre off the
        /// grid produces placement cells that miss it entirely. Catching that here is much
        /// cheaper than debugging it in play mode.
        /// </remarks>
        private void OnValidate()
        {
            Vector3 p = transform.position;
            Vector2 centre = WorldGrid.CellCentreXZ(new Vector2(p.x, p.z));

            if (Mathf.Abs(centre.x - p.x) > 0.001f || Mathf.Abs(centre.y - p.z) > 0.001f)
            {
                Debug.LogWarning(
                    $"{nameof(SnapSurface)} on '{name}' is not on a grid centre. " +
                    $"Its XZ is ({p.x:0.###}, {p.z:0.###}) but the nearest cell centre is ({centre.x:0.###}, {centre.y:0.###}). " +
                    "Placement snapping will land objects off this surface.",
                    this);
            }
        }
    }
}
