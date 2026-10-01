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
        /// Font size for body rows.
        /// </summary>
        private const float RowFontSize = 22f;

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
        /// Backgrounds of the document rows, in list order, for highlighting the chosen one.
        /// </summary>
        private readonly List<Image> _documentRows = new();

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

            /* Cached now rather than looked up when the panel closes: by then the player object may
             * be gone, and leaving input switched off on a player that is still alive is the worst
             * way to find out. */
            _movement = interaction.GetComponent<PlayerMovementPrediction>();
            _stamina = interaction.GetComponent<PlayerStamina>();

            Build();
            Rebuild();

            /* A document is chosen before the panel is shown, so the first printer row a player
             * clicks always has something to send. Landing on an empty choice would make the first
             * click do nothing, which reads as the window being broken. */
            List<DocumentCatalogue.Spec> specs = AvailableSpecs();
            Choose(specs.Count > 0 ? 0 : -1);

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

            if (_canvasObject != null)
                _canvasObject.SetActive(false);

            /* Before the references are dropped, and unconditionally: whatever else went wrong,
             * the player must not be left standing still with nothing on screen to explain it. */
            SetGameplayInputEnabled(true);

            _interaction = null;
            _computer = null;
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
                Hide();
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
        /// Builds the document column.
        /// </summary>
        private void BuildDocumentRows(float x, float width, float firstRowY)
        {
            DocumentCatalogue catalogue = _computer.Catalogue;

            if (catalogue == null || catalogue.Count == 0)
            {
                AddRow(x, firstRowY, width, "(没有可获取的文档)", null);
                return;
            }

            for (int i = 0; i < catalogue.Count; i++)
            {
                if (!catalogue.TryGet(i, out DocumentCatalogue.Spec spec))
                    continue;

                int index = i;

                Button row = AddRow(
                    x,
                    firstRowY + i * (RowHeight + RowGap),
                    width,
                    $"{spec.DisplayName}   [{SourceName(spec.Source)}]",
                    () => Choose(index));

                _documentRows.Add(row.GetComponent<Image>());
            }
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
        private Button AddRow(float x, float y, float width, string label, System.Action onClick)
        {
            Button button = CreateButton("Row", x, y, width, RowHeight, label, RowFontSize, onClick);

            /* Registered for the next rebuild. Built here rather than inside CreateButton so that
             * the window's permanent parts, which share that method, are not swept up with them. */
            _rows.Add(button.gameObject);

            if (onClick == null)
            {
                /* A row that is only a message. Its background would still swallow clicks that
                 * should reach nothing, so it is not a raycast target at all. */
                button.GetComponent<Image>().raycastTarget = false;
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
        /// The specs this computer offers, in catalogue order.
        /// </summary>
        private List<DocumentCatalogue.Spec> AvailableSpecs()
        {
            List<DocumentCatalogue.Spec> specs = new();

            DocumentCatalogue catalogue = _computer != null ? _computer.Catalogue : null;
            if (catalogue == null)
                return specs;

            for (int i = 0; i < catalogue.Count; i++)
            {
                if (catalogue.TryGet(i, out DocumentCatalogue.Spec spec))
                    specs.Add(spec);
            }

            return specs;
        }

        /// <summary>
        /// Chooses a document, and lights its row.
        /// </summary>
        private void Choose(int index)
        {
            _chosenSpec = index;

            for (int i = 0; i < _documentRows.Count; i++)
            {
                Image row = _documentRows[i];
                if (row != null)
                    row.color = i == index ? RowChosenColour : RowColour;
            }
        }

        /// <summary>
        /// Client: asks the server to send the chosen document to a printer.
        /// </summary>
        /// <remarks>
        /// Closed straight afterwards. One press is one document, and whether it landed is
        /// something the player reads off the machine rather than off this window — the server
        /// refuses a full queue silently, so any confirmation drawn here could be a lie.
        ///
        /// The queue is deliberately not shown. How full a machine is belongs to the gauge that
        /// will sit on the machine itself; a second readout of it here would be a second thing to
        /// keep in step.
        /// </remarks>
        private void SendTo(Printer printer)
        {
            if (_interaction == null || _computer == null || printer == null)
                return;
            if (_chosenSpec < 0)
                return;

            _interaction.RequestDocument(_computer.NetworkObject, printer.NetworkObject, _chosenSpec);

            Hide();
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
        /// A readable name for where a document comes from.
        /// </summary>
        private static string SourceName(int source) =>
            (DocumentSource)source == DocumentSource.Internet ? "Internet" : "后台";

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
