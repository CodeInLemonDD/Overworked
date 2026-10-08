using System.Collections.Generic;
using FishNet.Component.Transforming.Beta;
using Overworked.Interaction;
using Overworked.Match;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// The name tag over a player's head: which side they are on, and which of that side they are.
    /// </summary>
    /// <remarks>
    /// **It exists because four identical people were standing in a room.** Nothing in the project
    /// ever drew a player — no name, no number, no colour — and every test before this one had two
    /// players, where "the other one" needs no label. The first four-player session said it in one
    /// line: *nobody can tell who is who, and I cannot tell which side I am on.* The second half of
    /// that is the sharper one, because the game already believed it had answered it: the banner
    /// says "你是 A 队", and **nothing anywhere in the world was A** — not the characters, not the
    /// floor. A word for a thing that has no appearance is not an answer.
    ///
    /// **The callsign is derived, never entered.** A team letter plus a rank within the team —
    /// <c>A1</c>, <c>B2</c> — which is one short thing that answers both halves of the complaint and
    /// needs nobody to type anything. Names would need an input field, a round trip to the server
    /// and a rule for duplicates; Steam would supply them, and Steam is not wired up. When it is,
    /// this is where the name goes.
    ///
    /// **The rank is counted off a list sorted by <c>OwnerId</c>, and that is not a detail.**
    /// <c>FindObjectsByType</c> does not promise the same order on two machines — CONSTRAINTS.md
    /// hard constraint 19 — so ranking off the raw result would hand the same player a different
    /// number on every client, silently: the seed is right, the logs are right, and each machine
    /// looks reasonable on its own. A callsign that means a different person depending on who is
    /// reading it is worse than no callsign. <c>OwnerId</c> is replicated, so it sorts the same
    /// everywhere.
    ///
    /// **Before the sides are picked it shows a plain number and no letter.** The team a player
    /// carries defaults to zero, which is also team A, so a tag that trusted it would tell all four
    /// players they were on A while they were still choosing. See
    /// <see cref="UI.MatchBanner.SideLabel"/>, which had the same bug in words.
    ///
    /// **Built in code, and copied from <see cref="Npc.RequestLabel"/> rather than invented.** That
    /// is the project's only world-space label that is wired into a prefab and known to work, and
    /// there is no shared helper for the recipe by design — one readout, one component, arranged in
    /// code. The canvas, the metres-to-pixels sizing, the divided-out parent scale, the billboard
    /// and the absent raycaster are all its arrangement; the deliberate differences are the
    /// per-team panel colour, the roster scan, and where it hangs.
    ///
    /// **It hangs off the smoothed node, not the player root, and that is the difference that is
    /// easy to get wrong.** A player is two objects: the root, which the network steps once per
    /// tick, and <c>Graphical</c>, which <c>NetworkTickSmoother</c> interpolates toward it and
    /// which the camera follows. <c>PlayerCameraFollow</c> says so in its own remarks — following
    /// the root directly makes the camera step with the tick. A tag parented to the root would step
    /// at tick rate while the camera glided, which is the one combination that reads as broken
    /// rather than as lag. So this looks for the smoother and parents there, wherever the component
    /// itself happens to sit, rather than trusting where it was dropped in the inspector.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlayerTag : MonoBehaviour
    {
        #region Text.
        /// <summary>
        /// Assign Assets/Font/simhei SDF.asset.
        /// </summary>
        [Tooltip("Assign Assets/Font/simhei SDF.asset. The built-in font draws no Chinese.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Height of the callsign, in canvas pixels.
        /// </summary>
        [Tooltip("Text height, in canvas pixels. The canvas is scaled to metres below, so this is relative to the panel and not to the world.")]
        [Min(1f)]
        [SerializeField]
        private float _fontSize = 44f;
        #endregion

        #region Panel.
        /// <summary>
        /// Size of the tag, in metres.
        /// </summary>
        [Tooltip("Size of the tag, in metres. Wide enough for a callsign and 「（你）」.")]
        [SerializeField]
        private Vector2 _size = new(0.95f, 0.34f);

        /// <summary>
        /// How many canvas pixels make a metre.
        /// </summary>
        [Tooltip("Canvas pixels per metre. Only affects text crispness, not the size on screen.")]
        [Min(1f)]
        [SerializeField]
        private float _pixelsPerMetre = 400f;

        /// <summary>
        /// Where the tag sits, relative to the smoothed node under the player.
        /// </summary>
        /// <remarks>
        /// Measured from the player's feet, because the smoother sits at the origin of the model
        /// rather than at its head. The character is about 1.3 m tall, so 2.25 clears the head by
        /// roughly a metre — which the camera, eight metres up and four back looking down at sixty
        /// degrees, puts a little above the middle of the screen.
        /// </remarks>
        [Tooltip("Where the tag sits, relative to the player's smoothed node — measured from the feet. 2.25 clears the head by about a metre.")]
        [SerializeField]
        private Vector3 _localOffset = new(0f, 2.25f, 0f);

        /// <summary>
        /// Background per team id, in the same order <see cref="UI.MatchBanner.TeamLetter"/> names
        /// teams.
        /// </summary>
        /// <remarks>
        /// **A deliberate copy, and the one piece of debt this file carries.** The colours are
        /// <see cref="Containers.PayloadLabel"/>'s — the values authored on
        /// <c>Contract.prefab</c>, <c>Excel.prefab</c> and <c>Folder.prefab</c> — because a team has
        /// to be one colour across everything it appears on, and the folders already established
        /// what that colour is. That class warns in its own remarks that two arrays are two places
        /// to get a team's colour wrong, and it is right; the reason this is still a second array
        /// is that its <c>_teamColours</c> is private with no accessor and the project has no shared
        /// palette to read instead. The fix is a palette asset that both draw from. It is not in
        /// this round because it means changing a frozen interface and creating an asset, and the
        /// cost of being wrong here is a name tag in the wrong shade.
        /// </remarks>
        [Tooltip("Panel colour per team id, matching the folder colours. Index 0 is team A.")]
        [SerializeField]
        private Color[] _teamColours =
        {
            new(0.604f, 0.185f, 0.051f, 1f),
            new(0.046f, 0.323f, 0.509f, 1f),
        };

        /// <summary>
        /// Background before anyone is on a side.
        /// </summary>
        [Tooltip("Panel colour while nobody has been put on a side yet.")]
        [SerializeField]
        private Color _unassignedColour = new(0.13f, 0.13f, 0.15f, 0.92f);

        /// <summary>
        /// Whether the tag is turned to face the camera.
        /// </summary>
        [Tooltip("Turn the tag to face the camera. Off leaves it wherever the anchor points it.")]
        [SerializeField]
        private bool _faceCamera = true;
        #endregion

        #region Refresh.
        /// <summary>
        /// How often the roster is re-read, in seconds.
        /// </summary>
        /// <remarks>
        /// The text is rebuilt every frame, because it is a string and a colour. The roster is not:
        /// it costs a <c>FindObjectsByType</c> of the whole scene plus a sort, and it only changes
        /// when somebody joins or is put on a side. One second is the cadence
        /// <see cref="UI.MatchBanner"/> already uses to find the local player.
        /// </remarks>
        [Tooltip("How often the player list is re-read, in seconds. The text itself is rebuilt every frame.")]
        [Min(0.05f)]
        [SerializeField]
        private float _rosterSeconds = 1f;
        #endregion

        #region Runtime.
        /// <summary>
        /// Appended to the local player's own tag.
        /// </summary>
        /// <remarks>
        /// **The camera is behind a character that looks like the other three.** In a third-person
        /// game the thing you are looking at is not obviously you, and with four identical models
        /// it is a real question rather than a rhetorical one. Words rather than a brighter colour,
        /// because a colour difference is the first thing to disappear and the last thing to be
        /// noticed.
        /// </remarks>
        private const string LocalMarker = "（你）";

        /// <summary>
        /// Reused across every tag on this client, so the scan allocates nothing after the first.
        /// </summary>
        private static readonly List<PlayerInteraction> Roster = new();

        /// <summary>
        /// Orders the roster the same way on every machine. See the class remarks.
        /// </summary>
        private static readonly System.Comparison<PlayerInteraction> ByOwnerId = CompareByOwnerId;

        private Canvas _canvas;
        private Image _panel;
        private TextMeshProUGUI _text;
        private Camera _camera;
        private PlayerInteraction _player;

        /// <summary>
        /// This player's position in the whole room, counted from one. Zero until the first scan.
        /// </summary>
        private int _roomRank;

        /// <summary>
        /// This player's position within their own team, counted from one. Zero until sides exist.
        /// </summary>
        private int _teamRank;

        private float _rosterTimer;
        #endregion

        private void Awake()
        {
            _player = GetComponentInParent<PlayerInteraction>();

            if (_player == null)
            {
                Debug.LogError(
                    $"{nameof(PlayerTag)} on {gameObject.name} found no {nameof(PlayerInteraction)} " +
                    "on itself or above it, so it has nothing to label.",
                    this);

                return;
            }

            /* Timer starts at zero so the first scan happens on the first frame rather than a
             * second later — a tag that reads "1" from the moment it exists, instead of blinking
             * into place after the round has started. */
            _rosterTimer = 0f;

            Build();
        }

        private void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        private void Update()
        {
            if (_canvas == null)
                return;

            _rosterTimer -= Time.unscaledDeltaTime;
            if (_rosterTimer <= 0f)
            {
                _rosterTimer = _rosterSeconds;
                RebuildRoster();
            }

            /* Every frame, unlike the roster: it is one string and one colour, and the team it
             * reads is a SyncVar that can change on the frame the sides are announced. */
            string callsign = Callsign();

            _canvas.gameObject.SetActive(callsign.Length > 0);

            if (callsign.Length == 0)
                return;

            _text.SetText(callsign);
            _panel.color = PanelColour();
        }

        private void LateUpdate()
        {
            if (!_faceCamera || _canvas == null)
                return;

            if (_camera == null)
                _camera = Camera.main;

            if (_camera == null)
                return;

            /* World rotation rather than local: the tag has to be parallel to the screen however
             * the character under it happens to be turned. LateUpdate because the camera has
             * finished moving for the frame by the time this runs. */
            _canvas.transform.rotation = _camera.transform.rotation;
        }

        /// <summary>
        /// Reads the room and works out where this player sits in it.
        /// </summary>
        private void RebuildRoster()
        {
            _roomRank = 0;
            _teamRank = 0;

            if (_player == null)
                return;

            Roster.Clear();
            Roster.AddRange(FindObjectsByType<PlayerInteraction>(FindObjectsInactive.Exclude));
            Roster.Sort(ByOwnerId);

            int team = _player.Team;
            int seen = 0;
            int rank = 0;

            for (int i = 0; i < Roster.Count; i++)
            {
                PlayerInteraction candidate = Roster[i];
                if (candidate == null)
                    continue;

                seen++;

                if (candidate == _player)
                    _roomRank = seen;

                if (candidate.Team != team)
                    continue;

                /* Counted before the identity test, so that reaching this player includes them:
                 * first in a team is rank one, not rank zero. */
                rank++;

                if (candidate == _player)
                    _teamRank = rank;
            }
        }

        /// <summary>
        /// What this player's tag says.
        /// </summary>
        /// <remarks>
        /// Empty before the roster has been read once, which is what keeps an unlabelled tag from
        /// appearing for a frame. The caller hides the panel on an empty string rather than drawing
        /// a blank chip.
        /// </remarks>
        private string Callsign()
        {
            if (_player == null)
                return string.Empty;

            string mine = _player.IsOwner ? LocalMarker : string.Empty;

            /* Not on a side yet. A letter here would be a claim the game cannot back up — every
             * player carries team zero until somebody assigns them, and team zero is a real team. */
            if (!SidesAssigned())
                return _roomRank > 0 ? $"{_roomRank}{mine}" : string.Empty;

            return _teamRank > 0 ? $"{MatchBanner.TeamLetter(_player.Team)}{_teamRank}{mine}" : mine;
        }

        private Color PanelColour()
        {
            if (!SidesAssigned() || _player == null)
                return _unassignedColour;

            int team = _player.Team;
            bool known = _teamColours != null && team >= 0 && team < _teamColours.Length;

            return known ? _teamColours[team] : _unassignedColour;
        }

        /// <summary>
        /// Whether anybody has been put on a side yet.
        /// </summary>
        /// <remarks>
        /// Asked of <see cref="MatchStarter.IsDeparting"/> and not of the team value, because the
        /// team value cannot answer it: it is zero for everybody until the match begins, and zero
        /// is also team A. Departing is set by <c>BeginMatch</c> immediately <em>after</em> it
        /// writes the teams, and by <c>ServerForceStart</c> the same way, so it is exactly the
        /// question "have the sides been handed out" — and it is replicated, so a client can ask it.
        /// </remarks>
        private static bool SidesAssigned()
        {
            MatchStarter starter = MatchStarter.Instance;

            return starter != null && starter.IsDeparting;
        }

        private static int CompareByOwnerId(PlayerInteraction left, PlayerInteraction right)
        {
            int a = left != null ? left.OwnerId : int.MaxValue;
            int b = right != null ? right.OwnerId : int.MaxValue;

            return a.CompareTo(b);
        }

        /// <summary>
        /// Creates the tag and everything on it.
        /// </summary>
        private void Build()
        {
            /* Parented to the smoothed node rather than to whatever this component sits on, so
             * that the tag and the camera move together — see the class remarks. Falls back to this
             * transform when there is no smoother to find, which is what a scene with no network
             * running yet looks like. */
            Transform anchor = transform;
            NetworkTickSmoother smoother = GetComponentInChildren<NetworkTickSmoother>();

            if (smoother != null)
                anchor = smoother.transform;

            GameObject canvasObject = new("Player Tag", typeof(RectTransform));
            canvasObject.transform.SetParent(anchor, worldPositionStays: false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            /* Above the FishNet demo canvas at 0. */
            _canvas.sortingOrder = 1;

            /* No GraphicRaycaster anywhere in this hierarchy, deliberately. Nothing here is meant to
             * be clicked, and a raycaster is what would let it eat the demo's Host/Client buttons —
             * which are how a session gets started at all. See CONSTRAINTS.md convention C. */

            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(
                Mathf.Max(0.01f, _size.x) * _pixelsPerMetre,
                Mathf.Max(0.01f, _size.y) * _pixelsPerMetre);
            canvasRect.localPosition = _localOffset;
            canvasRect.localRotation = Quaternion.identity;

            /* The anchor's scale is divided out so the tag lands at the requested size in metres
             * whatever the character model was imported at. Read once: a player that rescales
             * itself at runtime is not a thing that happens. */
            Vector3 parentScale = anchor.lossyScale;
            float inverse = 1f / _pixelsPerMetre;
            canvasRect.localScale = new Vector3(
                inverse / Mathf.Max(Mathf.Abs(parentScale.x), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.y), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.z), 1e-4f));

            CreateBackground(canvasRect);
            CreateText(canvasRect);

            /* Hidden until the first refresh decides otherwise. It would otherwise sit there as an
             * empty dark chip for the frame before the roster is read. */
            canvasObject.SetActive(false);
        }

        /// <summary>
        /// Adds the panel behind the callsign.
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

            _panel = panelObject.AddComponent<Image>();
            _panel.color = _unassignedColour;

            /* Load-bearing: an Image is the one graphic in the project that would happily swallow a
             * click meant for the FishNet demo buttons behind it. */
            _panel.raycastTarget = false;
        }

        /// <summary>
        /// Adds the callsign text.
        /// </summary>
        private void CreateText(RectTransform parent)
        {
            GameObject textObject = new("Text", typeof(RectTransform));
            textObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 4f);
            rect.offsetMax = new Vector2(-6f, -4f);

            /* Configured after AddComponent, not before: TMP's Awake loads defaults off TMP
             * Settings, so anything set before it would be overwritten. */
            _text = textObject.AddComponent<TextMeshProUGUI>();
            _text.raycastTarget = false;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
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
                    $"{nameof(PlayerTag)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the tag will fall back to the built-in font, which draws no Chinese. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }
        }
    }
}
