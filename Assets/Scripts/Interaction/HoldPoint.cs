using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// Marks the transform that a carried object is driven to, and is used as the origin for
    /// pickup detection and throw direction.
    /// </summary>
    /// <remarks>
    /// This must live under the player's smoothed graphical node, not on the player root.
    /// The root is written only inside the movement script's [Replicate] at the tick rate, so
    /// anything parented to it steps and jumps during rollbacks; the graphical node is
    /// advanced every rendered frame. See PlayerInteraction for how it is read.
    /// </remarks>
    [DisallowMultipleComponent]
    public class HoldPoint : MonoBehaviour
    {
    }
}
