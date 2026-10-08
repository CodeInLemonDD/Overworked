using Overworked.Interaction;
using Overworked.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.Match
{
    /// <summary>
    /// A patch of floor a player stands on to say which side they want.
    /// </summary>
    /// <remarks>
    /// The whole of the team-picking mechanism, and deliberately the whole of it: a box, a number,
    /// and where that side's players are put when the match begins. Everything that decides when a
    /// choice counts — how many players there are, whether they are all standing in one, when the
    /// countdown runs — belongs to <see cref="MatchStarter"/>, which watches every zone at once and
    /// is the only thing that can see all of them.
    ///
    /// **The number is authored, not counted.** Zones are numbered in the inspector rather than
    /// being "the first one in the scene and the other one", because scene order is not a thing
    /// anybody can see while placing them, and a drag that reorders two objects in the hierarchy
    /// would silently swap the teams. Zero is team A. A zone left on a number another zone already
    /// has is a wiring mistake <see cref="MatchStarter"/> reports once rather than a thing to
    /// guess about.
    ///
    /// **The box is a box, not a trigger.** A trigger collider needs a Rigidbody on one of the two
    /// objects to fire at all, and a player is a CharacterController, which is not one — so a
    /// trigger volume here would silently never see anybody. This asks the same question in the
    /// same way every other volume in the project does, through
    /// <see cref="IntakeVolume.Contains"/>.
    ///
    /// **The letter on the floor is here rather than in a component of its own.** It was invisible
    /// in the game for as long as this class has existed — the gizmo below only draws in the
    /// editor, so picking a side meant standing in a box that had no appearance at all. What fixed
    /// it is a marking built in code, and it belongs in this class because the box's geometry is
    /// already here: a second component would have to be told <c>_centre</c> and
    /// <c>_halfExtents</c> again, which is two places for the marking and the place it marks to
    /// disagree. It draws a letter and not a team colour for the same reason there is no colour
    /// here to draw from — that would be a third table of team colours, and the letter has no
    /// ambiguity to resolve.
    /// </remarks>
    [DisallowMultipleComponent]
    public class TeamZone : MonoBehaviour
    {
        /// <summary>
        /// Which side this zone picks. Zero is team A.
        /// </summary>
        [Tooltip("Which team standing here picks. 0 is team A.")]
        [Min(0)]
        [SerializeField]
        private int _team;

        /// <summary>
        /// Middle of the box, in this object's own space.
        /// </summary>
        [Tooltip("Middle of the box, relative to this object. Raised above the pivot so it covers a player rather than the floor under them.")]
        [SerializeField]
        private Vector3 _centre = new(0f, 1f, 0f);

        /// <summary>
        /// Half the size of the box, in this object's own space.
        /// </summary>
        /// <remarks>
        /// Generous for the same reason the intake volumes are: a player deciding which side to be
        /// on should be able to stand roughly here rather than exactly here, and the cost of a box
        /// that is too big is only that two players who meant different things are both counted.
        /// </remarks>
        [Tooltip("Half the size of the box. Generous on purpose: this is a place to stand, not a target.")]
        [SerializeField]
        private Vector3 _halfExtents = new(1.5f, 1.2f, 1.5f);

        /// <summary>
        /// Where this side's players are put when the match starts, or null to leave them alone.
        /// </summary>
        /// <remarks>
        /// Optional. Empty is a perfectly good configuration — the players are already standing
        /// where they chose to be — and it is the safer default for a scene being laid out, since a
        /// zone with no spawn moves nobody rather than moving everybody to the origin.
        ///
        /// The scene's own `Spwan point` objects are the intended thing to drag in here.
        /// </remarks>
        [Tooltip("Where this team is placed when the match starts. Empty leaves players where they are.")]
        [SerializeField]
        private Transform _spawn;

        /// <summary>
        /// The letter drawn on the floor. Assign Assets/Font/simhei SDF.asset.
        /// </summary>
        [Tooltip("Assign Assets/Font/simhei SDF.asset, the same font the other readouts use.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Colour of the patch painted on the floor.
        /// </summary>
        [Tooltip("Colour of the patch on the floor. Kept dark and translucent so it reads as a marking rather than as a hole.")]
        [SerializeField]
        private Color _markerColour = new(0f, 0f, 0f, 0.35f);

        /// <summary>
        /// Colour of the letter.
        /// </summary>
        [Tooltip("Colour of the letter.")]
        [SerializeField]
        private Color _letterColour = new(1f, 1f, 1f, 0.85f);

        /// <summary>
        /// How tall the letter is, as a fraction of the patch.
        /// </summary>
        [Tooltip("Height of the letter as a fraction of the patch. One letter, so this can be most of it.")]
        [Range(0.05f, 0.95f)]
        [SerializeField]
        private float _letterFraction = 0.6f;

        /// <summary>
        /// How many canvas pixels make a metre.
        /// </summary>
        [Tooltip("Canvas pixels per metre. Only affects crispness, not the size on the floor.")]
        [Min(1f)]
        [SerializeField]
        private float _pixelsPerMetre = 400f;

        /// <summary>
        /// How far above this object's origin the marking sits.
        /// </summary>
        /// <remarks>
        /// The box is centred on <c>_centre</c>, which is deliberately raised so it covers a player
        /// rather than the floor — so the marking cannot be placed at the box's own middle and has
        /// to be given the floor height instead. The lobby floor is at this object's origin, hence
        /// a hair above zero.
        /// </remarks>
        [Tooltip("How far above this object's origin the floor marking sits. The patch is drawn at the floor, not at the middle of the box.")]
        [SerializeField]
        private float _markerHeight = 0.02f;

        /// <summary>
        /// Which team standing here picks.
        /// </summary>
        public int Team => _team;

        /// <summary>
        /// Where this team is placed when the match starts, or null.
        /// </summary>
        public Transform Spawn => _spawn;

        /// <summary>
        /// True when a world position is inside this zone.
        /// </summary>
        /// <remarks>
        /// Measured in this object's own space, so a zone that is turned or scaled is not a lie
        /// about where it is — the same rule every other volume in the project follows.
        /// </remarks>
        public bool Contains(Vector3 worldPosition) =>
            IntakeVolume.Contains(transform, _centre, _halfExtents, worldPosition);

        /// <summary>
        /// The canvas laid on the floor. Built in <c>Awake</c>; null before that.
        /// </summary>
        private Canvas _marker;

        private void Awake()
        {
            BuildMarker();
        }

        private void OnDestroy()
        {
            if (_marker != null)
                Destroy(_marker.gameObject);
        }

        /// <summary>
        /// Lays a letter on the floor so the place to stand can be seen.
        /// </summary>
        /// <remarks>
        /// **A world-space canvas rather than a quad with a material.** A material has to exist as
        /// an asset, has to be dragged in, and has to have survived the build — and the shader it
        /// names has to be one the build kept. An Image uses the interface shader that is present
        /// in every build and takes its colour per instance, which is the same trade the project's
        /// other readouts already made. Nothing here is clickable and nothing carries a raycaster;
        /// see CONSTRAINTS.md convention C.
        /// </remarks>
        private void BuildMarker()
        {
            GameObject markerObject = new("Zone Marker", typeof(RectTransform));
            markerObject.transform.SetParent(transform, worldPositionStays: false);

            _marker = markerObject.AddComponent<Canvas>();
            _marker.renderMode = RenderMode.WorldSpace;

            /* Above the FishNet demo canvas at 0. */
            _marker.sortingOrder = 1;

            RectTransform rect = (RectTransform)markerObject.transform;
            rect.sizeDelta = new Vector2(
                Mathf.Max(0.01f, _halfExtents.x) * 2f * _pixelsPerMetre,
                Mathf.Max(0.01f, _halfExtents.z) * 2f * _pixelsPerMetre);

            /* At the floor rather than at the middle of the box: _centre is deliberately raised so
             * the volume covers a player, and a marking drawn there would hang in the air. */
            rect.localPosition = new Vector3(_centre.x, _markerHeight, _centre.z);

            /* Face up, with the top of the letter pointing away from the way in, so it reads the
             * right way up to somebody walking toward it. Local, so a zone that is turned carries
             * its letter round with it. */
            rect.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);

            /* The parent's scale is divided out so the marking lands at the size of the box in
             * metres whatever this object was scaled to. */
            Vector3 parentScale = transform.lossyScale;
            float inverse = 1f / _pixelsPerMetre;
            rect.localScale = new Vector3(
                inverse / Mathf.Max(Mathf.Abs(parentScale.x), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.y), 1e-4f),
                inverse / Mathf.Max(Mathf.Abs(parentScale.z), 1e-4f));

            CreatePatch(rect);
            CreateLetter(rect);
        }

        /// <summary>
        /// Paints the patch the letter sits on.
        /// </summary>
        private void CreatePatch(RectTransform parent)
        {
            GameObject patchObject = new("Patch", typeof(RectTransform));
            patchObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)patchObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = patchObject.AddComponent<Image>();
            image.color = _markerColour;

            /* Load-bearing: an Image is the one graphic here that would happily swallow a click
             * meant for the FishNet demo buttons behind it. */
            image.raycastTarget = false;
        }

        /// <summary>
        /// Draws this zone's letter.
        /// </summary>
        private void CreateLetter(RectTransform parent)
        {
            GameObject letterObject = new("Letter", typeof(RectTransform));
            letterObject.transform.SetParent(parent, worldPositionStays: false);

            RectTransform rect = (RectTransform)letterObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            /* Configured after AddComponent, not before: TMP's Awake loads defaults off TMP
             * Settings, so anything set before it would be overwritten. */
            TextMeshProUGUI letter = letterObject.AddComponent<TextMeshProUGUI>();
            letter.raycastTarget = false;
            letter.textWrappingMode = TextWrappingModes.NoWrap;
            letter.alignment = TextAlignmentOptions.Center;
            letter.color = _letterColour;

            /* Sized off the patch rather than given a number, because the patch is sized off the
             * box and the two would otherwise have to be kept in step by hand. */
            letter.fontSize = rect.rect.height * _letterFraction;

            /* The same mapping the banner names sides with. A second letter table is how a team
             * ends up called two different things on the same screen. */
            letter.SetText(MatchBanner.TeamLetter(_team));

            if (_font != null)
            {
                letter.font = _font;
            }
            else
            {
                /* A warning and not an error, unlike the player tag: this letter is always Latin,
                 * so the built-in font draws it perfectly well. What is lost is the match with the
                 * rest of the project's typography, not the letter. */
                Debug.LogWarning(
                    $"{nameof(TeamZone)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned, " +
                    "so its zone letter will not match the project's other readouts. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }
        }

        /// <summary>
        /// Draws the place to stand for whoever is laying the scene out.
        /// </summary>
        /// <remarks>
        /// Only when selected, like the intake boxes. Four of these drawn all the time would be
        /// four translucent slabs over the office floor, and the one being adjusted would be
        /// indistinguishable from the three that are already right.
        /// </remarks>
        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.25f);
            Gizmos.DrawCube(_centre, _halfExtents * 2f);

            Gizmos.color = new Color(0.3f, 0.7f, 1f, 1f);
            Gizmos.DrawWireCube(_centre, _halfExtents * 2f);

            if (_spawn == null)
                return;

            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = new Color(0.3f, 1f, 0.3f, 1f);
            Gizmos.DrawLine(transform.position + _centre, _spawn.position);
            Gizmos.DrawWireSphere(_spawn.position, 0.25f);
        }
    }
}
