using Overworked.Containers;
using Overworked.Documents;
using Overworked.Visuals;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// Shows what the printer's pile is made of, and feeds the machine's animator.
    /// </summary>
    /// <remarks>
    /// The line between this and the animator is hard, and it is what keeps two people's work
    /// from fighting: the animator decides **how many** sheets are visible, and nothing here
    /// ever touches a slot's active state; this decides **which document each of them is**, and
    /// nothing in the animator touches a slot's contents.
    ///
    /// The whole pile is kept in step, not just the top of it. Slot i always shows the i-th
    /// entry of the output, because that is what the animator means when it turns slots 1..N
    /// on: the sheets that are actually in the pile, in order. Swapping only the top one works
    /// until somebody takes a sheet, after which every sheet below it is showing the wrong
    /// document.
    ///
    /// The two animator parameters are written from here rather than from the printer, because
    /// a server object has no business knowing what the machine looks like:
    ///
    /// - <c>Printing</c> comes from the replicated print state, so both peers animate the same;
    /// - <c>Printed</c> is the output count, which every peer already has.
    ///
    /// Both are sampled every frame rather than subscribed to. Two integer reads and a count
    /// cost nothing, and a poll has no gaps in it: a subscription would miss whatever happened
    /// before this component was enabled, and would miss the initial value of a replicated
    /// field, which never raises a change callback.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PrinterDisplay : MonoBehaviour
    {
        /// <summary>
        /// Animator parameter: 0 when idle, otherwise the pile position being printed.
        /// </summary>
        private static readonly int PrintingHash = Animator.StringToHash("Printing");

        /// <summary>
        /// Animator parameter: how many sheets are in the output.
        /// </summary>
        private static readonly int PrintedHash = Animator.StringToHash("Printed");

        [Header("References")]

        /// <summary>
        /// The machine whose pile is drawn.
        /// </summary>
        [Tooltip("The machine whose pile is drawn. Its own output container is used, never a second reference.")]
        [SerializeField]
        private Printer _printer;

        /// <summary>
        /// Where the document prefabs come from.
        /// </summary>
        [Tooltip("Where the document prefabs come from. Must be the same catalogue the grabbables use.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        /// <summary>
        /// The animator on the machine. Its own controller decides the slot visibility.
        /// </summary>
        [Tooltip("The animator on the machine. Falls back to one on this object or below it.")]
        [SerializeField]
        private Animator _animator;

        [Header("Pile")]

        /// <summary>
        /// The six pile slots, bottom first.
        /// </summary>
        [Tooltip("The six pile slots, bottom first. Slot 1 shows the first entry of the output.")]
        [SerializeField]
        private Transform[] _slots;

        /// <summary>
        /// The sheet on the print head.
        /// </summary>
        [Tooltip("The sheet on the print head, which shows the document currently being printed.")]
        [SerializeField]
        private Transform _printingSheet;

        /// <summary>
        /// Document id currently drawn in each slot, or -1 for the authored placeholder.
        /// </summary>
        private int[] _shown;

        /// <summary>
        /// Document id currently drawn on the print head, or -1.
        /// </summary>
        private int _shownPrinting = -1;

        /// <summary>
        /// True once the animator has been put on the state matching the pile it joined.
        /// </summary>
        private bool _syncedInitialState;

        private void Awake()
        {
            _shown = new int[_slots != null ? _slots.Length : 0];

            for (int i = 0; i < _shown.Length; i++)
                _shown[i] = -1;

            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
        }

        private void Start()
        {
            if (_printer == null || _catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(PrinterDisplay)} on {gameObject.name} has no printer or no catalogue assigned; the pile will not be drawn.",
                    this);
            }

            if (_slots == null || _slots.Length != Printer.PileCapacity)
            {
                Debug.LogWarning(
                    $"{nameof(PrinterDisplay)} on {gameObject.name} has {(_slots == null ? 0 : _slots.Length)} pile slots; the machine's output holds {Printer.PileCapacity}.",
                    this);
            }

            if (_animator == null)
            {
                Debug.LogError(
                    $"{nameof(PrinterDisplay)} on {gameObject.name} found no animator, so nothing will ever appear or disappear.",
                    this);
            }
        }

        private void Update()
        {
            if (_printer == null || _catalogue == null)
                return;

            ContainerBase output = _printer.Output;
            if (output != null)
                SyncSlots(output);

            SyncInitialState(output);
            SyncPrintingSheet();

            if (_animator == null)
                return;

            _animator.SetInteger(PrintingHash, _printer.PrintingSlot);
            _animator.SetInteger(PrintedHash, output != null ? output.Count : 0);
        }

        /// <summary>
        /// Makes every drawn slot show the document that belongs in it.
        /// </summary>
        /// <remarks>
        /// Slots past the end of the output are left as they are. They are hidden by the
        /// animator, and they are corrected by the pass that covers them the moment a sheet
        /// lands in one — so there is nothing to clear and nothing to flicker.
        /// </remarks>
        private void SyncSlots(ContainerBase output)
        {
            int count = Mathf.Min(output.Count, _shown.Length);

            for (int i = 0; i < count; i++)
            {
                if (!output.TryGetEntry(i, out ContainerEntry entry))
                    continue;
                if ((ContainerEntryKind)entry.Kind != ContainerEntryKind.Data)
                    continue;

                /* Keyed on the document, not on its appearance. Two documents printed from the
                 * same template share a payload index and differ only in the number drawn on
                 * them, so comparing payloads would leave the second one showing the first one's
                 * number: the pile would look right and read wrong, which is worse than looking
                 * wrong. */
                if (_slots[i] == null || _shown[i] == entry.DataId)
                    continue;

                /* Only remembered once something was actually put there, so a slot that could
                 * not be filled is retried instead of being written off. */
                if (ReplaceContent(_slots[i], entry.DataId))
                    _shown[i] = entry.DataId;
            }
        }

        /// <summary>
        /// Puts the animator on the state that matches the pile this peer has joined.
        /// </summary>
        /// <remarks>
        /// The controller's default state is Empty, and Empty's only way out is a job starting.
        /// So a peer that arrives with sheets already in the pile would sit on an empty machine
        /// for good, with the documents right there in the container: nothing else in the graph
        /// can reach a non-empty idle state either, because the Finished states are only ever
        /// reached from a Printing state.
        ///
        /// One hard Play, once, before anything is playing — so there is no transition to
        /// interrupt. From then on the state machine drives itself and this never runs again.
        /// Getting only the pile size right is enough: from the Finished state the ordinary
        /// transitions take over, and a job that is already running pulls it on to the right
        /// Printing state by itself.
        ///
        /// It waits for the container to be spawned rather than merely non-null. An object that
        /// exists but has not been synchronised yet reads as an empty pile, and jumping on that
        /// would land the animator on Empty — the exact state this exists to avoid.
        /// </remarks>
        private void SyncInitialState(ContainerBase output)
        {
            if (_syncedInitialState || _animator == null || output == null || !output.IsSpawned)
                return;

            _syncedInitialState = true;

            int count = Mathf.Clamp(output.Count, 0, Printer.PileCapacity);
            _animator.Play(count > 0 ? $"Finish {count}" : "Empty", 0, 0f);
        }

        /// <summary>
        /// Shows the document being printed on the print head.
        /// </summary>
        /// <remarks>
        /// Only while a job is running. Between jobs the head is left exactly as the animation
        /// parked it: it is off screen, and clearing it would only make it pop back into view
        /// empty on the way.
        /// </remarks>
        private void SyncPrintingSheet()
        {
            if (_printingSheet == null)
                return;

            int documentId = _printer.PrintingDocument;
            if (_printer.PrintingSlot == 0 || documentId < 0 || documentId == _shownPrinting)
                return;

            if (ReplaceContent(_printingSheet, documentId))
                _shownPrinting = documentId;
        }

        /// <summary>
        /// Draws a document in a slot, replacing whatever is there.
        /// </summary>
        /// <remarks>
        /// Everything about the sheet is looked up from the document: which template it is
        /// printed on, and what is written on it. What the container holds says only which
        /// document it is, so a store that cannot answer means there is nothing to draw.
        ///
        /// Nothing is resolved before everything is: the document, the template and the label
        /// are all in hand before the slot is cleared. A machine whose catalogue is unfilled, or
        /// whose store has not replicated yet, therefore keeps showing whatever it was showing
        /// instead of emptying the slot — the failure mode of a half-configured prefab should be
        /// the stand-ins the artist authored, not holes.
        /// </remarks>
        /// <returns>True when something was placed in the slot.</returns>
        private bool ReplaceContent(Transform slot, int documentId)
        {
            if (slot == null)
                return false;

            DocumentStore store = DocumentStore.Instance;
            if (store == null || !store.TryGet(documentId, out DocumentRecord document))
                return false;

            GameObject prefab = _catalogue.Get(document.PayloadIndex);
            if (prefab == null)
                return false;

            CosmeticCopy.Clear(slot);

            GameObject content = CosmeticCopy.Instantiate(slot, prefab);
            ApplyLabel(content, document);

            return true;
        }

        /// <summary>
        /// Writes the document's number and team into every label on a drawn copy.
        /// </summary>
        /// <remarks>
        /// The same thing <see cref="Interaction.NetworkGrabbable"/> does for a sheet in
        /// someone's hands, and it has to happen here too: a template on its own is a blank
        /// form, and the number is the whole of what tells one printed document from another.
        ///
        /// A payload with no PayloadLabel — blank paper, an ink cartridge — has nothing to write
        /// into, which is not an error.
        /// </remarks>
        private static void ApplyLabel(GameObject content, in DocumentRecord document)
        {
            foreach (PayloadLabel label in content.GetComponentsInChildren<PayloadLabel>(includeInactive: true))
                label.SetVariant(document.Number, document.Team);
        }
    }
}
