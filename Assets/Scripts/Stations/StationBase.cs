using FishNet.Connection;
using FishNet.Object;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Base for anything a player walks up to and operates.
    /// </summary>
    /// <remarks>
    /// Players never call into a station directly. E is sampled on the player, which owns the
    /// input asset and is always owned by its own client, and PlayerInteraction forwards the
    /// press through a ServerRpc. That indirection is the whole reason this type exists: a
    /// station is a scene object owned by nobody, and a ServerRpc declared on it would be
    /// rejected outright.
    ///
    /// A station supplies the collider the player's sector scan finds, and decides what an
    /// interaction means. Taking from a box, feeding a printer and holding a button down are
    /// the same call with different answers, so nothing about the verb belongs here.
    ///
    /// Handing something IN is not this type's business either. An object arriving at a
    /// machine — paper fed to a printer — gets there by being placed or thrown into the
    /// station's cell, which the station watches for itself.
    /// </remarks>
    public abstract class StationBase : NetworkBehaviour
    {
        /// <summary>
        /// How far from the station a player may stand and still be served.
        /// </summary>
        /// <remarks>
        /// Measured flat, from the server's own copy of the player's root to this transform.
        /// Generous on purpose: the client already decided the player was close enough when it
        /// found the station with its sector scan, and this exists only to stop a modified
        /// client from operating a machine from across the map.
        /// </remarks>
        [Tooltip("How far from this station a player may stand and still be served, in metres.")]
        [Min(0.1f)]
        [SerializeField]
        private float _interactReach = 2.5f;

        /// <summary>
        /// How far from the station a player may stand and still be served.
        /// </summary>
        public float InteractReach => _interactReach;

        /// <summary>
        /// Server: a player interacted with this station.
        /// </summary>
        /// <remarks>
        /// Not virtual, and the guard sits here rather than on the extension point. The weaver
        /// adds a [Server] check by rewriting a method's body, and an abstract method has no body
        /// to rewrite — so the attribute belongs on a concrete method that has one. Overriders
        /// implement <see cref="OnServerInteract"/>, which is only reachable through here.
        /// </remarks>
        /// <param name="player">
        /// The server's copy of the asking player. Use it for the hand position and for anything
        /// else the asking player owns.
        /// </param>
        /// <param name="conn">
        /// The connection that asked. Range has already been checked by the caller.
        /// </param>
        /// <param name="longPress">
        /// True when the press was held past the threshold. Stations that do not care which it
        /// was may ignore it.
        /// </param>
        [Server]
        public void ServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress) =>
            OnServerInteract(player, conn, longPress);

        /// <summary>
        /// Server: does the work. Only ever reached through <see cref="ServerInteract"/>.
        /// </summary>
        protected abstract void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress);

        /// <summary>
        /// Server: puts this station back to how it starts a round.
        /// </summary>
        /// <remarks>
        /// Called once per station when a round is cleared, by whatever clears it — today
        /// <see cref="Match.MatchFlow"/>. **Not the same thing as the round being over**: the round
        /// ends while the office is still standing, and this is what takes the last round's work
        /// back out of it. See <see cref="Match.MatchFlow"/> for why those two are separate.
        ///
        /// The guard sits here rather than on the extension point, for the reason
        /// <see cref="ServerInteract"/> gives — the weaver inserts it by rewriting a method's body,
        /// and an override is only reachable through this.
        /// </remarks>
        [Server]
        public void ServerReset() => OnServerReset();

        /// <summary>
        /// Server: empties this station of the last round. Only ever reached through
        /// <see cref="ServerReset"/>.
        /// </summary>
        /// <remarks>
        /// **An empty body rather than an abstract method**, unlike
        /// <see cref="OnServerInteract"/>. Every station has to answer a press, so making that one
        /// abstract costs nothing; most stations have nothing of their own to put back — a box of
        /// paper is not different after a round than before one — and making this abstract would
        /// have them all write an empty override to say so. A station overrides this when it has
        /// state of its own, and the ones that do not are correct without being asked.
        ///
        /// Note that a container on the station is **not** automatically emptied here. Whether a
        /// container holds the last round's work or is simply stock is a question only the station
        /// can answer — a printer's queue is the former and a supply box's shelf is the latter —
        /// and a base class that guessed would empty the shelf.
        /// </remarks>
        protected virtual void OnServerReset()
        {
        }
    }
}
