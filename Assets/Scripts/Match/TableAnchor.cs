using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Somewhere a table can be put when the office is set up, and somewhere a station may take
    /// instead.
    /// </summary>
    /// <remarks>
    /// **A table anchor is a place that is furnished by default.** A round that puts a station
    /// here has a machine; a round that does not has a desk. That is the whole of the difference
    /// between this and a <see cref="StationAnchor"/>, and it is what lets the office be furnished
    /// to a fixed density while the machines on it move around.
    ///
    /// **Why tables fill the gaps rather than stations being placed sparsely.** An office laid out
    /// by dropping a fixed number of machines into a large room has holes in it, and the holes get
    /// bigger the fewer machines there are — a 1v1 would be played in a warehouse. Filling every
    /// unmachine spot with a desk keeps the room looking like a room at every player count, and it
    /// costs nothing but the desks, which are already in the scene.
    ///
    /// **A station that lands here replaces the desk, and that is not only cosmetic.** A table is a
    /// <see cref="Interaction.SnapSurface"/>, which is what the cleaner reads to decide that
    /// something on it is safe — see <see cref="Npc.Cleaner"/>. So a table anchor taken by a
    /// station is a safe spot that stops existing, and an office with more machines in it is an
    /// office with fewer places to put something down. That is a real change to how the round
    /// plays, and it is meant to be.
    /// </remarks>
    [DisallowMultipleComponent]
    public class TableAnchor : MonoBehaviour
    {
        /// <summary>
        /// Where a table put here would stand.
        /// </summary>
        public Vector3 Position => transform.position;

        /// <summary>
        /// Which way a table put here would face.
        /// </summary>
        public Quaternion Rotation => transform.rotation;
    }
}
