using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Marks a desk as something the office layout is allowed to move.
    /// </summary>
    /// <remarks>
    /// **On the table prefab, not on the ones in the scene.** Every desk gets this by being the same
    /// prefab; nothing has to be ticked per instance, and a desk added to the scene tomorrow is
    /// already part of the layout.
    ///
    /// **A marker rather than a search for the things a table happens to have.** A desk is a
    /// <see cref="Interaction.SnapSurface"/> and so is anything else that accepts a placed object,
    /// so finding the furniture by looking for one would sweep up whatever else claimed a surface
    /// next — and the failure would be a machine being shuffled like a desk. Naming the thing is
    /// cheaper than guessing at it.
    ///
    /// **It carries nothing.** Everything the layout needs to know is where the desk was authored,
    /// which is on its transform, and that it exists at all, which is this component.
    /// </remarks>
    [DisallowMultipleComponent]
    public class TablePiece : MonoBehaviour
    {
    }
}
