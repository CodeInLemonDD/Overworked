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
        /// Height of the heading that names a source group.
        /// </summary>
        private const float GroupHeadingHeight = 28f;

        /// <summary>
        /// Extra space left under a group, so the next heading reads as a new group rather than as
        /// one more row.
        /// </summary>
        private const float GroupGap = 12f;

        /// <summary>
        /// How far a group heading is indented from the column edge.
        /// </summary>
        private const float GroupIndent = 10f;

        /// <summary>
        /// How far a row is indented under its group heading.
        /// </summary>
        /// <remarks>
        /// The indentation is the only thing saying the rows belong to the heading above them.
        /// Without it the two groups are one undifferentiated list with two labels lost inside it.
        /// </remarks>
        private const float RowIndent = 26f;

        /// <summary>
        /// Font size for body rows.
        /// </summary>
        private const float RowFontSize = 22f;

        /// <summary>
        /// Font size for a source group's heading.
        /// </summary>
        private const float GroupHeadingFontSize = 19f;

        /// <summary>
        /// Font size for headers and the title.
        /// </summary>
        private const float HeadingFontSize = 26f;

        /// <summary>
        /// Font size for the footer hint.
        /// </summary>
        private const float HintFontSize = 18f;

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
        /// Colour of a source group's heading.
        /// </summary>
        private static readonly Color GroupHeadingColour = new(0.62f, 0.67f, 0.76f, 1f);

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
        /// The window itself, and the parent every row is placed under.
        /// </summary>
        private RectTransform _panelRect;

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
        private readonly List<SpecRow> _documentRows = new();

        /// <summary>
        /// When this client first saw each spec start being fetched, by catalogue index.
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
        /// One row of the document column.
        /// </summary>
        /// <remarks>
        /// A record rather than three lists kept the same length, because they are only ever
        /// right together and a row that is fetching has no business lighting up as the chosen
        /// document — so which row means which spec, and whether it can be picked at all, have
        /// to travel with the row.
        /// </remarks>
        private sealed class SpecRow
        {
            public Button Button;
            public Image Background;
            public TextMeshProUGUI Label;
            public int SpecIndex;
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
        private int _chosenSpec = -1;

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
             * offer while the window is open: a fetch starting or landing, and a kind being opened
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

            Build();
            Rebuild();

            /* A document is chosen before the panel is shown, so the first printer row a player
             * clicks always has something to send. Landing on an empty choice would make the first
             * click do nothing, which reads as the window being broken. A row that is already
             * waiting is passed over for the same reason — it cannot be sent, so it cannot be what
             * the first click spends. */
            Choose(FirstSelectableSpec());

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

            float columnWidth = (PanelWidth - Padding * 2f - ColumnGap) * 0.5f;
            float rightColumnX = Padding * 2f + ColumnGap * 0.5f + columnWidth;

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

            float columnWidth = (PanelWidth - Padding * 2f - ColumnGap) * 0.5f;
            float rightColumnX = Padding * 2f + ColumnGap * 0.5f + columnWidth;
            float firstRowY = TitleHeight + HeaderHeight;

            BuildDocumentRows(Padding, columnWidth, firstRowY);
            BuildPrinterRows(rightColumnX, columnWidth, firstRowY);
        }

        /// <summary>
        /// Builds the document column, as one group per source.
        /// </summary>
        /// <remarks>
        /// Rows that cannot be pressed — one already on its way, one that is still locked — are
        /// drawn in place and made unclickable rather than removed. A list that shortened itself
        /// under the player's cursor would move whatever they were about to press; and for a locked
        /// kind, hiding it would hide the one thing the player is meant to learn from it.
        ///
        /// Whether a row can be pressed is not the same as whether it is lit: a row that is waiting
        /// cannot be pressed but stays at full brightness, because it is working rather than
        /// withheld. Only a locked one is drawn darker.
        /// </remarks>
        private void BuildDocumentRows(float x, float width, float firstRowY)
        {
            DocumentCatalogue catalogue = _computer.Catalogue;

            if (catalogue == null || catalogue.Count == 0)
            {
                AddRow(x, firstRowY, width, "(没有可获取的文档)", null);
                return;
            }

            PruneFetchClocks(catalogue);

            /* The y is carried down through the groups rather than computed from a row number.
             * Grouping is what breaks "the fourth row is the fourth spec", so nothing here may
             * derive a spec from a position — which spec a row means travels on the row. */
            float y = BuildSourceGroup(x, width, firstRowY, catalogue, DocumentSource.Filing);
            BuildSourceGroup(x, width, y, catalogue, DocumentSource.Internet);
        }

        /// <summary>
        /// Builds one source group: its heading, then its rows.
        /// </summary>
        /// <remarks>
        /// A heading is drawn only when the group has something under it; a heading with nothing
        /// following would be a promise the catalogue does not keep.
        /// </remarks>
        /// <returns>The y the next group starts at.</returns>
        private float BuildSourceGroup(
            float x,
            float width,
            float y,
            DocumentCatalogue catalogue,
            DocumentSource source)
        {
            if (!HasSpecInGroup(catalogue, source))
                return y;

            AddGroupHeading(x + GroupIndent, y, width - GroupIndent, GroupName(source));
            y += GroupHeadingHeight + RowGap;

            for (int i = 0; i < catalogue.Count; i++)
            {
                if (!catalogue.TryGet(i, out DocumentCatalogue.Spec spec) || !InGroup(spec, source))
                    continue;

                AddSpecRow(x + RowIndent, y, width - RowIndent, i, spec);
                y += RowHeight + RowGap;
            }

            return y + GroupGap;
        }

        /// <summary>
        /// Builds one row of the document column.
        /// </summary>
        /// <remarks>
        /// Nothing is derived from the row's position. The catalogue index travels with the row, in
        /// <see cref="SpecRow.SpecIndex"/>, because grouping means the two no longer agree.
        /// </remarks>
        private void AddSpecRow(float x, float y, float width, int specIndex, DocumentCatalogue.Spec spec)
        {
            bool fetching = _computer.IsFetching(specIndex);
            bool selectable = IsUnlocked(specIndex) && !fetching;

            /* Waiting beats locked when both are true at once, which a reset can do to a fetch
             * already in flight. The document is on its way, and the countdown is the only sign of
             * it there is — so that is the more useful of the two things the row could say. */
            string label = fetching
                ? FetchLabel(specIndex, BeginTracking(specIndex))
                : selectable
                    ? spec.DisplayName
                    : $"{spec.DisplayName}   锁定";

            Color baseColour = selectable || fetching ? RowColour : RowLockedColour;

            Button row = AddRow(
                x,
                y,
                width,
                label,
                selectable ? () => Choose(specIndex) : null,
                baseColour);

            _documentRows.Add(new SpecRow
            {
                Button = row,
                Background = row.GetComponent<Image>(),
                Label = row.GetComponentInChildren<TextMeshProUGUI>(),
                SpecIndex = specIndex,
                Selectable = selectable,
                BaseColour = baseColour,
            });
        }

        /// <summary>
        /// Adds the heading that names a source group.
        /// </summary>
        private void AddGroupHeading(float x, float y, float width, string text)
        {
            GameObject headingObject = new("Group", typeof(RectTransform));
            headingObject.transform.SetParent(_panelRect, worldPositionStays: false);

            /* Registered for the next rebuild, unlike the labels AddLabel makes: a group heading is
             * a row of the list and goes with the rest of them. */
            _rows.Add(headingObject);

            RectTransform rect = (RectTransform)headingObject.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, GroupHeadingHeight);

            TextMeshProUGUI label = headingObject.AddComponent<TextMeshProUGUI>();
            ApplyText(label, text, GroupHeadingFontSize, TextAlignmentOptions.MidlineLeft);

            /* Dimmer than a row, and smaller, so it reads as a caption for what follows rather than
             * as one more thing that could be pressed. */
            label.color = GroupHeadingColour;
        }

        /// <summary>
        /// Whether a kind may be asked for.
        /// </summary>
        /// <remarks>
        /// A missing component means nothing is locked, which is what the server does with the same
        /// answer: a scene that predates unlocks offers everything rather than nothing. The two
        /// failures are not symmetric, and the cheap one is the one to prefer.
        /// </remarks>
        private static bool IsUnlocked(int specIndex)
        {
            DocumentUnlocks unlocks = DocumentUnlocks.Instance;
            return unlocks == null || unlocks.IsUnlocked(specIndex);
        }

        /// <summary>
        /// Whether a spec belongs in a group.
        /// </summary>
        /// <remarks>
        /// Anything that is not Internet is filed, rather than only the value that means filing.
        /// A source the catalogue does not define is a data-entry mistake, and the one outcome that
        /// must not come of it is a spec appearing in no group at all — the panel would silently
        /// drop a document, which is exactly what a hand-written grouping invites.
        /// </remarks>
        private static bool InGroup(DocumentCatalogue.Spec spec, DocumentSource source) =>
            ((DocumentSource)spec.Source == DocumentSource.Internet)
            == (source == DocumentSource.Internet);

        /// <summary>
        /// Whether a group has anything under it.
        /// </summary>
        private static bool HasSpecInGroup(DocumentCatalogue catalogue, DocumentSource source)
        {
            for (int i = 0; i < catalogue.Count; i++)
            {
                if (catalogue.TryGet(i, out DocumentCatalogue.Spec spec) && InGroup(spec, source))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Forgets the countdowns for specs this machine is no longer waiting on.
        /// </summary>
        /// <remarks>
        /// Entries are dropped, not the whole dictionary: a countdown has to survive a rebuild
        /// caused by some other row starting or landing. The keys are copied out before the
        /// removals, because a dictionary cannot be changed while it is being walked.
        /// </remarks>
        private void PruneFetchClocks(DocumentCatalogue catalogue)
        {
            if (_fetchStartedAt.Count == 0)
                return;

            _pruneBuffer.Clear();

            foreach (KeyValuePair<int, float> entry in _fetchStartedAt)
            {
                if (entry.Key >= catalogue.Count || !_computer.IsFetching(entry.Key))
                    _pruneBuffer.Add(entry.Key);
            }

            foreach (int specIndex in _pruneBuffer)
                _fetchStartedAt.Remove(specIndex);

            _pruneBuffer.Clear();
        }

        /// <summary>
        /// Returns when this client began counting a fetch down, starting the clock if it is new.
        /// </summary>
        private float BeginTracking(int specIndex)
        {
            if (_fetchStartedAt.TryGetValue(specIndex, out float started))
                return started;

            started = Time.unscaledTime;
            _fetchStartedAt[specIndex] = started;
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
                SpecRow row = _documentRows[i];

                if (row.Label == null || row.Selectable)
                    continue;
                if (!_fetchStartedAt.TryGetValue(row.SpecIndex, out float started))
                    continue;

                /* Compared before it is assigned: TMP rebuilds its mesh on every set, and this
                 * runs every frame the window is open. */
                string text = FetchLabel(row.SpecIndex, started);
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
        private string FetchLabel(int specIndex, float started)
        {
            float remaining = Mathf.Max(0f, FetchSeconds(specIndex) - (Time.unscaledTime - started));

            return remaining <= 0f
                ? "下载完成,等待打印机…"
                : $"下载中… {Mathf.CeilToInt(remaining)}s";
        }

        /// <summary>
        /// How long a spec takes to acquire, or 0 when there is no such spec.
        /// </summary>
        private float FetchSeconds(int specIndex)
        {
            DocumentCatalogue catalogue = _computer != null ? _computer.Catalogue : null;
            if (catalogue == null || !catalogue.TryGet(specIndex, out DocumentCatalogue.Spec spec))
                return 0f;

            return spec.FetchSeconds;
        }

        /// <summary>
        /// Client: something changed what the document column may offer, so it is rebuilt.
        /// </summary>
        /// <remarks>
        /// Two events land here and they want the same work: a fetch starting or landing, and a
        /// kind being opened up. Neither is frequent and both are the same kind of news, so there
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
            Choose(_chosenSpec);
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
        private void BuildPrinterRows(float x, float width, float firstRowY)
        {
            Printer[] found = FindObjectsByType<Printer>(FindObjectsInactive.Exclude);

            foreach (Printer printer in found)
            {
                if (printer != null && printer.IsSpawned)
                    _printers.Add(printer);
            }

            /* Sorted so the numbers keep their meaning between openings. FindObjectsByType returns
             * them in an order that has nothing to do with anything on screen. */
            _printers.Sort(ComparePrinters);

            if (_printers.Count == 0)
            {
                AddRow(x, firstRowY, width, "(场景里没有打印机)", null);
                return;
            }

            for (int i = 0; i < _printers.Count; i++)
            {
                Printer printer = _printers[i];

                AddRow(
                    x,
                    firstRowY + i * (RowHeight + RowGap),
                    width,
                    $"打印到 #{i + 1}   {printer.gameObject.name}",
                    () => SendTo(printer));
            }
        }

        /// <summary>
        /// Adds one list row, optionally clickable.
        /// </summary>
        /// <param name="onClick">
        /// What pressing it does, or null when the row cannot be pressed — a message, or a document
        /// that is not on offer.
        /// </param>
        /// <param name="background">
        /// Background colour, or null for the ordinary one. Used to draw a row that is withheld
        /// darker than the rest without making it unreadable.
        /// </param>
        private Button AddRow(
            float x,
            float y,
            float width,
            string label,
            System.Action onClick,
            Color? background = null)
        {
            Button button = CreateButton("Row", x, y, width, RowHeight, label, RowFontSize, onClick);

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
            string name,
            float x,
            float y,
            float width,
            float height,
            string label,
            float fontSize,
            System.Action onClick)
        {
            GameObject buttonObject = new(name, typeof(RectTransform));
            buttonObject.transform.SetParent(_panelRect, worldPositionStays: false);

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

            AddLabelTo(buttonObject.transform, label, fontSize);

            return button;
        }

        /// <summary>
        /// Adds a text child filling a button.
        /// </summary>
        private void AddLabelTo(Transform parent, string text, float fontSize)
        {
            GameObject textObject = new("Label", typeof(RectTransform));
            textObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(14f, 0f);
            rect.offsetMax = new Vector2(-14f, 0f);

            ApplyText(textObject.AddComponent<TextMeshProUGUI>(), text, fontSize, TextAlignmentOptions.MidlineLeft);
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
        private void Choose(int index)
        {
            _chosenSpec = -1;

            for (int i = 0; i < _documentRows.Count; i++)
            {
                SpecRow row = _documentRows[i];
                if (row.Background == null)
                    continue;

                bool chosen = row.Selectable && row.SpecIndex == index;
                if (chosen)
                    _chosenSpec = index;

                row.Background.color = chosen ? RowChosenColour : row.BaseColour;
            }
        }

        /// <summary>
        /// The first row that is on offer, or -1 when none is.
        /// </summary>
        private int FirstSelectableSpec()
        {
            for (int i = 0; i < _documentRows.Count; i++)
            {
                if (_documentRows[i].Selectable)
                    return _documentRows[i].SpecIndex;
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
            if (_chosenSpec < 0)
                return;

            _interaction.RequestDocument(_computer.NetworkObject, printer.NetworkObject, _chosenSpec);

            if (FetchSeconds(_chosenSpec) > 0f)
            {
                MarkWaiting(_chosenSpec);
                return;
            }

            Hide();
        }

        /// <summary>
        /// Marks a row busy without waiting to be told.
        /// </summary>
        /// <remarks>
        /// A fetch that has to wait leaves the window open, which leaves the row the player just
        /// pressed still under their finger — and a second press during the round trip orders the
        /// same document twice, at full price both times.
        ///
        /// This is a guess, and every rebuild throws it away: rows are recomputed from what the
        /// server actually says, so a request the server refused comes back on its own a round
        /// trip later. Between rebuilds the guess can only be wrong if the server refused and said
        /// nothing, which leaves the row counting down to zero and sitting there — so the repair
        /// for that is closing and reopening the window, not a timer here.
        /// </remarks>
        private void MarkWaiting(int specIndex)
        {
            for (int i = 0; i < _documentRows.Count; i++)
            {
                SpecRow row = _documentRows[i];
                if (row.SpecIndex != specIndex || !row.Selectable)
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
                    row.Label.text = FetchLabel(specIndex, BeginTracking(specIndex));

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
        /// What a source group is called.
        /// </summary>
        /// <remarks>
        /// The source is named once per group rather than repeated on every row. With the rows
        /// sitting under a heading that already says where they come from, a suffix on each of them
        /// would be the same word twice in a column two rows tall.
        /// </remarks>
        private static string GroupName(DocumentSource source) =>
            source == DocumentSource.Internet ? "Internet" : "后台文件";

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
