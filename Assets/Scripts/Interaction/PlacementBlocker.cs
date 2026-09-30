using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// Marks a fixed object that stands in a grid cell as not placeable, and blocks that cell.
    /// </summary>
    /// <remarks>
    /// A fixed prop standing on a table is parented to the table, so a downward probe hits the
    /// prop and <c>GetComponentInParent&lt;SnapSurface&gt;()</c> walks up to the table's own
    /// SnapSurface. The hit then looks like a perfectly good surface whose height happens to be
    /// the prop's top face, and objects dropped over the table get yanked up onto the prop. A
    /// machine is the same case: it stands in a cell, and that cell must not accept placement.
    ///
    /// Nothing in the hit itself can tell "this is the tabletop" from "this is something standing
    /// on the tabletop" — both are children of the same SnapSurface. So the object occupying the
    /// space declares it here, and <see cref="SnapSurface"/> rejects any hit whose path up to the
    /// surface passes through one of these.
    ///
    /// Put it on the object that owns the collider the probe hits, or on a node between that
    /// collider and the surface it stands on. The walk starts at the hit and stops at the first
    /// SnapSurface it reaches, so a blocker must sit <em>at or below</em> a surface and never
    /// above one — which is what <see cref="OnValidate"/> warns about. Use
    /// <see cref="SnapSurface.AcceptsObjects"/> instead when a whole table should refuse
    /// placement; this is for the individual cells an object sits in.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlacementBlocker : MonoBehaviour
    {
        /// <summary>
        /// Warns in the editor when this blocker is authored where the probe walk can never reach it.
        /// </summary>
        /// <remarks>
        /// <c>SnapSurface.IsBlocked</c> climbs from the collider it hits and stops at the first
        /// SnapSurface it reaches, so a blocker above that point is never seen and the cell stays
        /// placeable — the exact bug this component exists to prevent. The failure is otherwise
        /// silent: the hierarchy looks fine, and the symptom (objects yanked onto the prop) points
        /// back at the probe code rather than at the authoring.
        ///
        /// Two mistakes make a blocker useless, and both are checked, because either one alone
        /// leaves a hole:
        /// <list type="bullet">
        /// <item>nothing hittable on this node or under it — the walk starts at the collider it
        /// hits, so with no collider down there no probe can ever pass through here;</item>
        /// <item>a SnapSurface that accepts objects below this node — the walk stops at that one
        /// first, so hits on it are never blocked and objects can still be placed there.</item>
        /// </list>
        ///
        /// Deliberately <em>not</em> checked: whether any surface exists at or above this node. A
        /// machine stands in its cell on its own with nothing above it, and its own table is
        /// furniture rather than a surface — warning there would fire on every machine. Nothing
        /// leaks in that shape either: with no surface to resolve, the probe already refuses the
        /// cell without any help from a blocker.
        /// </remarks>
        private void OnValidate()
        {
            if (!HasHittableCollider())
            {
                Debug.LogWarning(
                    $"{nameof(PlacementBlocker)} on '{name}' has no collider on it or under it, so no probe " +
                    "can ever hit anything here and be stopped: the walk starts at the collider it hits. " +
                    "Put this component on the object that owns the collider, or on a node between that " +
                    "collider and the surface it stands on.",
                    this);
                return;
            }

            SnapSurface below = FindSurfaceBelow();
            if (below == null)
                return;

            Debug.LogWarning(
                $"{nameof(PlacementBlocker)} on '{name}' sits above the {nameof(SnapSurface)} on " +
                $"'{below.name}'. The walk stops at the first surface it reaches, so hits on that surface " +
                "are never blocked and objects can still be placed there. Move this component down onto " +
                "the object that owns the collider it should block — or, if that surface should not accept " +
                $"objects either, turn off '{nameof(SnapSurface.AcceptsObjects)}' on it: it then refuses " +
                "placement on its own.",
                this);
        }

        /// <summary>
        /// Returns true when a probe could actually hit something on this node or under it.
        /// </summary>
        /// <remarks>
        /// Triggers do not count: the probe casts with <c>QueryTriggerInteraction.Ignore</c>, so a
        /// trigger-only subtree can never stop a probe either.
        /// </remarks>
        private bool HasHittableCollider()
        {
            Collider[] colliders = GetComponentsInChildren<Collider>(false);

            foreach (Collider collider in colliders)
            {
                if (!collider.isTrigger)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the first surface under this node that placement can still reach, if any.
        /// </summary>
        /// <remarks>
        /// This node itself is excluded — a blocker sitting on the surface it guards is the normal,
        /// working arrangement — as are surfaces that do not accept objects, since those refuse
        /// placement without any help from a blocker.
        /// </remarks>
        private SnapSurface FindSurfaceBelow()
        {
            SnapSurface[] surfaces = GetComponentsInChildren<SnapSurface>(false);

            foreach (SnapSurface surface in surfaces)
            {
                if (surface.transform == transform)
                    continue;
                if (surface.AcceptsObjects)
                    return surface;
            }

            return null;
        }
    }
}
