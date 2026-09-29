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
    /// Put it on the occupying object's root, alongside that object's collider — the collider is
    /// what the probe actually hits, and the walk runs from there up to the surface. Use
    /// <see cref="SnapSurface.AcceptsObjects"/> instead when a whole table should refuse
    /// placement; this is for the individual cells an object sits in.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlacementBlocker : MonoBehaviour
    {
    }
}
