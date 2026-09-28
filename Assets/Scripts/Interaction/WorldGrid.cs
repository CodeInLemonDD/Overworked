using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// World-space square grid used for snapping placed objects.
    /// </summary>
    /// <remarks>
    /// Deliberately a static class with a const cell size rather than an inspector field:
    /// the client and the server must never be able to disagree about where a cell is.
    /// If the size ever needs to become authoritative, promote it to a Synced value on the
    /// NetworkManager and read it from here.
    ///
    /// Tables are authored so that their root XZ sits on a cell centre, which puts their
    /// top surface centred on the cell and makes the snap land where the player expects.
    /// </remarks>
    public static class WorldGrid
    {
        /// <summary>
        /// Size of one cell, in world units.
        /// </summary>
        public const float CellSize = 1f;

        /// <summary>
        /// Returns the cell containing the given world XZ.
        /// </summary>
        public static Vector2Int CellCoord(Vector2 worldXZ) => new(
            Mathf.FloorToInt(worldXZ.x / CellSize),
            Mathf.FloorToInt(worldXZ.y / CellSize));

        /// <summary>
        /// Returns the centre of the cell containing the given world XZ, as XZ.
        /// </summary>
        public static Vector2 CellCentreXZ(Vector2 worldXZ)
        {
            Vector2Int cell = CellCoord(worldXZ);
            return new Vector2((cell.x + 0.5f) * CellSize, (cell.y + 0.5f) * CellSize);
        }

        /// <summary>
        /// Returns the centre of a cell, with Y left at zero for the caller to fill in.
        /// </summary>
        public static Vector3 CellCentre(Vector2Int cell) => new(
            (cell.x + 0.5f) * CellSize,
            0f,
            (cell.y + 0.5f) * CellSize);
    }
}
