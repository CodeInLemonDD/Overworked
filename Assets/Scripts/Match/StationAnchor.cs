using Overworked.Stations;
using UnityEngine;

namespace Overworked.Match
{
    /// <summary>
    /// Somewhere a station can be put when the office is set up.
    /// </summary>
    /// <remarks>
    /// **A place, not a thing.** This object is never seen and never moves: it is a position and
    /// a facing, and the station that lands on it is chosen at the start of the round. That split is
    /// what makes the random layout safe — the level designer decides where furniture *can* stand,
    /// and the round decides which piece stands there. Nothing is ever placed somewhere a person
    /// has not already looked at.
    ///
    /// The alternative — picking random positions in a region — has to answer questions this does
    /// not: is this spot inside a wall, is it on top of another station, is it in a doorway. Those
    /// are answerable, and the answers are a physics query and a retry loop that can still fail.
    /// An anchor cannot fail, because a person already checked.
    ///
    /// **An arrangement is a prefab.** One arrangement of the office is one prefab full of anchors,
    /// and the round instantiates one of them — see <see cref="OfficeLayout"/>. That is what makes
    /// several arrangements affordable to author: as scene objects they would all be standing in
    /// the same room at once, overlapping, with no way to look at one without the others.
    ///
    /// **Put one where a station could go and mark what it takes.** Anything the layout cannot find
    /// a station for is reported; see <see cref="OfficeLayout"/> for how many of each are needed.
    /// </remarks>
    [DisallowMultipleComponent]
    public class StationAnchor : MonoBehaviour
    {
        /// <summary>
        /// The station type this anchor will take, or empty for any.
        /// </summary>
        /// <remarks>
        /// A type name rather than a reference, because the thing being named is a *kind* of
        /// station and not one of the ones in the scene. A reference would tie the anchor to a
        /// particular object: drag a printer in, and the anchor would only ever accept that printer,
        /// which is the opposite of what a shuffled anchor is for.
        ///
        /// Empty means "anything fits", which is the right default for an office where most
        /// furniture is the same size. A typo is the failure to watch for — nothing will match, the
        /// anchor stays empty, and <see cref="OfficeLayout"/> says so in the log rather than leaving
        /// somebody to wonder why one corner of the room never fills up.
        /// </remarks>
        [Tooltip("Station type this anchor takes, matching the component name — 'Printer', 'Computer'. Leave empty for any.")]
        [SerializeField]
        private string _accepts = string.Empty;

        /// <summary>
        /// Whether a station may be put here.
        /// </summary>
        public bool Accepts(StationBase station)
        {
            if (station == null)
                return false;

            if (string.IsNullOrWhiteSpace(_accepts))
                return true;

            return string.Equals(_accepts.Trim(), station.GetType().Name, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What this anchor is written to accept, for the log.
        /// </summary>
        public string AcceptsDescription => string.IsNullOrWhiteSpace(_accepts) ? "any station" : _accepts.Trim();

        /// <summary>
        /// Where a station put here would stand.
        /// </summary>
        public Vector3 Position => transform.position;

        /// <summary>
        /// Which way a station put here would face.
        /// </summary>
        public Quaternion Rotation => transform.rotation;
    }
}
