using Overworked.Interaction;
using UnityEngine;

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
