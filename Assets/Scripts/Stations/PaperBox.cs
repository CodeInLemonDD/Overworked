using FishNet;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using Overworked.Containers;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// A free source of raw material that restocks itself, slowly.
    /// </summary>
    /// <remarks>
    /// Material here is bought with time, not money. There is no price, and no way to use the box
    /// up for good: leave it alone and it fills back, so a player who needs paper is delayed
    /// rather than stuck. That is the whole point of the arrangement — the real cost of a sheet is
    /// the walk to fetch it, and a cost measured in seconds cannot deadlock while a cost measured
    /// in something spendable can. The score is a monotonic total that is never spent, for the
    /// same reason: a score you could spend would be a second currency, and the game would be able
    /// to reach a state where nobody can act.
    ///
    /// A station with its own table, placed in the scene as one piece. The root carries a
    /// ContainerBase holding the stock, a PlacementBlocker so nothing may be placed in the cell
    /// the machine stands in, and the collider the player's sector scan finds.
    ///
    /// The stock is entries, never objects. A sheet does not exist until somebody asks for one,
    /// which is what keeps the box invisible to the grid, to placement probes and to the cleaner.
    /// What comes out is a real object made by <see cref="GrabbableSpawner"/>, like every other
    /// object in the game, and handed straight into the hands of whoever asked.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PaperBox : StationBase
    {
        [Header("Stock")]

        /// <summary>
        /// The container holding the box's stock.
        /// </summary>
        /// <remarks>
        /// On this object's root. Its capacity is the buffer: a box nobody has touched for a while
        /// holds that many sheets, and the restock rate is what a player drains past.
        /// </remarks>
        [Tooltip("Container on this object's root holding the box's stock. Its capacity is how many sheets can be waiting.")]
        [SerializeField]
        private ContainerBase _container;

        /// <summary>
        /// Which payload a sheet is.
        /// </summary>
        /// <remarks>
        /// Resolved through the same catalogue everything else uses, so the box never holds a
        /// prefab reference of its own — the array order is the shared contract, and this is only
        /// an index into it. That matters more here than anywhere else: whatever index the printer
        /// treats as paper has to be the index this box hands out, and the catalogue is the only
        /// place that can be agreed.
        ///
        /// The default is -1, which leaves the grabbable prefab exactly as authored, so the box
        /// works before a catalogue exists at all. Set a real index once one does.
        /// </remarks>
        [Tooltip("Index into the payload catalogue for one sheet. -1 hands out the bare prefab; set this to the same index the printer reads as paper.")]
        [Min(-1)]
        [SerializeField]
        private int _payloadIndex = -1;

        /// <summary>
        /// Used only to check the index above at startup.
        /// </summary>
        /// <remarks>
        /// Required as soon as <see cref="_payloadIndex"/> names anything, and used for nothing
        /// else. An index nothing can resolve does not fail anywhere visible — the object is still
        /// made, just wearing the prefab's original look — so without this check a mis-set index
        /// reads as "the box hands out a featureless cube" rather than as a wiring mistake.
        ///
        /// Optional only on the -1 path, where "the prefab exactly as authored" is the answer the
        /// index already gives and there is nothing to look up.
        /// </remarks>
        [Tooltip("Required when the payload index above names anything. Only used to check that index at startup.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        [Header("Restock")]

        /// <summary>
        /// Seconds between restocks.
        /// </summary>
        /// <remarks>
        /// The one number that sets what a sheet costs, and the price is paid in walking. Long on
        /// purpose: a box that kept up with a player standing at it would make fetching pointless,
        /// and fetching is the loop.
        /// </remarks>
        [Tooltip("Seconds between restocks. The price of a sheet, paid in walking.")]
        [Min(0.01f)]
        [SerializeField]
        private float _refillSeconds = 8f;

        /// <summary>
        /// TimeManager subscribed to, kept so the timer can be detached from the same object.
        /// </summary>
        /// <remarks>
        /// Cached rather than read again in OnStopNetwork, matching what TickNetworkBehaviour
        /// does: holding the reference that was subscribed with is the safer half of the pair.
        /// </remarks>
        private TimeManager _timeManager;

        /// <summary>
        /// Seconds accumulated towards the next restock.
        /// </summary>
        private float _refillTimer;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            /* StationBase derives from NetworkBehaviour, so the tick-callback template that would
             * normally relay this is not available here. Subscribing directly is what that
             * template does anyway, and it is what keeps this off Update(): the restock is a
             * server-side timer and has no business running at a rate of its own. */
            _timeManager = TimeManager;
            if (_timeManager != null)
                _timeManager.OnUpdate += TimeManager_OnUpdate;
        }

        public override void OnStopNetwork()
        {
            if (_timeManager != null)
            {
                _timeManager.OnUpdate -= TimeManager_OnUpdate;
                _timeManager = null;
            }

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* A pooled object keeps its fields across spawns, and a scene object starts again on
             * a later session. Neither should inherit the previous run's countdown. */
            _refillTimer = 0f;

            if (_container == null)
            {
                Debug.LogError($"{nameof(PaperBox)} on {gameObject.name} has no {nameof(ContainerBase)} assigned; it has nothing to hand out.", this);
                return;
            }

            /* Split in two so the likelier mistake gets its own message. Pointing at an index with
             * no catalogue to resolve it against used to pass in silence — nothing was checked at
             * all — and that is the one case where the box hands out bare prefabs while looking
             * correctly configured in the Inspector. */
            if (_payloadIndex >= 0 && _catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(PaperBox)} on {gameObject.name} names payload index {_payloadIndex} but has no " +
                    $"{nameof(PayloadCatalogue)} assigned, so nothing can resolve it; it will hand out unmodified " +
                    "objects. Assign the same catalogue everything else uses, or set the index to -1 if bare " +
                    "prefabs are what you want.",
                    this);
            }
            else if (_payloadIndex >= 0 && !_catalogue.TryGet(_payloadIndex, out _))
            {
                Debug.LogError(
                    $"{nameof(PaperBox)} on {gameObject.name} points at payload index {_payloadIndex}, which the " +
                    "assigned catalogue does not define; it will hand out unmodified objects.",
                    this);
            }

            FillStock();
        }

        /// <summary>
        /// Server: fills the box to its capacity.
        /// </summary>
        /// <remarks>
        /// Starting full rather than empty is what makes the first sheet free of a wait. A box
        /// that began empty would look broken for the first restock interval, which is exactly
        /// when a player is most likely to walk up and try it.
        /// </remarks>
        private void FillStock()
        {
            /* An unlimited container has no cap to fill towards, so there is nothing to restock
             * into: the loop below would not run and the box would start empty, which is the exact
             * state this method exists to avoid. Filling to an invented number would be inventing a
             * rule, so the mistake is reported and the box carries on as it is.
             *
             * Warned rather than errored, matching the printer: the box still works, and what is
             * lost is the material constraint the restock rate is built around. Not left silent,
             * because capacity zero is ContainerBase's default — a ContainerBase added without its
             * capacity filled in lands here, and an unbounded box then grows its list without limit
             * for every client that joins later. */
            if (_container.IsUnlimited)
            {
                Debug.LogWarning(
                    $"{nameof(PaperBox)} on {gameObject.name} has an unlimited container, so it has no buffer to " +
                    "restock into and starts empty. Set its capacity to the number of sheets the box should hold " +
                    "(a small one — the walk is meant to be the price of a sheet).",
                    this);
                return;
            }

            for (int i = 0; i < _container.Capacity; i++)
            {
                if (!_container.ServerTryAdd(ContainerEntry.ForEntity(_payloadIndex)))
                    break;
            }
        }

        /// <summary>
        /// Server: the restock timer.
        /// </summary>
        /// <remarks>
        /// Unscaled, per the project's rule for server-side timers: this is a wall-clock
        /// production rate rather than a physics quantity, and only the server ever writes to the
        /// container, so the rate is the same on every peer by construction.
        /// </remarks>
        private void TimeManager_OnUpdate()
        {
            if (!IsServerInitialized || _container == null)
                return;

            /* A full box has nothing to produce, and the countdown starts only once there is room.
             * Accumulating while full would make the wait disappear: leave a box alone, take the
             * top sheet, and the next one would appear on the very next frame — which is the same
             * as having no restock time at all, and would leave the box keeping up with a player
             * standing at it. */
            if (_container.IsFull)
            {
                _refillTimer = 0f;
                return;
            }

            _refillTimer += Time.unscaledDeltaTime;
            if (_refillTimer < _refillSeconds)
                return;

            /* Reset only on a successful add. A failed one leaves the timer elapsed so the next
             * frame tries again, which is the right answer if the container filled up from
             * somewhere else in between. */
            if (_container.ServerTryAdd(ContainerEntry.ForEntity(_payloadIndex)))
                _refillTimer = 0f;
        }

        /// <summary>
        /// Server: hands one sheet to the player who asked, if their hands are free.
        /// </summary>
        /// <remarks>
        /// Both refusals are silent. A player with full hands and a player at an empty box are
        /// each in a normal state, and the machine has nothing useful to say about either beyond
        /// not doing anything.
        ///
        /// The length of the press is ignored: this box has exactly one verb, so a held press is
        /// not a different request, and answering it with nothing would read as the station having
        /// stopped responding.
        ///
        /// Nobody owns the stock, and nothing downstream will either: paper is a shared pool,
        /// the same sheet whoever carried it to the machine, and feeding one in scores nothing.
        /// Ownership is a property of a finished document, which arrives from the computer
        /// already carrying a faction — not of the material that goes into the machine.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)
        {
            if (player == null || conn == null || _container == null)
                return;

            if (NetworkGrabbable.IsHeldBy(InstanceFinder.NetworkManager, conn.ClientId))
                return;

            if (_container.Count == 0)
                return;

            NetworkObject nob = GrabbableSpawner.SpawnGrabbable(_payloadIndex, player.HandPosition, Quaternion.identity, conn);
            if (nob == null)
                return;

            /* Spent only once the object exists, so a spawn that failed for any reason leaves the
             * stock where it was instead of quietly costing a sheet. */
            _container.ServerTryRemoveLast();

            /* Marked held as well as handed over. See ServerHandToPlayer: "are the hands empty"
             * is answered from the object's own replicated state, and a freshly spawned object
             * is Idle with no holder. */
            player.ServerHandToPlayer(nob);
        }
    }
}
