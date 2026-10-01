using FishNet.Connection;
using FishNet.Object;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.UI;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Where a player chooses a document to have printed.
    /// </summary>
    /// <remarks>
    /// This is data acquisition, not printing. The machine hands nothing to anybody and spawns
    /// nothing: pressing E asks the server to open this client's panel, and every choice is made
    /// there and sent back. Keeping the choice out of the station is what lets the panel be a
    /// purely local thing — built in code, holding no replicated state, one per client.
    ///
    /// It reaches printers that are nowhere near it, and that is the point. The cost the game
    /// charges for a document is fetch-and-carry; making the player stand at the machine while it
    /// prints would delete that cost. So the only range that means anything is the one back to the
    /// computer the player is standing at — never the one to the printer they picked.
    ///
    /// Refusals are silent. A full queue, a printer that has since gone away, and a document index
    /// that does not exist all end the same way: nothing happens. Each is a normal state for the
    /// player to be in, and a machine that announced them would be reporting its own bookkeeping.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Computer : StationBase
    {
        [Header("Documents")]

        /// <summary>
        /// The documents this computer offers.
        /// </summary>
        /// <remarks>
        /// A catalogue of specifications, so the panel can list what is available without anything
        /// having been created. Picking one is what makes a document; see
        /// <see cref="DocumentStore.ServerCreate"/>.
        /// </remarks>
        [Tooltip("The documents this computer offers. Assign the DocumentCatalogue asset.")]
        [SerializeField]
        private DocumentCatalogue _catalogue;

        /// <summary>
        /// The panel that opens on a client when this computer is used.
        /// </summary>
        private ComputerPanel _panel;

        /// <summary>
        /// The documents this computer offers.
        /// </summary>
        public DocumentCatalogue Catalogue => _catalogue;

        /// <summary>
        /// The panel belonging to this computer, or null when the prefab has none.
        /// </summary>
        /// <remarks>
        /// Resolved on demand rather than in Awake. A NetworkBehaviour's Awake is rewritten by the
        /// weaver to run its own initialisation around the user's, and there is no reason to put a
        /// component lookup inside that. This is a lookup on one object, once, on the first press.
        /// </remarks>
        public ComputerPanel Panel
        {
            get
            {
                if (_panel == null)
                    _panel = GetComponentInChildren<ComputerPanel>(includeInactive: true);

                return _panel;
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Both of these are wiring mistakes whose only symptom is a machine that does nothing,
             * which is indistinguishable from a machine that is working and simply not wanted yet. */
            if (_catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(Computer)} on {gameObject.name} has no {nameof(DocumentCatalogue)} assigned; pressing E on it will do nothing.",
                    this);
            }

            if (Panel == null)
            {
                Debug.LogError(
                    $"{nameof(Computer)} on {gameObject.name} has no {nameof(ComputerPanel)} in its children; the panel it opens will never appear.",
                    this);
            }
        }

        /// <summary>
        /// Server: tells the asking client to open its panel.
        /// </summary>
        /// <remarks>
        /// Nothing is created and nothing is decided here. The station's whole part in the
        /// transaction is to be the thing the player pressed E on, and — later, when the choice
        /// comes back — to be the point the range is measured from.
        ///
        /// The press length is ignored: there is one verb, and how long the key was held does not
        /// change what it means.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)
        {
            if (player == null || conn == null)
                return;

            /* An empty catalogue would open a panel with nothing on it. Leaving the press
             * unanswered is the better failure: no window appears, and there is nothing to misread
             * as the machine being broken. */
            if (_catalogue == null || _catalogue.Count == 0)
                return;

            player.ServerOpenComputerPanel(this);
        }
    }
}
