using Overworked.Interaction;
using Overworked.Match;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// Tells a player which side they are on, and counts the match in.
    /// </summary>
    /// <remarks>
    /// The whole hierarchy is built in code — canvas, panel, texts — rather than authored into the
    /// scene, for the reason every other readout in the project gives: scenes and prefabs are YAML
    /// and cannot be merged, and only one window may touch them. This can be dropped onto any
    /// object as a component.
    ///
    /// **It exists because a team was invisible.** Everything downstream of a team already knew
    /// about it — the customer's clock, the computer panel, which documents a folder will take —
    /// and none of it was anywhere the player could see. The first play test found that in one
    /// line: "I cannot tell whether I am team A or team B", which is a fine thing to find out from
    /// a person and a bad thing to find out from a design.
    ///
    /// **The two things it draws are the same fact at different times.** Before the match, what a
    /// player needs is "how long have I got" and "which side am I heading for"; the countdown is
    /// centred and huge because it is what everybody is waiting on, and the side is a line of text
    /// because it is not news yet. After the match begins the countdown goes away and the side line
    /// stays, which is the whole of the difference.
    ///
    /// Three properties of what gets built are load-bearing, and all three are copied from
    /// <see cref="DebugHud"/> for the same reason: the FishNet demo keeps its logo and its
    /// Host/Client buttons in the top-left at sorting order 0, and MPPM testing cannot start
    /// without them. The canvas draws above them, it carries no GraphicRaycaster anywhere in the
    /// hierarchy, and every graphic is marked as not a raycast target. Drop any one of the three
    /// and a full-screen overlay silently eats those two clicks.
    /// </remarks>
    [DisallowMultipleComponent]
    public class MatchBanner : MonoBehaviour
    {
        /// <summary>
        /// Resolution the layout is authored against, matching the other code-built panels.
        /// </summary>
        private static readonly Vector2 ReferenceResolution = new(1920f, 1080f);

        /// <summary>
        /// Font for both texts.
        /// </summary>
        [Tooltip("Font for the readout. Assign Assets/Font/simhei SDF.asset — the built-in TMP font has no Chinese glyphs.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Sorting order given to the created canvas.
        /// </summary>
        /// <remarks>
        /// Two, so the countdown draws over the debug readout at one. The countdown is the thing
        /// everybody is looking at for the few seconds it is up, and having a debug panel cover it
        /// is the one overlap that would actually cost something.
        /// </remarks>
        [Tooltip("Sorting order of the created canvas. Above the debug readout, which sits at 1.")]
        [SerializeField]
        private int _sortingOrder = 2;

        /// <summary>
        /// Text size of the line naming the player's side.
        /// </summary>
        [Tooltip("Text size of the line naming the player's side.")]
        [SerializeField]
        private float _teamFontSize = 30f;

        /// <summary>
        /// Text size of the countdown.
        /// </summary>
        [Tooltip("Text size of the countdown. Drawn centred and large: it is what everyone is waiting on.")]
        [SerializeField]
        private float _countdownFontSize = 130f;

        /// <summary>
        /// Distance from the top of the screen to the side line.
        /// </summary>
        [Tooltip("Distance from the top of the screen to the side line.")]
        [SerializeField]
        private float _margin = 24f;

        /// <summary>
        /// The canvas, or null until the first refresh.
        /// </summary>
        private GameObject _canvasObject;

        /// <summary>
        /// The line naming the player's side.
        /// </summary>
        private TextMeshProUGUI _teamText;

        /// <summary>
        /// The countdown, hidden whenever one is not running.
        /// </summary>
        private TextMeshProUGUI _countdownText;

        /// <summary>
        /// The player this client owns, or null before one has spawned.
        /// </summary>
        private PlayerInteraction _local;

        /// <summary>
        /// Whether the countdown was on screen after the last refresh, so the object is only
        /// switched when it changes.
        /// </summary>
        private bool _countdownShown;

        /// <summary>
        /// Seconds before looking for the local player again.
        /// </summary>
        /// <remarks>
        /// The same shape the debug readout uses: a player that is not there yet is retried on a
        /// timer rather than searched for every frame. A scene search ten times a second is nothing;
        /// one per frame per readout is how a project ends up with a frame budget full of them.
        /// </remarks>
        private float _retryTimer;

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            if (_canvasObject != null)
                Destroy(_canvasObject);
        }

        private void Update()
        {
            if (_teamText == null)
                return;

            if (_local == null || !_local.IsOwner)
            {
                _retryTimer -= Time.unscaledDeltaTime;
                if (_retryTimer > 0f)
                    return;

                _retryTimer = 1f;
                AcquireLocalPlayer();
            }

            _teamText.SetText(SideLabel());
            RefreshCountdown();
        }

        /// <summary>
        /// What to call the player's side.
        /// </summary>
        /// <remarks>
        /// Letters rather than numbers, because every number in this game is already something
        /// else — a document number, a team's score, a printer's position in a list — and "team 1"
        /// next to a score of 1 is a sentence nobody should have to parse twice. **This is the one
        /// place the mapping lives**: the panel and the readout deliberately show the index,
        /// because both of those are for whoever is debugging rather than for whoever is playing.
        /// </remarks>
        private string SideLabel()
        {
            if (_local == null)
                return "未分队";

            int team = _local.Team;

            return team >= 0 && team < Letters.Length
                ? $"你是 {Letters[team]} 队"
                : $"你是 {team} 队";
        }

        /// <summary>
        /// Shows the countdown while one is running, and takes it away when it is not.
        /// </summary>
        /// <remarks>
        /// The object is toggled rather than the text emptied, so that a countdown that has not
        /// started yet leaves nothing on screen at all. An empty big text still reserves its own
        /// height and would shift the side line around for the first seconds of every session.
        /// </remarks>
        private void RefreshCountdown()
        {
            MatchStarter starter = MatchStarter.Instance;

            bool running = starter != null && starter.IsCountingDown;
            int remaining = running ? starter.CountdownRemaining : 0;

            if (running != _countdownShown)
            {
                _countdownShown = running;
                _countdownText.gameObject.SetActive(running);
            }

            if (running)
                _countdownText.SetText("{0}", remaining);
        }

        /// <summary>
        /// Finds the player this client owns.
        /// </summary>
        /// <remarks>
        /// Ownership is the discriminator, the same way the camera, the debug readout and the
        /// customer's own label pick their player: on a client exactly one player belongs to it, and
        /// on a host that is the host's own.
        /// </remarks>
        private void AcquireLocalPlayer()
        {
            _local = null;

            PlayerInteraction[] candidates = FindObjectsByType<PlayerInteraction>(FindObjectsInactive.Exclude);

            foreach (PlayerInteraction candidate in candidates)
            {
                if (candidate == null || !candidate.IsOwner)
                    continue;

                _local = candidate;
                return;
            }
        }

        /// <summary>
        /// Builds the canvas, the side line and the countdown.
        /// </summary>
        private void Build()
        {
            _canvasObject = new GameObject("Match Banner", typeof(RectTransform));
            _canvasObject.transform.SetParent(transform, worldPositionStays: false);

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            /* Above the demo canvas at 0 and the debug readout at 1. Clamped rather than trusted,
             * because the failure mode of getting this wrong is an invisible overlay that still
             * blocks clicks. */
            canvas.sortingOrder = Mathf.Max(1, _sortingOrder);

            /* No GraphicRaycaster is added anywhere in this hierarchy, deliberately. That is what
             * keeps the demo's Host/Client buttons clickable; see the class remarks. */

            CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            /* Said once, at build, rather than left to be discovered: a missing font asset draws
             * every Chinese character as an empty box, and the line naming the player's side is
             * entirely Chinese. */
            if (_font == null)
            {
                Debug.LogError(
                    $"{nameof(MatchBanner)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the side line will draw as empty boxes. Assign Assets/Font/simhei SDF.asset.",
                    this);
            }

            _teamText = AddText("Side", new Vector2(0.5f, 1f), new Vector2(0f, -_margin), _teamFontSize);
            _countdownText = AddText("Countdown", new Vector2(0.5f, 0.5f), Vector2.zero, _countdownFontSize);

            _countdownText.gameObject.SetActive(false);
        }

        /// <summary>
        /// Creates one centred line of text.
        /// </summary>
        /// <remarks>
        /// Both are anchored to the middle horizontally and sized to their own content, so neither
        /// needs a background to be readable against whatever the office happens to look like
        /// behind it — an outline is enough, and a box behind a countdown would cover the game.
        /// </remarks>
        private TextMeshProUGUI AddText(string name, Vector2 anchor, Vector2 offset, float fontSize)
        {
            GameObject textObject = new(name, typeof(RectTransform));
            textObject.transform.SetParent(_canvasObject.transform, worldPositionStays: false);

            RectTransform rect = (RectTransform)textObject.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(ReferenceResolution.x, fontSize * 1.4f);

            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();

            if (_font != null)
                text.font = _font;

            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;

            /* Not a raycast target, for the reason the class remarks give. Also outlined rather
             * than given a panel: the countdown sits over the middle of the office, and a box
             * there would be a box over the game. */
            text.raycastTarget = false;
            text.outlineWidth = 0.2f;
            text.outlineColor = new Color(0f, 0f, 0f, 0.85f);

            return text;
        }

        /// <summary>
        /// How the round names its teams.
        /// </summary>
        /// <remarks>
        /// Long enough for any team count the board might be set to. Past the end the index is
        /// printed as-is, which is what a round with more teams than alphabet letters deserves.
        /// </remarks>
        private static readonly string[] Letters = { "A", "B", "C", "D", "E", "F", "G", "H" };
    }
}
