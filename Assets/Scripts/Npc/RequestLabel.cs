using System.Collections.Generic;
using System.Text;
using TMPro;
using Overworked.Documents;
using Overworked.Interaction;
using FishNet.Object;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.Npc
{
    /// <summary>
    /// The board over a customer's head: what they want, and how long you have.
    /// </summary>
    /// <remarks>
    /// **The clock it shows is the one belonging to whoever is looking.** Two players standing in
    /// front of the same customer see different countdowns, because the question the label answers
    /// is not "how long is left" but "how long have *I* got" — one team may be working on this
    /// customer while the other has not taken it, and those two are on different clocks. There is
    /// no such thing as the customer's clock to show.
    ///
    /// **Drawn in code, not authored.** The same reason the project's other interfaces are: a
    /// scene and a prefab are YAML and cannot be merged, so a window that has to put something on
    /// screen puts it there from a script and the user only has to place one empty object. This is
    /// the world-space counterpart of what <see cref="UI.DebugHud"/> does on a canvas, and it
    /// borrows its arrangement from <see cref="UI.ContainerGauge"/>: a world canvas sized in
    /// metres, turned to face the camera, with no raycaster anywhere in it.
    ///
    /// **It does not check anything.** Whether a delivery counts is decided on the server, in
    /// <see cref="Customer.Meets"/>. This reads replicated state and draws it; a label that could
    /// disagree with the server about whether a folder is good enough would be a second opinion
    /// nobody asked for.
    /// </remarks>
    [DisallowMultipleComponent]
    public class RequestLabel : MonoBehaviour
    {
        [Header("Text")]

        /// <summary>
        /// Assign Assets/Font/simhei SDF.asset.
        /// </summary>
        [Tooltip("Assign Assets/Font/simhei SDF.asset. The built-in font draws no Chinese.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Height of the text, in canvas pixels.
        /// </summary>
        [Tooltip("Text height, in canvas pixels. The canvas is scaled to metres below, so this is relative to the panel and not to the world.")]
        [Min(1f)]
        [SerializeField]
        private float _fontSize = 42f;

        [Header("Panel")]

        /// <summary>
        /// Size of the panel, in metres.
        /// </summary>
        /// <remarks>
        /// Wide enough for two lines of a Chinese document name plus a clock. Too small and a long
        /// ask wraps into three lines and overflows the background; too large and it stops reading
        /// as belonging to the person under it.
        /// </remarks>
        [Tooltip("Size of the panel, in metres.")]
        [SerializeField]
        private Vector2 _size = new(1.5f, 0.62f);

        /// <summary>
        /// How many canvas pixels make a metre.
        /// </summary>
        [Tooltip("Canvas pixels per metre. Only affects text crispness, not the size on screen.")]
        [Min(1f)]
        [SerializeField]
        private float _pixelsPerMetre = 400f;

        /// <summary>
        /// Where the panel sits, relative to the object this is on.
        /// </summary>
        [Tooltip("Where the panel sits, relative to the object this is on. A little above the customer's head.")]
        [SerializeField]
        private Vector3 _localOffset = new(0f, 2.1f, 0f);

        /// <summary>
        /// Background of the panel.
        /// </summary>
        [Tooltip("Panel background. Dark and slightly transparent, so white text survives a bright office.")]
        [SerializeField]
        private Color _panelColour = new(0f, 0f, 0f, 0.65f);

        /// <summary>
        /// Whether the panel is turned to face the camera.
        /// </summary>
        [Tooltip("Turn the panel to face the camera. Off leaves it wherever the anchor points it.")]
        [SerializeField]
        private bool _faceCamera = true;

        [Header("Refresh")]

        /// <summary>
        /// How often the text is rebuilt, in seconds.
        /// </summary>
        /// <remarks>
        /// The clock is displayed to the second and the ask changes a handful of times a round, so
        /// rebuilding on a timer rather than every frame costs nothing anyone can see and keeps a
        /// text mesh rebuild off sixty frames a second per customer.
        /// </remarks>
        [Tooltip("How often the text is rebuilt, in seconds.")]
        [Min(0.01f)]
        [SerializeField]
        private float _refreshSeconds = 0.1f;

        /// <summary>
        /// How often to look again for the local player, in seconds, while there is none.
        /// </summary>
        /// <remarks>
        /// The search walks every network object in the scene, so it is not something to do every
        /// frame. Until the local player has spawned the label simply shows no clock, and the
        /// retries stop for good once one is found.
        /// </remarks>
        [Tooltip("How often to look again for the local player while there is none, in seconds.")]
        [Min(0.1f)]
        [SerializeField]
        private float _playerRetrySeconds = 1f;

        /// <summary>
        /// The customer this hangs over. Looked up rather than serialized: it is the parent, and a
        /// field for it would be a second place for the same fact.
        /// </summary>
        private Customer _customer;

        /// <summary>
        /// The local player's interaction component, for its team. Null until one is found.
        /// </summary>
        private PlayerInteraction _local;

        /// <summary>
        /// Seconds since the text was last rebuilt.
        /// </summary>
        private float _refreshTimer;

        /// <summary>
        /// Seconds since the local player was last looked for.
        /// </summary>
        private float _playerTimer;

        /// <summary>
        /// The canvas this builds, so it can be shown and hidden with the request.
        /// </summary>
        private Canvas _canvas;

        /// <summary>
        /// The line of text.
        /// </summary>
        private TextMeshProUGUI _text;

        /// <summary>
        /// The camera the panel faces, cached because <c>Camera.main</c> is a tagged search.
        /// </summary>
        private Camera _camera;

        /// <summary>
        /// Reused while building the text, so a refresh allocates nothing.
        /// </summary>
        private readonly StringBuilder _builder = new();

        /// <summary>
        /// Reused for the rows of the request being drawn.
        /// </summary>
        private readonly List<DocumentRequest> _rows = new();

        private void Awake()
        {
            _customer = GetComponentInParent<Customer>();

            if (_customer == null)
            {
                Debug.LogError(
                    $"{nameof(RequestLabel)} on {gameObject.name} is not under a {nameof(Customer)}, so there is no request to show.",
                    this);
                return;
            }

            Build();
        }

        private void OnDestroy()
        {
            /* A child of this object, so it goes with it. This covers the other case: the component
             * removed at runtime while its object lives on — same as ContainerGauge. */
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        private void Update()
        {
            if (_canvas == null)
                return;

            float deltaTime = Time.unscaledDeltaTime;

            if (_local == null)
            {
                _playerTimer += deltaTime;
                if (_playerTimer >= _playerRetrySeconds)
                {
                    _playerTimer = 0f;
                    AcquireLocalPlayer();
                }
            }

            _refreshTimer += deltaTime;
            if (_refreshTimer < _refreshSeconds)
                return;

            _refreshTimer = 0f;
            Refresh();
        }

        private void LateUpdate()
        {
            if (!_faceCamera || _canvas == null)
                return;

            if (_camera == null)
                _camera = Camera.main;
            if (_camera == null)
                return;

            /* World rotation rather than local: the panel stays parallel to the screen whatever the
             * customer it is bolted to is rotated to. LateUpdate so it faces where the camera ended
             * up this frame rather than where it was when Update ran. */
            _canvas.transform.rotation = _camera.transform.rotation;
        }

        /// <summary>
        /// Writes the ask and the clock.
        /// </summary>
        private void Refresh()
        {
            /* Hidden rather than emptied when there is nothing to say. A customer between jobs is
             * the normal state of two of the four in the office, and a panel reading "nothing" over
             * their heads would be four panels saying things where two belong. */
            if (!_customer.HasRequest)
            {
                _canvas.gameObject.SetActive(false);
                return;
            }

            _canvas.gameObject.SetActive(true);

            _builder.Clear();
            AppendWanted();
            AppendOffer();
            AppendClock();

            _text.text = _builder.ToString();
        }

        /// <summary>
        /// Writes what this NPC is giving back, when he is giving something.
        /// </summary>
        /// <remarks>
        /// **A trade whose reward cannot be seen before agreeing to it is not a trade**, it is a
        /// chore — so the line that says what is being handed over matters more than the one saying
        /// what is wanted. A customer answers null here: his reward is points, and the scoreboard in
        /// the corner already says what those are worth.
        ///
        /// Asked of the NPC rather than checked by type, so that this label keeps working for
        /// anything that wants a line of its own without learning what kinds of NPC exist. See
        /// <see cref="Customer.OfferLabel"/>.
        /// </remarks>
        private void AppendOffer()
        {
            string offer = _customer.OfferLabel;

            if (string.IsNullOrEmpty(offer))
                return;

            _builder.Append('\n').Append(offer);
        }

        /// <summary>
        /// Writes what the customer is asking for: a name and a number per row.
        /// </summary>
        /// <remarks>
        /// The name comes from the catalogue through the store, not from the row — a row carries a
        /// kind index because that is what a request is checked against, and a name is what a
        /// person reads. They are the same fact in two forms, and only one of them belongs on
        /// screen.
        /// </remarks>
        private void AppendWanted()
        {
            RequestBoard board = RequestBoard.Instance;
            DocumentStore store = DocumentStore.Instance;

            if (board == null || store == null || !board.TryGetWanted(_customer.RequestId, _rows))
            {
                _builder.Append("...");
                return;
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i > 0)
                    _builder.Append(" · ");

                DocumentRequest row = _rows[i];

                if (store.TryGetSpecAt(row.SpecIndex, out DocumentCatalogue.Spec spec))
                    _builder.Append(spec.DisplayName);
                else
                    _builder.Append('#').Append(row.SpecIndex);

                _builder.Append(' ').Append(row.Number);
            }
        }

        /// <summary>
        /// Writes the clock belonging to the player reading it.
        /// </summary>
        /// <remarks>
        /// Nothing at all when the local team is not known. That happens for the second or so
        /// between the scene loading and the local player spawning, and showing some other team's
        /// countdown in the meantime would be worse than showing none.
        ///
        /// Rounded up, so a clock that has a fifth of a second left reads as 1 rather than as 0 —
        /// a zero that is still running is a number that makes people think the game has stopped.
        /// </remarks>
        private void AppendClock()
        {
            if (_local == null)
                return;

            string clock = ClockText(_local.Team);

            /* Nothing at all rather than an empty line, for the same reason the whole panel is
             * hidden when there is no request: a blank row is a row somebody reads. */
            if (!string.IsNullOrEmpty(clock))
                _builder.Append('\n').Append(clock);
        }

        /// <summary>
        /// What the clock line says for a team, or null when there is no clock to speak of.
        /// </summary>
        /// <remarks>
        /// Two questions, and they are the NPC's to answer rather than this label's: whether it
        /// gives up on its own (see <see cref="Customer.UsesWaitingClock"/>), and whether a team
        /// that has taken the job is on a clock of its own (see
        /// <see cref="Customer.UsesPatienceClock"/>). An NPC that answers no to the second has no
        /// countdown to draw, and drawing a frozen one would be the label announcing a deadline
        /// that is not running — which is worse than saying nothing, because the player waits for
        /// it.
        /// </remarks>
        private string ClockText(int team)
        {
            switch (_customer.PhaseOf(team))
            {
                case CustomerPhase.Failed:
                    return "出局";

                case CustomerPhase.Working:
                    return _customer.UsesPatienceClock
                        ? Mathf.CeilToInt(_customer.RemainingFor(team)) + "s"
                        : null;

                default:
                    if (!_customer.UsesWaitingClock)
                        return "未接单";

                    return "等待 " + Mathf.CeilToInt(_customer.WaitingRemaining) + "s";
            }
        }

        /// <summary>
        /// Finds the player this client owns.
        /// </summary>
        /// <remarks>
        /// Ownership is the discriminator, the same way the camera and the debug readout pick their
        /// player: on a client exactly one player belongs to it, and on a host that is the host's
        /// own.
        /// </remarks>
        private void AcquireLocalPlayer()
        {
            _local = null;

            NetworkObject[] candidates = FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude);

            foreach (NetworkObject candidate in candidates)
            {
                if (candidate == null || !candidate.IsOwner)
                    continue;
                if (!candidate.CompareTag("Player"))
                    continue;

                _local = candidate.GetComponent<PlayerInteraction>();
                if (_local != null)
                    return;
            }
        }

        /// <summary>
        /// Creates the panel and everything on it.
        /// </summary>
        private void Build()
        {
            GameObject canvasObject = new("Request Label", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, worldPositionStays: false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            /* Above the FishNet demo canvas at 0. Clamped rather than trusted, matching DebugHud
             * and ContainerGauge: getting this wrong means an overlay that still blocks clicks. */
            _canvas.sortingOrder = 1;

            /* No GraphicRaycaster anywhere in this hierarchy, deliberately. Nothing here is meant to
             * be clicked, and a raycaster is what would let it eat the demo's Host/Client buttons. */

            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(
                Mathf.Max(0.01f, _size.x) * _pixelsPerMetre,
                Mathf.Max(0.01f, _size.y) * _pixelsPerMetre);
            canvasRect.localPosition = _localOffset;
            canvasRect.localRotation = Quaternion.identity;

            /* The parent's scale is divided out so the panel lands at the requested size in metres
             * whatever the customer model was imported at. Read once: a customer that rescales
             * itself at runtime is not a thing that happens. */
            Vector3 parentScale = transform.lossyScale;
            float inverse = 1f / _pixelsPerMetre;
            canvasRect.localScale = new Vector3(
                inverse / Mathf.Max(Mathf.Abs(parentScale.x), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.y), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.z), 1e-4f));

            CreateBackground(canvasRect);
            CreateText(canvasRect);

            /* Hidden until the first refresh decides otherwise. It would otherwise sit there as an
             * empty dark panel for the tenth of a second before the first refresh runs. */
            canvasObject.SetActive(false);
        }

        /// <summary>
        /// Adds the panel behind the text.
        /// </summary>
        private void CreateBackground(RectTransform parent)
        {
            GameObject panelObject = new("Background", typeof(RectTransform));
            panelObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)panelObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = panelObject.AddComponent<Image>();
            image.color = _panelColour;

            /* Load-bearing: an Image is the one graphic in the project that would happily swallow a
             * click meant for the FishNet demo buttons behind it. */
            image.raycastTarget = false;
        }

        /// <summary>
        /// Adds the text.
        /// </summary>
        private void CreateText(RectTransform parent)
        {
            GameObject textObject = new("Text", typeof(RectTransform));
            textObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 6f);
            rect.offsetMax = new Vector2(-8f, -6f);

            /* Configured after AddComponent, not before: TMP's Awake loads defaults off TMP
             * Settings, so anything set before it would be overwritten. */
            _text = textObject.AddComponent<TextMeshProUGUI>();
            _text.raycastTarget = false;

            /* Wrapping on, unlike the debug readout: a customer's ask is a sentence about documents
             * and is allowed to use two lines inside the panel. */
            _text.textWrappingMode = TextWrappingModes.Normal;
            _text.alignment = TextAlignmentOptions.Center;
            _text.fontSize = _fontSize;
            _text.color = Color.white;

            if (_font != null)
            {
                _text.font = _font;
            }
            else
            {
                Debug.LogError(
                    $"{nameof(RequestLabel)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the label will fall back to the built-in font, which draws no Chinese. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }
        }
    }
}
