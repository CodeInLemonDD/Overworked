using Overworked.Containers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// Shows how full a container is, as an indicator rather than as a picture of its contents.
    /// </summary>
    /// <remarks>
    /// The contents of a container have left the physical world — that is the whole premise of the
    /// container model. Drawing them back as a pile of paper would contradict the thing it is
    /// meant to be reporting on, and it would pile up meshes for what is a single integer. So the
    /// reading is a bar and a count: enough to answer "can this machine work right now", which is
    /// the only question a stock level is ever asked.
    ///
    /// One component serves every container in the game — the printer's paper, ink and queue, and
    /// the paper box. It knows nothing about printers, and must not learn.
    ///
    /// The hierarchy is built in code rather than authored into a prefab, for the same reason
    /// DebugHud is: scenes and prefabs are YAML, cannot be merged, and only one window may touch
    /// them. Dropping this component on a node is the entire setup.
    ///
    /// A world-space canvas sits at <see cref="_localOffset"/> and is turned to face the local
    /// camera, because a gauge on a machine is read while walking around it. Three properties of
    /// what gets built are load-bearing and exist for one reason — the FishNet demo keeps the
    /// Host/Client buttons in the top-left, and MPPM testing cannot start without them: nothing
    /// here ever adds a GraphicRaycaster, every graphic is marked as not a raycast target, and the
    /// sorting order is clamped to at least 1. See CONSTRAINTS.md, convention C.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ContainerGauge : MonoBehaviour
    {
        #region Configuration.
        /// <summary>
        /// The container being reported on.
        /// </summary>
        [Tooltip("The container being reported on.")]
        [SerializeField]
        private ContainerBase _container;

        /// <summary>
        /// Text shown above the bar. Empty means the container's own object name.
        /// </summary>
        /// <remarks>
        /// The container usually sits on a child node called "Paper" or "Output", which is already
        /// the name a reader wants. Overriding is for the cases where the node name is not: two
        /// gauges on one node, or a name that was never meant to be read by a player.
        /// </remarks>
        [Tooltip("Text shown above the bar. Leave empty to use the container's object name.")]
        [SerializeField]
        private string _label;

        /// <summary>
        /// Font for the readout.
        /// </summary>
        [Tooltip("Font for the readout. Assign Assets/Font/simhei SDF.asset — the built-in TMP font draws no Chinese.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Where the gauge sits, relative to this transform.
        /// </summary>
        [Tooltip("Where the gauge sits, relative to this transform.")]
        [SerializeField]
        private Vector3 _localOffset = new(0f, 0.35f, 0f);

        /// <summary>
        /// Size of the gauge in metres.
        /// </summary>
        /// <remarks>
        /// Metres rather than canvas units because this is a thing in the world: the number that
        /// matters is how big it looks next to the machine, not how many units wide a rect is.
        /// </remarks>
        [Tooltip("Size of the gauge in metres.")]
        [SerializeField]
        private Vector2 _size = new(0.7f, 0.22f);

        /// <summary>
        /// Canvas units per metre.
        /// </summary>
        /// <remarks>
        /// The gauge is drawn on a canvas of this many units per metre and then scaled down, which
        /// is what keeps the text sharp: a world-space canvas rasterises text against its own unit
        /// grid, so a canvas that is one unit per metre would give an eight-unit-tall label about
        /// eight texels to work with. Raise this if text looks soft; it costs nothing but vertices.
        /// </remarks>
        [Tooltip("Canvas units per metre. Higher is sharper text.")]
        [Min(1f)]
        [SerializeField]
        private float _pixelsPerMetre = 400f;

        /// <summary>
        /// Sorting order given to the created canvas.
        /// </summary>
        [Tooltip("Sorting order of the created canvas. Clamped to at least 1.")]
        [SerializeField]
        private int _sortingOrder = 1;

        /// <summary>
        /// Whether the gauge turns to face the local camera.
        /// </summary>
        [Tooltip("Whether the gauge turns to face the local camera. Turn off for a gauge on a wall or a fixed panel.")]
        [SerializeField]
        private bool _faceCamera = true;

        /// <summary>
        /// Behind the text and bar.
        /// </summary>
        [Tooltip("Background panel colour.")]
        [SerializeField]
        private Color _panelColour = new(0f, 0f, 0f, 0.6f);

        /// <summary>
        /// The filled part of the bar while there is something in the container.
        /// </summary>
        [Tooltip("Bar colour while the container holds something.")]
        [SerializeField]
        private Color _fillColour = new(0.25f, 0.7f, 1f, 1f);

        /// <summary>
        /// The filled part of the bar once the container is empty.
        /// </summary>
        /// <remarks>
        /// An empty container is the one state worth colouring: it is the state where the machine
        /// stops working, and it is not visible from the outside of a machine whose contents are
        /// data. This is the indicator light.
        /// </remarks>
        [Tooltip("Bar colour once the container is empty. The one state worth flagging.")]
        [SerializeField]
        private Color _emptyColour = new(1f, 0.4f, 0.25f, 1f);

        /// <summary>
        /// Colour of the numbers.
        /// </summary>
        [Tooltip("Colour of the readout text.")]
        [SerializeField]
        private Color _textColour = Color.white;
        #endregion

        #region Layout constants.
        /// <summary>
        /// Bottom of the text band, as a fraction of the gauge height.
        /// </summary>
        private const float TextBandBottom = 0.45f;

        /// <summary>
        /// Bottom of the bar band, as a fraction of the gauge height.
        /// </summary>
        private const float BarBandBottom = 0.12f;

        /// <summary>
        /// Top of the bar band, as a fraction of the gauge height.
        /// </summary>
        private const float BarBandTop = 0.42f;

        /// <summary>
        /// Left and right inset of the bar, as a fraction of the gauge width.
        /// </summary>
        private const float BarSideInset = 0.05f;

        /// <summary>
        /// Text size, as a fraction of the whole gauge height.
        /// </summary>
        /// <remarks>
        /// Derived rather than authored so resizing the gauge scales its text with it. There is no
        /// font size field on purpose: two numbers that both control how big the text is can
        /// disagree, and the one in the Inspector is the harder one to notice.
        /// </remarks>
        private const float TextSizeFraction = 0.32f;

        /// <summary>
        /// Colour of the unfilled part of the bar.
        /// </summary>
        private static readonly Color TrackColour = new(1f, 1f, 1f, 0.15f);
        #endregion

        #region Runtime.
        /// <summary>
        /// Canvas created by this component.
        /// </summary>
        private Canvas _canvas;

        /// <summary>
        /// Empty bar the fill is drawn inside.
        /// </summary>
        private RectTransform _track;

        /// <summary>
        /// Filled part of the bar.
        /// </summary>
        private Image _fill;

        /// <summary>
        /// Name of the container.
        /// </summary>
        private TMP_Text _labelText;

        /// <summary>
        /// Count and capacity.
        /// </summary>
        private TMP_Text _valueText;

        /// <summary>
        /// Cached camera to face. Re-acquired whenever it goes away.
        /// </summary>
        private Camera _camera;
        #endregion

        private void Awake()
        {
            Build();
        }

        private void OnEnable()
        {
            if (_container != null)
                _container.ContentsChanged += Refresh;

            /* The container is usually already populated by the time this runs — the server fills
             * its boxes at start — so the first reading cannot wait for a change that has already
             * happened. */
            Refresh();
        }

        private void OnDisable()
        {
            if (_container != null)
                _container.ContentsChanged -= Refresh;
        }

        private void OnDestroy()
        {
            /* The canvas is a child, so destroying this object takes it along. This covers the
             * other case: the component removed at runtime while its object lives on. */
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        private void LateUpdate()
        {
            if (!_faceCamera || _canvas == null)
                return;

            if (_camera == null)
                _camera = Camera.main;
            if (_camera == null)
                return;

            /* World rotation, not local: the gauge stays parallel to the screen whatever the
             * machine it is bolted to is rotated to. LateUpdate so it faces where the camera
             * ended up this frame rather than where it was when Update ran. */
            _canvas.transform.rotation = _camera.transform.rotation;
        }

        /// <summary>
        /// Creates the canvas and everything on it.
        /// </summary>
        private void Build()
        {
            if (_container == null)
            {
                Debug.LogError(
                    $"{nameof(ContainerGauge)} on {gameObject.name} has no {nameof(ContainerBase)} assigned; " +
                    "it will show nothing.",
                    this);
                return;
            }

            float width = Mathf.Max(0.01f, _size.x) * _pixelsPerMetre;
            float height = Mathf.Max(0.01f, _size.y) * _pixelsPerMetre;

            GameObject canvasObject = new("Container Gauge", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, worldPositionStays: false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            /* Above the FishNet demo canvas at 0. Clamped rather than trusted, matching DebugHud:
             * the failure mode of getting this wrong is an overlay that still blocks clicks. */
            _canvas.sortingOrder = Mathf.Max(1, _sortingOrder);

            /* No GraphicRaycaster anywhere in this hierarchy, deliberately. Nothing here is meant
             * to be clicked, and a raycaster is what would let it eat the demo's Host/Client
             * buttons; see the class remarks. */

            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(width, height);
            canvasRect.localPosition = _localOffset;
            canvasRect.localRotation = Quaternion.identity;

            /* The parent's scale is divided out so the gauge lands at the requested size in metres
             * even on a machine whose model was imported at some other scale. Read once, at build:
             * authoring a machine that rescales itself at runtime is not a thing that happens. */
            Vector3 parentScale = transform.lossyScale;
            float inverse = 1f / _pixelsPerMetre;
            canvasRect.localScale = new Vector3(
                inverse / Mathf.Max(Mathf.Abs(parentScale.x), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.y), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.z), 1e-4f));

            CreatePanel(canvasRect);
            CreateTexts(canvasRect, height);
            CreateBar(canvasRect, width);
        }

        /// <summary>
        /// Adds the background panel.
        /// </summary>
        private void CreatePanel(RectTransform parent)
        {
            GameObject panelObject = new("Panel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)panelObject.transform;
            Place(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            Image image = panelObject.AddComponent<Image>();
            image.color = _panelColour;
            image.raycastTarget = false;
        }

        /// <summary>
        /// Adds the name and the numbers.
        /// </summary>
        private void CreateTexts(RectTransform parent, float height)
        {
            _labelText = CreateText(parent, "Label", TextAlignmentOptions.MidlineLeft, height);
            _valueText = CreateText(parent, "Value", TextAlignmentOptions.MidlineRight, height);

            /* One band, two texts anchored to opposite ends of it. Cheaper and more predictable
             * than a layout group for a row that never reflows — the strings here are a word and a
             * ratio, and neither wraps. */
            float inset = height * 0.12f;

            Place(
                (RectTransform)_labelText.transform,
                new Vector2(0f, TextBandBottom),
                new Vector2(0.6f, 1f),
                new Vector2(inset, 0f),
                Vector2.zero);

            Place(
                (RectTransform)_valueText.transform,
                new Vector2(0.4f, TextBandBottom),
                new Vector2(1f, 1f),
                Vector2.zero,
                new Vector2(-inset, 0f));
        }

        /// <summary>
        /// Adds one text object filling the given anchor band.
        /// </summary>
        private TMP_Text CreateText(RectTransform parent, string name, TextAlignmentOptions alignment, float height)
        {
            GameObject textObject = new(name, typeof(RectTransform));
            textObject.transform.SetParent(parent, worldPositionStays: false);

            /* Configured after AddComponent, not before: TMP's Awake loads defaults off TMP
             * Settings, so anything set before it would be overwritten. */
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.raycastTarget = false;

            /* One line each. A wrapped number reads as two numbers. */
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = alignment;
            text.fontSize = height * TextSizeFraction;
            text.color = _textColour;

            if (_font != null)
            {
                text.font = _font;
            }
            else if (_labelText == null)
            {
                /* Once per gauge, on the first text built, so a scene full of unconfigured gauges
                 * does not fill the console with the same line. */
                Debug.LogError(
                    $"{nameof(ContainerGauge)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the gauge will fall back to the built-in font, which draws no Chinese. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }

            return text;
        }

        /// <summary>
        /// Adds the bar.
        /// </summary>
        private void CreateBar(RectTransform parent, float width)
        {
            GameObject trackObject = new("Bar", typeof(RectTransform));
            trackObject.transform.SetParent(parent, worldPositionStays: false);

            _track = (RectTransform)trackObject.transform;
            float inset = width * BarSideInset;
            Place(
                _track,
                new Vector2(0f, BarBandBottom),
                new Vector2(1f, BarBandTop),
                new Vector2(inset, 0f),
                new Vector2(-inset, 0f));

            Image track = trackObject.AddComponent<Image>();
            track.color = TrackColour;
            track.raycastTarget = false;

            GameObject fillObject = new("Fill", typeof(RectTransform));
            fillObject.transform.SetParent(_track, worldPositionStays: false);

            _fill = fillObject.AddComponent<Image>();
            _fill.color = _fillColour;
            _fill.raycastTarget = false;
        }

        /// <summary>
        /// Anchors a rect to a band of its parent and insets it.
        /// </summary>
        /// <remarks>
        /// One call that sets both, because the two interact: writing the anchors and then calling
        /// something that reaches for a full stretch would silently throw the band away. The
        /// rect is placed entirely by anchors and offsets, so its size needs no separate write.
        /// </remarks>
        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        /// <summary>
        /// Redraws the gauge from the container's current count.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="ContainerBase.ContentsChanged"/> rather than from Update. A stock
        /// level changes when somebody carries something to the machine, which is a few times a
        /// minute; polling it every frame would be sixty times the work to catch the same events.
        ///
        /// A container that has not spawned yet reads as empty, and is drawn as empty for the
        /// moment before its contents arrive. That is not a bug to be papered over with a loading
        /// state: the list really is empty locally, the gauge is telling the truth about it, and
        /// the change callback that follows corrects it within a frame or two of the spawn.
        /// </remarks>
        private void Refresh()
        {
            if (_labelText == null || _valueText == null || _fill == null)
                return;
            if (_container == null)
                return;

            int count = _container.Count;

            _labelText.SetText(string.IsNullOrEmpty(_label) ? _container.gameObject.name : _label);

            if (_container.IsUnlimited)
            {
                /* No denominator, so no fraction to draw. Any bar here would have to invent a
                 * maximum, and a bar that is wrong is worse than no bar — the count alone is the
                 * honest reading. Both images are disabled rather than left at the previous
                 * fraction, which would freeze a stale level on screen. */
                _valueText.SetText("{0} ∞", (float)count);

                _track.gameObject.SetActive(false);
                return;
            }

            int capacity = _container.Capacity;

            /* Capacity is positive on this branch, so the division is safe. Clamped anyway: a
             * container whose capacity was lowered below what it holds is a real thing to be able
             * to survive, and a bar that overflows its track is a stranger sight than a full one. */
            float fraction = Mathf.Clamp01(count / (float)capacity);

            /* Cast to float on purpose. TMP also offers an int overload taking a
             * ReadOnlySpan<char>, so passing the raw ints is ambiguous between the two and does
             * not compile. */
            _valueText.SetText("{0}/{1}", (float)count, (float)capacity);

            _track.gameObject.SetActive(true);

            /* Driven by the anchors rather than by Image.fillAmount. An Image with no sprite falls
             * back to a plain quad and ignores its fill type entirely, so fillAmount would compile,
             * run, and do nothing at all. Anchors need no sprite and are exact. */
            _fill.rectTransform.anchorMin = Vector2.zero;
            _fill.rectTransform.anchorMax = new Vector2(fraction, 1f);
            _fill.rectTransform.offsetMin = Vector2.zero;
            _fill.rectTransform.offsetMax = Vector2.zero;

            _fill.color = count == 0 ? _emptyColour : _fillColour;
        }
    }
}
