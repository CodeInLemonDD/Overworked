using System.Collections.Generic;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.Player;
using Overworked.Stations;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// The window a player works a computer through: pick a document, pick a printer.
    /// </summary>
    /// <remarks>
    /// The whole hierarchy is built in code — canvas, panel, rows — rather than authored into a
    /// prefab, for the same reason the debug HUD is: scenes and prefabs are YAML and cannot be
    /// merged, so anything authored has to be done by one pair of hands in one sitting. Building
    /// it in code means the prefab needs one component and nothing else.
    ///
    /// **This one carries a GraphicRaycaster, unlike every other panel in the project.** The rule
    /// against it exists to keep the FishNet demo's Host and Client buttons in the top-left
    /// clickable, because MPPM testing cannot start without them. That is a rule about overlays
    /// that cover the screen, and this is not one: it is a window on the right-hand side, it never
    /// covers the corner those buttons live in, and it draws no full-screen backdrop to swallow
    /// clicks on the way past. The rule's reason is kept even though its letter is not. See
    /// CONSTRAINTS.md convention C before moving this anywhere near the top-left.
    ///
    /// It is a client-side object with no network presence. The server's part is one message
    /// telling a client to open it and one message back carrying the choice; nothing about the
    /// window itself is replicated, and each client builds its own.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ComputerPanel : MonoBehaviour
    {
        #region Configuration.
        /// <summary>
        /// Font for every label on the panel.
        /// </summary>
        [Tooltip("Font for every label. Assign Assets/Font/simhei SDF.asset — the built-in TMP font has no Chinese glyphs.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Sorting order given to the created canvas.
        /// </summary>
        [Tooltip("Sorting order of the created canvas. Must stay above the FishNet demo UI, which sits at 0.")]
        [SerializeField]
        private int _sortingOrder = 10;
        #endregion

        #region Layout constants.
        /// <summary>
        /// Reference resolution the panel is scaled against.
        /// </summary>
        private static readonly Vector2 ReferenceResolution = new(1920f, 1080f);

        /// <summary>
        /// Width of the window.
        /// </summary>
        private const float PanelWidth = 780f;

        /// <summary>
        /// Height of the window.
        /// </summary>
        private const float PanelHeight = 640f;

        /// <summary>
        /// Gap between the window and the screen edge.
        /// </summary>
        private const float Margin = 28f;

        /// <summary>
        /// Padding inside the window.
        /// </summary>
        private const float Padding = 22f;

        /// <summary>
        /// Gap between the two columns.
        /// </summary>
        private const float ColumnGap = 18f;

        /// <summary>
        /// Height of a list row.
        /// </summary>
        private const float RowHeight = 46f;

        /// <summary>
        /// Gap between rows.
        /// </summary>
        private const float RowGap = 8f;

        /// <summary>
        /// Space between the title and the first list header.
        /// </summary>
        private const float TitleHeight = 46f;

        /// <summary>
        /// Height of a column header.
        /// </summary>
        private const float HeaderHeight = 30f;

        /// <summary>
        /// Height of the row that names a kind.
        /// </summary>
        private const float KindRowHeight = 30f;

        /// <summary>
        /// Extra space left under a kind's documents, so the next kind reads as a new group rather
        /// than as one more row.
        /// </summary>
        private const float KindGap = 12f;

        /// <summary>
        /// How far a kind row is indented from the column edge.
        /// </summary>
        private const float KindIndent = 8f;

        /// <summary>
        /// How far a document row is indented under its kind.
        /// </summary>
        /// <remarks>
        /// The indentation is the only thing saying a document belongs to the kind above it.
        /// Without it the two levels are one flat list with captions lost inside it, and the
        /// difference between "Excel" and "Excel 1" stops being visible at all.
        /// </remarks>
        private const float RowIndent = 34f;

        /// <summary>
        /// Left inset for the text of any row, kind or document.
        /// </summary>
        private const float LabelInset = 14f;

        /// <summary>
        /// Right inset for the tag that ends a row.
        /// </summary>
        private const float TagInset = 12f;

        /// <summary>
        /// Width reserved at the end of a row for that tag.
        /// </summary>
        /// <remarks>
        /// Fixed rather than measured from the text. Both the name and the tag are laid out against
        /// it, so a long kind name ellipsizes instead of pushing the tag off the row.
        /// </remarks>
        private const float TagWidth = 46f;

        /// <summary>
        /// Font size for body rows.
        /// </summary>
        private const float RowFontSize = 22f;

        /// <summary>
        /// Font size for the row that names a kind.
        /// </summary>
        private const float KindFontSize = 20f;

        /// <summary>
        /// Font size for headers and the title.
        /// </summary>
        private const float HeadingFontSize = 26f;

        /// <summary>
        /// Font size for the footer hint.
        /// </summary>
        private const float HintFontSize = 18f;

        /// <summary>
        /// What the document column says when there is nothing in it.
        /// </summary>
        /// <remarks>
        /// The column is a list of what this team has been handed, so it is empty until somebody
        /// takes a job — a state that has to say what to do about it. A blank column reads as a
        /// broken panel, and what to do about it is not guessable from a computer: the paperwork
        /// comes from a customer.
        /// </remarks>
        private const string EmptyDocumentsHint = "(还没有拿到任何文件 —— 去找客户接单)";

        /// <summary>
        /// Background of a row that is not chosen.
        /// </summary>
        private static readonly Color RowColour = new(0.17f, 0.18f, 0.22f, 0.95f);

        /// <summary>
        /// Background of the document that is chosen.
        /// </summary>
        private static readonly Color RowChosenColour = new(0.20f, 0.42f, 0.62f, 0.95f);

        /// <summary>
        /// Background of a row that cannot be pressed yet.
        /// </summary>
        /// <remarks>
        /// A darker step of <see cref="RowColour"/> rather than a tint or a fade, and the text is
        /// left alone: a locked document has to stay as readable as an open one. The point of
        /// showing it at all is that the player learns there is something they have not found.
        /// </remarks>
        private static readonly Color RowLockedColour = new(0.11f, 0.12f, 0.145f, 0.95f);

        /// <summary>
        /// Colour of the row that names a kind.
        /// </summary>
        private static readonly Color KindColour = new(0.62f, 0.67f, 0.76f, 1f);

        /// <summary>
        /// Window background.
        /// </summary>
        private static readonly Color PanelColour = new(0.09f, 0.10f, 0.13f, 0.94f);
        #endregion

        #region Runtime.
        /// <summary>
        /// The canvas, or null until the panel is first opened.
        /// </summary>
        private GameObject _canvasObject;

        /// <summary>
        /// The window itself, and the parent of everything that is not part of the list.
        /// </summary>
        private RectTransform _panelRect;

        /// <summary>
        /// Where the document rows go: the scrolling part of the left column.
        /// </summary>
        /// <remarks>
        /// A row's y is measured from the top of this rather than from the top of the window, and
        /// its height on each rebuild is what tells that column's scroll bar how far there is to
        /// go. The window's own furniture — title, headings, hint, close button — is deliberately
        /// not under either of these; see <see cref="Build"/>.
        /// </remarks>
        private RectTransform _documentContent;

        /// <summary>
        /// Where the printer rows go: the scrolling part of the right column.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="_documentContent"/> so the two lists scroll apart. A job is
        /// "this document, that machine", and one shared area carries the machines out of reach as
        /// soon as the documents are longer than the window.
        /// </remarks>
        private RectTransform _printerContent;

        /// <summary>
        /// Rows built on the last rebuild, so they can be thrown away.
        /// </summary>
        /// <remarks>
        /// List rows only. The title, the headers, the hint and the close button are made once and
        /// are deliberately **not** registered here — a rebuild that cleared them would take the
        /// window's own furniture with it and leave a box of rows with nothing to say what it is.
        /// </remarks>
        private readonly List<GameObject> _rows = new();

        /// <summary>
        /// The document rows, in catalogue order.
        /// </summary>
        private readonly List<DocumentRow> _documentRows = new();

        /// <summary>
        /// When this client first saw each document start being fetched, by document id.
        /// </summary>
        /// <remarks>
        /// Local, and deliberately so. The server publishes **that** a fetch is running and never
        /// how far along it is: a countdown written to the wire ten times a second would spend
        /// most of this project's bandwidth budget redrawing a progress bar. The duration is in
        /// the catalogue, which every peer already holds, so each client runs the clock itself and
        /// draws a bar that can be a network round-trip out and still be worth looking at.
        ///
        /// The cost is that opening the window halfway through a fetch starts its bar from the
        /// top, and that a document can land a moment before its bar empties. Both are cosmetic,
        /// and both are the right trade against syncing a number nothing acts on.
        /// </remarks>
        private readonly Dictionary<int, float> _fetchStartedAt = new();

        /// <summary>
        /// Scratch list for <see cref="PruneFetchClocks"/>, reused so a rebuild allocates nothing.
        /// </summary>
        private readonly List<int> _pruneBuffer = new();

        /// <summary>
        /// The document ids, in the order the column lists them.
        /// </summary>
        /// <remarks>
        /// Sorted once per rebuild rather than walked in store order, because the store is in the
        /// order documents were named and the column is in the order kinds are catalogued. Sizing
        /// this by hand at a few dozen entries is not worth a second structure; the sort is what
        /// turns "which kind comes first" into a question the ids already answer.
        /// </remarks>
        private readonly List<int> _documentOrder = new();

        /// <summary>
        /// One row of the document column.
        /// </summary>
        /// <remarks>
        /// A record rather than three lists kept the same length, because they are only ever
        /// right together and a row that is fetching has no business lighting up as the chosen
        /// document — so which row means which document, and whether it can be picked at all,
        /// to travel with the row.
        /// </remarks>
        private sealed class DocumentRow
        {
            public Button Button;
            public Image Background;
            public TextMeshProUGUI Label;
            public int DocumentId;
            public bool Selectable;

            /// <summary>
            /// What the background goes back to when this row is not the chosen one.
            /// </summary>
            /// <remarks>
            /// Carried on the row rather than recomputed, because <see cref="Choose"/> repaints
            /// every row in the column and only the row knows which of the two ordinary colours it
            /// is entitled to. Without it, the first click anywhere lights up every row that is
            /// not on offer — and the one thing a locked row has to look like is locked.
            /// </remarks>
            public Color BaseColour;
        }

        /// <summary>
        /// Printers found on the last rebuild, in the order they are listed.
        /// </summary>
        private readonly List<Printer> _printers = new();

        /// <summary>
        /// The player interaction this panel sends through, or null while closed.
        /// </summary>
        private PlayerInteraction _interaction;

        /// <summary>
        /// The computer this panel belongs to, or null while closed.
        /// </summary>
        private Computer _computer;

        /// <summary>
        /// The unlocks this panel is following, or null while closed — and null for good on a
        /// scene that has none.
        /// </summary>
        /// <remarks>
        /// Held rather than read back from <see cref="DocumentUnlocks.Instance"/> when unsubscribing,
        /// for the same reason the computer is: the static can have been cleared by the time the
        /// window closes, and a subscription that cannot be found again is one that stays attached
        /// to a panel that no longer belongs to it.
        /// </remarks>
        private DocumentUnlocks _unlocks;

        /// <summary>
        /// The team this panel's player is on.
        /// </summary>
        /// <remarks>
        /// Cached when the window opens rather than read per row, for the same reason the movement
        /// and stamina components are: by the time a row is built, the answer has to be something
        /// this window already holds rather than something it goes looking for.
        ///
        /// Every document belongs to a team and only that team may print it, so this is what tells
        /// the panel which of the store's documents are its business.
        /// </remarks>
        private int _team;

        /// <summary>
        /// The movement component on the same player, cached so input can be handed back.
        /// </summary>
        private PlayerMovementPrediction _movement;

        /// <summary>
        /// The stamina component on the same player, cached so input can be handed back.
        /// </summary>
        private PlayerStamina _stamina;

        /// <summary>
        /// Index of the chosen document, or -1 when there is nothing to choose.
        /// </summary>
        private int _chosenDocument = -1;

        /// <summary>
        /// Whether the panel is on screen.
        /// </summary>
        private bool _open;
        #endregion

        /// <summary>
        /// Whether the panel is currently on screen.
        /// </summary>
        public bool IsOpen => _open;

        /// <summary>
        /// Client: opens the panel for a computer this player just used.
        /// </summary>
        /// <param name="interaction">
        /// The local player's interaction component. Handed in by the RPC that opened this panel,
        /// which runs on that very component — so the panel never has to search for the player it
        /// belongs to.
        /// </param>
        public void Show(PlayerInteraction interaction, Computer computer)
        {
            if (interaction == null || computer == null)
                return;

            /* Building the canvas under an inactive parent produces a perfectly good window that
             * nobody can see, and nothing else would say so. */
            if (!gameObject.activeInHierarchy)
            {
                Debug.LogError(
                    $"{nameof(ComputerPanel)} on {gameObject.name} is inactive; the panel cannot be drawn. " +
                    "It has to sit on the computer prefab's root, or on a child that is never switched off.",
                    this);
                return;
            }

            _interaction = interaction;
            _computer = computer;

            /* Followed rather than sampled. Two things can change what the document column may
             * offer while the window is open: a fetch starting or landing, and a document being
             * up. Both have to redraw it, and both go through the one handler — the work they need
             * is identical, and a second copy of it would be a second place for the selection to be
             * forgotten. */
            _computer.FetchingChanged += OnOfferChanged;

            _unlocks = DocumentUnlocks.Instance;
            if (_unlocks != null)
                _unlocks.UnlockedChanged += OnOfferChanged;

            /* Cached now rather than looked up when the panel closes: by then the player object may
             * be gone, and leaving input switched off on a player that is still alive is the worst
             * way to find out. */
            _movement = interaction.GetComponent<PlayerMovementPrediction>();
            _stamina = interaction.GetComponent<PlayerStamina>();

            /* Which side of the round this window belongs to. Read from the player rather than
             * asked of the server, because it is the same answer on every peer and there is
             * nothing to disagree about — and the server checks the team again where it matters,
             * when a document is actually sent to a printer. */
            _team = interaction.Team;

            Build();
            Rebuild();

            /* A document is chosen before the panel is shown, so the first printer row a player
             * clicks always has something to send. Landing on an empty choice would make the first
             * click do nothing, which reads as the window being broken. A row that is already
             * waiting is passed over for the same reason — it cannot be sent, so it cannot be what
             * the first click spends. */
            Choose(FirstSelectableDocument());

            _canvasObject.SetActive(true);
            SetGameplayInputEnabled(false);
            _open = true;
        }

        /// <summary>
        /// Client: closes the panel and hands input back.
        /// </summary>
        public void Hide()
        {
            if (!_open)
                return;

            _open = false;

            /* Before the references are dropped, and unconditionally: a panel left subscribed to a
             * machine it no longer belongs to would rebuild another window's rows. */
            if (_computer != null)
                _computer.FetchingChanged -= OnOfferChanged;

            if (_unlocks != null)
                _unlocks.UnlockedChanged -= OnOfferChanged;

            _fetchStartedAt.Clear();

            if (_canvasObject != null)
                _canvasObject.SetActive(false);

            /* Before the references are dropped, and unconditionally: whatever else went wrong,
             * the player must not be left standing still with nothing on screen to explain it. */
            SetGameplayInputEnabled(true);

            _interaction = null;
            _computer = null;
            _unlocks = null;
            _movement = null;
            _stamina = null;
        }

        private void OnDisable()
        {
            /* The panel can be switched off while it is open — the prefab is deactivated, or the
             * scene is being torn down. Either way the input has to go back. */
            Hide();
        }

        private void Update()
        {
            if (!_open)
                return;

            /* The player or the machine going away closes the window. Neither can happen through
             * normal play — input is off, so the player cannot walk off — but a disconnect or a
             * scene teardown can, and a panel left floating over a dead world is worse than one
             * that closes itself. */
            if (_interaction == null || _computer == null)
            {
                Hide();
                return;
            }

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Hide();
                return;
            }

            RefreshFetchLabels();
        }

        // ------------------------------------------------------------------ building

        /// <summary>
        /// Creates the canvas and the window, once.
        /// </summary>
        private void Build()
        {
            if (_canvasObject != null)
                return;

            _canvasObject = new GameObject("Computer Panel", typeof(RectTransform));
            _canvasObject.transform.SetParent(transform, worldPositionStays: false);

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Mathf.Max(1, _sortingOrder);

            /* The one GraphicRaycaster in the project that is deliberate. See the class remarks:
             * it is what lets the rows be ordinary buttons, and it is safe here only because the
             * window never covers the demo's buttons and draws no backdrop under them. */
            _canvasObject.AddComponent<GraphicRaycaster>();

            CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            GameObject panelObject = new("Window", typeof(RectTransform));
            panelObject.transform.SetParent(_canvasObject.transform, worldPositionStays: false);

            _panelRect = (RectTransform)panelObject.transform;

            /* Right-hand side, centred vertically. The top-left corner is left alone on purpose —
             * that is where the FishNet demo keeps the buttons MPPM testing needs. */
            _panelRect.anchorMin = new Vector2(1f, 0.5f);
            _panelRect.anchorMax = new Vector2(1f, 0.5f);
            _panelRect.pivot = new Vector2(1f, 0.5f);
            _panelRect.anchoredPosition = new Vector2(-Margin, 0f);
            _panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            Image background = panelObject.AddComponent<Image>();
            background.color = PanelColour;

            /* Deliberately a raycast target. Nothing sits behind the window, so this is not here to
             * block anything — it is here so that a click on the window's own body is swallowed by
             * the window rather than falling through to whatever is underneath. */
            background.raycastTarget = true;

            /* Two lists, two scrollbars.
             *
             * They scroll because a list only grows: a document is never removed from the round —
             * ids stay valid for as long as anything might be holding one — so a panel that drew
             * them all ran off the bottom of the screen after a few jobs and stayed there.
             *
             * **Separately, and that is the point rather than a detail.** One shared area carries
             * the printer buttons out of reach the moment the document list is longer than the
             * window, and the printers are the half the player is reaching for: a job is "this
             * document, that machine", and having to scroll back to find the second half of the
             * sentence is what made a two-document job feel like two visits. A level with eight
             * printers beside thirty documents could not be worked at all with one bar.
             *
             * The title, the two headings, the hint and the close button are laid out against the
             * window instead. The close button especially: a window whose only way out can be
             * scrolled out of reach is a window that traps whoever opened it. */
            float hintHeight = HintFontSize * 1.6f;

            float columnWidth = (PanelWidth - Padding * 2f - ColumnGap) * 0.5f;
            float rightColumnX = Padding * 2f + ColumnGap * 0.5f + columnWidth;

            /* Both lists stop above the hint rather than at the padding line, so the line telling
             * the player what to do is never the first thing covered. */
            float listTop = TitleHeight + HeaderHeight;
            float listHeight = PanelHeight - listTop - Padding - hintHeight;

            _documentContent = CreateColumn("Documents", Padding, listTop, columnWidth, listHeight);
            _printerContent = CreateColumn("Printers", rightColumnX, listTop, columnWidth, listHeight);

            /* Said once, at build, rather than from every text: the symptom of a missing font asset
             * is a window full of empty boxes, and one line naming the asset to assign is worth
             * more than thirty lines saying the same thing. */
            if (_font == null)
            {
                Debug.LogError(
                    $"{nameof(ComputerPanel)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the panel will fall back to the built-in font, which draws no Chinese. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }

            AddLabel("Title", Padding, 0f, PanelWidth - Padding * 2f, TitleHeight, "电脑", HeadingFontSize);
            AddLabel("Documents", Padding, TitleHeight, columnWidth, HeaderHeight, "文档", HeadingFontSize);
            AddLabel("Printers", rightColumnX, TitleHeight, columnWidth, HeaderHeight, "打印机", HeadingFontSize);

            AddLabel(
                "Hint",
                Padding,
                PanelHeight - Padding - HintFontSize * 1.6f,
                PanelWidth - Padding * 2f,
                HintFontSize * 1.6f,
                "选一份文档,再点一台打印机送进它的队列。Esc 关闭。",
                HintFontSize);

            /* Built last so it draws over the body, and placed clear of the two columns. */
            CreateButton(
                _panelRect,
                "Close",
                PanelWidth - Padding - 110f,
                TitleHeight + 4f,
                110f,
                HeaderHeight + 8f,
                "关闭",
                RowFontSize,
                Hide);
        }

        /// <summary>
        /// Builds one scrolling list: a column, a viewport that clips it, and the content rows go in.
        /// </summary>
        /// <remarks>
        /// Called twice, once per side of the window, and the two are identical apart from where
        /// they sit. That is the point: the printer column is not a special case of the document
        /// column, it is the same thing with different rows in it, and a level with more printers
        /// than fit is handled by the same scrollbar that handles more documents than fit.
        /// </remarks>
        /// <returns>The transform rows should be parented to.</returns>
        private RectTransform CreateColumn(string name, float x, float y, float width, float height)
        {
            GameObject columnObject = new(name, typeof(RectTransform));
            columnObject.transform.SetParent(_panelRect, worldPositionStays: false);

            RectTransform column = (RectTransform)columnObject.transform;
            column.anchorMin = new Vector2(0f, 1f);
            column.anchorMax = new Vector2(0f, 1f);
            column.pivot = new Vector2(0f, 1f);
            column.anchoredPosition = new Vector2(x, -y);
            column.sizeDelta = new Vector2(width, height);

            GameObject viewportObject = new("Viewport", typeof(RectTransform));
            viewportObject.transform.SetParent(column, worldPositionStays: false);

            RectTransform viewport = (RectTransform)viewportObject.transform;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;

            /* Clips what is scrolled past. Without it the rows are drawn over the window's edge and
             * across into the other column, which reads as broken rather than as a list — and with
             * two columns it is worse than it was with one, because each would be spilling into the
             * other's rows. */
            viewportObject.AddComponent<RectMask2D>();

            GameObject contentObject = new("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, worldPositionStays: false);

            RectTransform content = (RectTransform)contentObject.transform;

            /* Pinned to the viewport's top edge and full width, growing downwards as rows are
             * added. Rows keep the anchor and pivot they have always had, so a row's y is measured
             * from the top of its own list rather than from the top of the window. */
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            ScrollRect scroll = columnObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;

            /* Clamped rather than elastic: this is a reference list being read, not something to be
             * flung about, and a list that bounces past its own end reads as a mistake. */
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            return content;
        }

        /// <summary>
        /// Gathers the printers this client can see, and puts them in a stable order.
        /// </summary>
        /// <remarks>
        /// Sorted so the numbers keep their meaning between openings. FindObjectsByType returns
        /// them in an order that has nothing to do with anything on screen, and the row says
        /// "打印到 #2", which has to mean the same machine twice running.
        /// </remarks>
        private void CollectPrinters()
        {
            Printer[] found = FindObjectsByType<Printer>(FindObjectsInactive.Exclude);

            foreach (Printer printer in found)
            {
                if (printer != null && printer.IsSpawned)
                    _printers.Add(printer);
            }

            _printers.Sort(ComparePrinters);
        }

        /// <summary>
        /// Throws away the list rows and builds them again.
        /// </summary>
        private void Rebuild()
        {
            foreach (GameObject row in _rows)
            {
                if (row == null)
                    continue;

                /* Deactivated before being destroyed. Destroy is deferred to the end of the frame,
                 * so a row left active would still be drawn on top of the new one for a frame. */
                row.SetActive(false);
                Destroy(row);
            }

            _rows.Clear();
            _documentRows.Clear();
            _printers.Clear();

            if (_panelRect == null || _computer == null)
                return;

            /* Gathered before either column is drawn, so that both are built from one list. The
             * printer column is the only one that draws them, but the list is the column's own
             * state and leaving it to be filled halfway through drawing would make the order the
             * two columns are built in matter. */
            CollectPrinters();

            float columnWidth = (PanelWidth - Padding * 2f - ColumnGap) * 0.5f;

            /* Rows are placed from the top of their own list, not the top of the window — the
             * window's title and headings are outside the scrolling areas, so both lists start at
             * zero, and each row's x is measured from its own column's left edge. */
            float documentHeight = BuildDocumentRows(0f, columnWidth, 0f);
            float printerHeight = BuildPrinterRows(0f, columnWidth, 0f);

            /* Each column is exactly as tall as its own contents, which is what gives each one its
             * own scrollbar instead of one bar for the pair. */
            if (_documentContent != null)
                _documentContent.sizeDelta = new Vector2(0f, documentHeight);

            if (_printerContent != null)
                _printerContent.sizeDelta = new Vector2(0f, printerHeight);
        }

        /// <summary>
        /// Builds the document column: a row per kind, and the documents of that kind under it.
        /// </summary>
        /// <remarks>
        /// A kind is listed only when something exists under it. A round names documents, not
        /// kinds, and the catalogue is a list of what *could* be asked for rather than of what has
        /// been — so a kind with nothing under it is a caption over nothing, and there would be one
        /// for every entry in the asset.
        ///
        /// Everything listed has been handed to this team, so the only row that cannot be pressed
        /// is one whose fetch is already on its way — and that one is drawn in place and made
        /// unclickable rather than removed, because a list that shortened itself under the player's
        /// cursor would move whatever they were about to press.
        ///
        /// Whether a row can be pressed is not the same as whether it is lit: a row that is waiting
        /// cannot be pressed but stays at full brightness, because it is working rather than
        /// withheld. Only a row this player cannot have is drawn darker.
        /// </remarks>
        private float BuildDocumentRows(float x, float width, float firstRowY)
        {
            DocumentStore store = DocumentStore.Instance;

            if (store != null)
                CollectDocumentOrder(store);

            if (_documentOrder.Count == 0)
            {
                AddRow(_documentContent, x, firstRowY, width, EmptyDocumentsHint, null);
                return firstRowY + RowHeight + RowGap;
            }

            PruneFetchClocks();

            float y = firstRowY;
            int openKind = -1;
            bool open = false;

            /* The kind's name is resolved once, where the kind row is drawn, and handed to every
             * document under it — so a row can never disagree with the caption above it about what
             * kind they are both showing. */
            string kindName = null;

            for (int i = 0; i < _documentOrder.Count; i++)
            {
                int id = _documentOrder[i];
                if (!store.TryGet(id, out DocumentRecord record))
                    continue;

                if (!open || record.SpecIndex != openKind)
                {
                    if (open)
                        y += KindGap;

                    bool known = store.TryGetSpec(id, out DocumentCatalogue.Spec spec);
                    kindName = KindName(record.SpecIndex, known, spec);

                    AddKindRow(
                        x + KindIndent,
                        y,
                        width - KindIndent,
                        kindName,
                        known ? SourceLabel(spec.Source) : null);

                    y += KindRowHeight + RowGap;

                    openKind = record.SpecIndex;
                    open = true;
                }

                AddDocumentRow(x + RowIndent, y, width - RowIndent, id, record, kindName);
                y += RowHeight + RowGap;
            }

            return y;
        }

        /// <summary>
        /// Puts every document into the order the column lists them in.
        /// </summary>
        /// <remarks>
        /// Kinds in catalogue order; within a kind, by team and then by number.
        ///
        /// The sort is on the index the record already carries, which *is* the kind's position in
        /// the catalogue — so the kinds come out in catalogue order without the panel ever needing
        /// the catalogue itself. It has no way to reach it and no other reason to want it.
        ///
        /// A document whose kind is no longer in the catalogue sorts after the ones that are, which
        /// is where it belongs: it is real, it is listed, and there is nowhere better to put it.
        /// </remarks>
        private void CollectDocumentOrder(DocumentStore store)
        {
            _documentOrder.Clear();

            /* **Only what this team has been handed.** The store is the round's list of names —
             * every document anybody has been told about, both sides' copies of all of them — so a
             * panel built from it shows the other team's paperwork next to your own. What a player
             * *has* is the grant list: the documents a job handed them when they took it.
             *
             * Both tests are here, and the second is not redundant. A grant is a document id and
             * ids are per team, so in ordinary play the grant already implies the team — but the
             * console can move somebody to the other side, and their old grants come with them. The
             * panel's promise is "my side's files, that I have been given", and it should keep that
             * promise for the same reason it makes it.
             *
             * So the column is a file cabinet rather than a catalogue, which is also what makes it
             * read the way the round was described: the list grows as jobs are taken, and a file
             * that has not been handed over is not greyed out, it is not there.
             *
             * A locked document used to be drawn greyed, and that was answering a different
             * question — it told a player what the round contained, when what they need to know is
             * what they can print. */
            for (int id = 0; id < store.Count; id++)
            {
                if (!store.TryGet(id, out DocumentRecord record))
                    continue;

                if (record.Team != _team || !IsUnlocked(id))
                    continue;

                _documentOrder.Add(id);
            }

            _documentOrder.Sort(CompareDocuments);
        }

        /// <summary>
        /// Orders two documents: kind, then team, then number.
        /// </summary>
        /// <remarks>
        /// Read out of the store rather than carried alongside the ids, because a comparison cannot
        /// see the list it is sorting and the store is the one place the answer lives.
        /// </remarks>
        private static int CompareDocuments(int left, int right)
        {
            DocumentStore store = DocumentStore.Instance;
            if (store == null)
                return left.CompareTo(right);

            store.TryGet(left, out DocumentRecord a);
            store.TryGet(right, out DocumentRecord b);

            int byKind = a.SpecIndex.CompareTo(b.SpecIndex);
            if (byKind != 0)
                return byKind;

            int byTeam = a.Team.CompareTo(b.Team);

            return byTeam != 0 ? byTeam : a.Number.CompareTo(b.Number);
        }

        /// <summary>
        /// Builds one document row.
        /// </summary>
        /// <remarks>
        /// Nothing is derived from the row's position. The document id travels with the row, in
        /// <see cref="DocumentRow.DocumentId"/>, because the two levels mean a row's place in the
        /// list and the document it stands for have nothing to do with each other.
        ///
        /// The label repeats the kind and adds the number. The kind is on the caption above, but a
        /// document is named by both — a round has an Excel 1 and an Excel 2, and a task asks for
        /// one of them — and a row that said only "1" would be unreadable the moment the caption
        /// scrolled past.
        /// </remarks>
        private void AddDocumentRow(
            float x,
            float y,
            float width,
            int documentId,
            in DocumentRecord record,
            string kindName)
        {
            bool fetching = _computer.IsFetching(documentId);

            /* Everything listed has been handed over — see CollectDocumentOrder — so the only thing
             * left that can stop a row being pressed is a fetch already on its way. */
            bool selectable = !fetching;

            /* Waiting beats locked when both are true at once, which can happen to a fetch already
             * in flight. The document is on its way, and the countdown is the only sign of it there
             * is — so that is the more useful of the two things the row could say. */
            string label = fetching
                ? FetchLabel(documentId, BeginTracking(documentId))
                : $"{kindName} {record.Number}";

            Color baseColour = selectable || fetching ? RowColour : RowLockedColour;

            Button row = AddRow(
                _documentContent,
                x,
                y,
                width,
                label,
                selectable ? () => Choose(documentId) : null,
                baseColour);

            _documentRows.Add(new DocumentRow
            {
                Button = row,
                Background = row.GetComponent<Image>(),
                Label = row.GetComponentInChildren<TextMeshProUGUI>(),
                DocumentId = documentId,
                Selectable = selectable,
                BaseColour = baseColour,
            });
        }

        /// <summary>
        /// Adds the row that names a kind, with where it comes from.
        /// </summary>
        /// <remarks>
        /// A label rather than a button, and not the same shape as a document row: a kind cannot be
        /// printed, so it must not look like something that could be. The round's notes say the
        /// same about keeping the two row kinds apart in code — the colours differ, the
        /// pressability differs, and one shared row type would have to carry a flag for each.
        ///
        /// The source tag is left off when the catalogue cannot name the kind. An unknown kind has
        /// no source to show, and guessing one would put a label on the row that nothing backs.
        /// </remarks>
        private void AddKindRow(float x, float y, float width, string name, string sourceTag)
        {
            GameObject kindObject = new("Kind", typeof(RectTransform));
            kindObject.transform.SetParent(_documentContent, worldPositionStays: false);

            /* Registered for the next rebuild, unlike the labels AddLabel makes: a kind row is a row
             * of the list and goes with the rest of them. */
            _rows.Add(kindObject);

            RectTransform rect = (RectTransform)kindObject.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, KindRowHeight);

            TextMeshProUGUI label = AddText(kindObject.transform, "Label", name, KindFontSize, TextAlignmentOptions.MidlineLeft);
            ApplyNameRect(label.rectTransform, sourceTag != null);

            /* Dimmer than a document row, so it reads as a caption for what follows rather than as
             * one more thing that could be pressed. */
            label.color = KindColour;

            if (sourceTag == null)
                return;

            TextMeshProUGUI tag = AddText(kindObject.transform, "Tag", sourceTag, KindFontSize, TextAlignmentOptions.MidlineRight);
            ApplyTagRect(tag.rectTransform);
            tag.color = KindColour;
        }

        /// <summary>
        /// What to call a kind, including when the catalogue cannot name it.
        /// </summary>
        /// <remarks>
        /// A document whose kind has been deleted from the catalogue is still a real document and
        /// may be in somebody's hands, so it is listed under a stand-in name rather than dropped.
        /// Losing a document silently is the worst way for this column to be wrong, because nothing
        /// about it looks wrong.
        /// </remarks>
        private static string KindName(int specIndex, bool known, in DocumentCatalogue.Spec spec) =>
            known && !string.IsNullOrEmpty(spec.DisplayName) ? spec.DisplayName : $"种类 {specIndex}";

        /// <summary>
        /// Where a kind comes from, as the tag at the end of its row.
        /// </summary>
        /// <remarks>
        /// Anything that is not Internet is filed, rather than only the value that means filing. A
        /// source the catalogue does not define is a data-entry mistake, and the harmless reading
        /// of it is the one to take.
        /// </remarks>
        private static string SourceLabel(int source) =>
            (DocumentSource)source == DocumentSource.Internet ? "Internet" : "后台";

        /// <summary>
        /// Whether this player's side has been handed a document.
        /// </summary>
        /// <remarks>
        /// A missing component means nothing is locked, which is what the server does with the same
        /// answer: a scene that predates unlocks offers everything rather than nothing. The two
        /// failures are not symmetric, and the cheap one is the one to prefer.
        ///
        /// The server asks the same question again when a document is actually sent, because this
        /// answer decides only whether a row can be pressed.
        /// </remarks>
        private static bool IsUnlocked(int documentId)
        {
            DocumentUnlocks unlocks = DocumentUnlocks.Instance;
            return unlocks == null || unlocks.IsUnlocked(documentId);
        }

        /// <summary>
        /// Forgets the countdowns for documents this machine is no longer waiting on.
        /// </summary>
        /// <remarks>
        /// Entries are dropped, not the whole dictionary: a countdown has to survive a rebuild
        /// caused by some other row starting or landing. The keys are copied out before the
        /// removals, because a dictionary cannot be changed while it is being walked.
        /// </remarks>
        private void PruneFetchClocks()
        {
            if (_fetchStartedAt.Count == 0)
                return;

            _pruneBuffer.Clear();

            foreach (KeyValuePair<int, float> entry in _fetchStartedAt)
            {
                if (!_computer.IsFetching(entry.Key))
                    _pruneBuffer.Add(entry.Key);
            }

            foreach (int documentId in _pruneBuffer)
                _fetchStartedAt.Remove(documentId);

            _pruneBuffer.Clear();
        }

        /// <summary>
        /// Returns when this client began counting a fetch down, starting the clock if it is new.
        /// </summary>
        private float BeginTracking(int documentId)
        {
            if (_fetchStartedAt.TryGetValue(documentId, out float started))
                return started;

            started = Time.unscaledTime;
            _fetchStartedAt[documentId] = started;
            return started;
        }

        /// <summary>
        /// Redraws the countdown on every row that is waiting.
        /// </summary>
        private void RefreshFetchLabels()
        {
            if (_fetchStartedAt.Count == 0)
                return;

            for (int i = 0; i < _documentRows.Count; i++)
            {
                DocumentRow row = _documentRows[i];

                if (row.Label == null || row.Selectable)
                    continue;
                if (!_fetchStartedAt.TryGetValue(row.DocumentId, out float started))
                    continue;

                /* Compared before it is assigned: TMP rebuilds its mesh on every set, and this
                 * runs every frame the window is open. */
                string text = FetchLabel(row.DocumentId, started);
                if (row.Label.text != text)
                    row.Label.text = text;
            }
        }

        /// <summary>
        /// What a row says while its document is being acquired.
        /// </summary>
        /// <remarks>
        /// Rounded up, so the row reads "3s" for the whole of the third second instead of spending
        /// most of it on "2s". Past zero it says the download is done and the machine is the
        /// holdup — which is exactly the state a fetch sits in when the queue has no room for it,
        /// and the state it is in for one frame in every other case, so the wording has to be true
        /// of the long one without being wrong about the short one.
        /// </remarks>
        private string FetchLabel(int documentId, float started)
        {
            float remaining = Mathf.Max(0f, FetchSeconds(documentId) - (Time.unscaledTime - started));

            return remaining <= 0f
                ? "下载完成,等待打印机…"
                : $"下载中… {Mathf.CeilToInt(remaining)}s";
        }

        /// <summary>
        /// How long a document takes to acquire, or 0 when this client cannot name it.
        /// </summary>
        private static float FetchSeconds(int documentId)
        {
            DocumentStore store = DocumentStore.Instance;
            return store != null && store.TryGetSpec(documentId, out DocumentCatalogue.Spec spec)
                ? spec.FetchSeconds
                : 0f;
        }

        /// <summary>
        /// Client: something changed what the document column may offer, so it is rebuilt.
        /// </summary>
        /// <remarks>
        /// Two events land here and they want the same work: a fetch starting or landing, and a
        /// document being handed over. Neither is frequent and both are the same kind of news, so
        /// is one handler rather than two that would have to be kept agreeing.
        ///
        /// The selection is re-applied rather than left to survive, because rebuilding the rows
        /// throws the highlight away with them. Asking for it again also drops a selection that has
        /// stopped being legal — the row that has just started waiting, or one that has been
        /// re-locked by a reset — so the lit row and the row a printer press would send are always
        /// the same one. That is the bug this line exists for; without it the panel redraws with
        /// nothing lit, and the next printer press does nothing at all.
        /// </remarks>
        private void OnOfferChanged()
        {
            if (!_open)
                return;

            Rebuild();
            Choose(_chosenDocument);
        }

        /// <summary>
        /// Builds the printer column.
        /// </summary>
        /// <remarks>
        /// The list comes from this client's own scene rather than from the server. The choice is
        /// sent back as the object itself, not as a position in a list, so a client whose scene is
        /// a frame out of date can only ever name a printer the server does not have — which is
        /// refused — and can never name the wrong one.
        /// </remarks>
        private float BuildPrinterRows(float x, float width, float firstRowY)
        {
            if (_printers.Count == 0)
            {
                AddRow(_printerContent, x, firstRowY, width, "(场景里没有打印机)", null);
                return firstRowY + RowHeight + RowGap;
            }

            for (int i = 0; i < _printers.Count; i++)
            {
                Printer printer = _printers[i];

                AddRow(
                    _printerContent,
                    x,
                    firstRowY + i * (RowHeight + RowGap),
                    width,
                    $"打印到 #{i + 1}   {printer.gameObject.name}",
                    () => SendTo(printer));
            }

            return firstRowY + _printers.Count * (RowHeight + RowGap);
        }

        /// <summary>
        /// Adds one list row, optionally clickable.
        /// </summary>
        /// <param name="onClick">
        /// What pressing it does, or null when the row cannot be pressed — a message, or a document
        /// that is not on offer.
        /// </param>
        /// <param name="background">
        /// Background colour, or null for the ordinary one. Used to draw a row this player cannot
        /// have darker than the rest without making it unreadable.
        /// </param>
        /// <param name="tag">
        /// Text for the right-hand end of the row — a team, or where a kind comes from — or null
        /// for a row that has nothing to put there.
        /// </param>
        private Button AddRow(
            Transform parent,
            float x,
            float y,
            float width,
            string label,
            System.Action onClick,
            Color? background = null,
            string tag = null)
        {
            Button button = CreateButton(parent, "Row", x, y, width, RowHeight, label, RowFontSize, onClick, tag);

            /* Registered for the next rebuild. Built here rather than inside CreateButton so that
             * the window's permanent parts, which share that method, are not swept up with them. */
            _rows.Add(button.gameObject);

            Image image = button.GetComponent<Image>();
            image.color = background ?? RowColour;

            if (onClick == null)
            {
                /* A row that cannot be pressed. Its background would still swallow clicks that
                 * should reach nothing, so it is not a raycast target at all. */
                image.raycastTarget = false;
                button.interactable = false;
            }

            return button;
        }

        /// <summary>
        /// Creates one button, and remembers it so the next rebuild can clear it.
        /// </summary>
        private Button CreateButton(
            Transform parent,
            string name,
            float x,
            float y,
            float width,
            float height,
            string label,
            float fontSize,
            System.Action onClick,
            string tag = null)
        {
            GameObject buttonObject = new(name, typeof(RectTransform));
            buttonObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            Image image = buttonObject.AddComponent<Image>();
            image.color = RowColour;

            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;

            /* The tint multiplies the image's own colour, so a value above white brightens a dark
             * row and a value below it darkens one. `selected` is kept at white because a Selectable
             * stays selected after a click, and a row that stayed lit afterwards would read as the
             * chosen document rather than as the last one pressed. */
            ColorBlock colours = button.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = new Color(1.7f, 1.7f, 1.7f, 1f);
            colours.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            colours.selectedColor = Color.white;
            colours.disabledColor = Color.white;
            colours.fadeDuration = 0.05f;
            button.colors = colours;

            if (onClick != null)
                button.onClick.AddListener(() => onClick());

            /* The name first and the tag second, and that order matters: a document row finds its
             * own name text with GetComponentInChildren, which returns the first one in the
             * hierarchy. Adding the tag first would hand every row its tag as its label, and the
             * countdown would be written over the team letter. */
            AddLabelTo(buttonObject.transform, label, fontSize, tag != null);

            if (tag != null)
                AddTagTo(buttonObject.transform, tag, fontSize);

            return button;
        }

        /// <summary>
        /// Adds the name text of a row.
        /// </summary>
        /// <param name="roomForTag">
        /// True when the row also carries a tag, which the name has to stop short of.
        /// </param>
        private void AddLabelTo(Transform parent, string text, float fontSize, bool roomForTag)
        {
            TextMeshProUGUI label = AddText(parent, "Label", text, fontSize, TextAlignmentOptions.MidlineLeft);
            ApplyNameRect(label.rectTransform, roomForTag);
        }

        /// <summary>
        /// Adds the tag that ends a row.
        /// </summary>
        private void AddTagTo(Transform parent, string text, float fontSize)
        {
            TextMeshProUGUI tag = AddText(parent, "Tag", text, fontSize, TextAlignmentOptions.MidlineRight);
            ApplyTagRect(tag.rectTransform);
        }

        /// <summary>
        /// Creates a text under a parent.
        /// </summary>
        /// <remarks>
        /// The rectangle is left to the caller, because a row's name and its tag are placed against
        /// each other and only the pair knows how much room each of them gets.
        /// </remarks>
        private TextMeshProUGUI AddText(
            Transform parent,
            string name,
            string text,
            float fontSize,
            TextAlignmentOptions alignment)
        {
            GameObject textObject = new(name, typeof(RectTransform));
            textObject.transform.SetParent(parent, worldPositionStays: false);

            TextMeshProUGUI label = textObject.AddComponent<TextMeshProUGUI>();
            ApplyText(label, text, fontSize, alignment);

            return label;
        }

        /// <summary>
        /// Places the name of a row, stopping short of the tag when the row has one.
        /// </summary>
        private static void ApplyNameRect(RectTransform rect, bool roomForTag)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(LabelInset, 0f);
            rect.offsetMax = new Vector2(roomForTag ? -(TagWidth + TagInset) : -LabelInset, 0f);
        }

        /// <summary>
        /// Places the tag at the end of a row, against its right edge.
        /// </summary>
        /// <remarks>
        /// Anchored rather than positioned, so it stays against the right edge whatever width the
        /// row is given. Both offsets are negative because, with the anchors stretched across the
        /// row, they are measured inwards from that edge.
        /// </remarks>
        private static void ApplyTagRect(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(-(TagWidth + TagInset), 0f);
            rect.offsetMax = new Vector2(-TagInset, 0f);
        }

        /// <summary>
        /// Adds a text that is not part of any button.
        /// </summary>
        private void AddLabel(string name, float x, float y, float width, float height, string text, float fontSize)
        {
            GameObject labelObject = new(name, typeof(RectTransform));
            labelObject.transform.SetParent(_panelRect, worldPositionStays: false);

            RectTransform rect = (RectTransform)labelObject.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            ApplyText(labelObject.AddComponent<TextMeshProUGUI>(), text, fontSize, TextAlignmentOptions.MidlineLeft);
        }

        /// <summary>
        /// Applies the settings every text on this panel shares.
        /// </summary>
        /// <remarks>
        /// Configured after AddComponent, not before: TMP's own Awake loads defaults off TMP
        /// Settings, so anything set first would be overwritten.
        /// </remarks>
        private void ApplyText(TextMeshProUGUI text, string value, float fontSize, TextAlignmentOptions alignment)
        {
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;

            if (_font != null)
                text.font = _font;
        }

        // ------------------------------------------------------------------ behaviour

        /// <summary>
        /// Chooses a document, and lights its row.
        /// </summary>
        /// <remarks>
        /// A row that is fetching cannot be chosen, and this follows what actually lit up rather
        /// than what was asked for. Leaving the selection on a row the player cannot see lit would
        /// put it somewhere the next printer press still sends from — and what it would send is a
        /// second fetch of a document already on its way, at full price.
        /// </remarks>
        private void Choose(int documentId)
        {
            _chosenDocument = -1;

            for (int i = 0; i < _documentRows.Count; i++)
            {
                DocumentRow row = _documentRows[i];
                if (row.Background == null)
                    continue;

                bool chosen = row.Selectable && row.DocumentId == documentId;
                if (chosen)
                    _chosenDocument = documentId;

                row.Background.color = chosen ? RowChosenColour : row.BaseColour;
            }
        }

        /// <summary>
        /// The first row that is on offer, or -1 when none is.
        /// </summary>
        private int FirstSelectableDocument()
        {
            for (int i = 0; i < _documentRows.Count; i++)
            {
                if (_documentRows[i].Selectable)
                    return _documentRows[i].DocumentId;
            }

            return -1;
        }

        /// <summary>
        /// Client: asks the server to send the chosen document to a printer.
        /// </summary>
        /// <remarks>
        /// An instant document has already arrived by the time this returns, so the window has
        /// nothing left to say and closes the way it always has — whether it landed is something
        /// the player reads off the machine rather than off this window, because the server
        /// refuses a full queue silently and any confirmation drawn here could be a lie.
        ///
        /// One that has to be fetched stays open instead. The row counting down is the only sign
        /// the player gets that anything is happening at all, and watching the cost run out is the
        /// whole of what the wait is for. They close it themselves once they have seen enough.
        ///
        /// The queue is deliberately not shown. How full a machine is belongs to the gauge that
        /// sits on the machine itself; a second readout of it here would be a second thing to
        /// keep in step.
        /// </remarks>
        private void SendTo(Printer printer)
        {
            if (_interaction == null || _computer == null || printer == null)
                return;
            if (_chosenDocument < 0)
                return;

            _interaction.RequestDocument(_computer.NetworkObject, printer.NetworkObject, _chosenDocument);

            /* **The window stays open, and the row goes busy.** It used to close on a document that
             * needed no download, which made a two-document job a two-visit job: walk to the
             * computer, open it, send, get thrown out, open it again. The fetch case already stayed
             * open — the countdown is the only sign a download is running — and the two had no
             * business differing.
             *
             * Both are marked busy for the same reason: the row the player just pressed is still
             * under their finger, and a second press during the round trip orders the same document
             * twice at full price both times. Dropping the selection afterwards means the next
             * press has to be a deliberate pick, which is one click per document and the least this
             * can cost. */
            MarkWaiting(_chosenDocument);
            Choose(_chosenDocument);
        }

        /// <summary>
        /// Marks a row busy without waiting to be told.
        /// </summary>
        /// <remarks>
        /// Sending leaves the window open, which leaves the row the player just pressed still under
        /// their finger — and a second press during the round trip orders the same document twice,
        /// at full price both times.
        ///
        /// This is a guess, and every rebuild throws it away: rows are recomputed from what the
        /// server actually says, so a request the server refused comes back on its own a round
        /// trip later. Between rebuilds the guess can only be wrong if the server refused and said
        /// nothing, which leaves the row counting down to zero and sitting there — so the repair
        /// for that is closing and reopening the window, not a timer here.
        /// </remarks>
        private void MarkWaiting(int documentId)
        {
            for (int i = 0; i < _documentRows.Count; i++)
            {
                DocumentRow row = _documentRows[i];
                if (row.DocumentId != documentId || !row.Selectable)
                    continue;

                row.Selectable = false;

                /* Back to the unlit colour, the same as any other row that is waiting: the
                 * highlight means "this is what a press would send", and a press can no longer
                 * send it. Read off the row rather than named here, so a waiting row and a locked
                 * one cannot end up disagreeing about what "unlit" means. */
                if (row.Background != null)
                    row.Background.color = row.BaseColour;

                if (row.Button != null)
                    row.Button.interactable = false;

                if (row.Label != null)
                    row.Label.text = FetchLabel(documentId, BeginTracking(documentId));

                return;
            }
        }

        /// <summary>
        /// Turns the local player's gameplay input on or off.
        /// </summary>
        /// <remarks>
        /// The window is modal, so the player stands still behind it. Every input-owning component
        /// keeps its own copy of the action asset — a shared one would let one player's Disable
        /// turn another player's input off — so each has to be told separately, the same way the
        /// dev console does it.
        ///
        /// The interaction component is switched off along with the movement, which also takes E
        /// with it: the player cannot press E at the machine behind the window and open a second
        /// one. Escape and the Close button are read directly and are unaffected.
        /// </remarks>
        private void SetGameplayInputEnabled(bool enabled)
        {
            if (_interaction != null)
                _interaction.SetInputEnabled(enabled);

            if (_movement != null)
                _movement.SetInputEnabled(enabled);

            if (_stamina != null)
                _stamina.SetInputEnabled(enabled);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Orders printers by the name they are listed under.
        /// </summary>
        private static int ComparePrinters(Printer left, Printer right)
        {
            if (left == null)
                return right == null ? 0 : 1;
            if (right == null)
                return -1;

            return string.CompareOrdinal(left.gameObject.name, right.gameObject.name);
        }
    }
}
