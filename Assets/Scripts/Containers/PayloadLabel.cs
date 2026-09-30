using TMPro;
using UnityEngine;

namespace Overworked.Containers
{
    /// <summary>
    /// Fills in the parts of a payload that change from one instance to the next.
    /// </summary>
    /// <remarks>
    /// A payload prefab is a look, not a document. The Excel template is one prefab however many
    /// reports a round produces; what makes Excel 1 different from Excel 2 is the number drawn on
    /// it, and that is data. Baking the number into the prefab would mean a prefab per number,
    /// and the number has no ceiling — a catalogue that grows for the whole life of the game.
    ///
    /// The team goes the same way for the same reason. If it only tints the text, it is a value
    /// rather than a second prefab.
    ///
    /// Both fields are optional. A payload with no label — a sheet of blank paper, an ink
    /// cartridge — simply has no component of this type, and the grabbable skips it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PayloadLabel : MonoBehaviour
    {
        /// <summary>
        /// Text the document's number is written into.
        /// </summary>
        [Tooltip("Text the document's number is written into. Leave empty to ignore the number.")]
        [SerializeField]
        private TMP_Text _text;

        /// <summary>
        /// Text colour per team id; index 0 is team 0.
        /// </summary>
        [Tooltip("Text colour per team id; index 0 is team 0. Leave empty to leave the colour alone.")]
        [SerializeField]
        private Color[] _teamColours;

        /// <summary>
        /// Writes the number and tints the text.
        /// </summary>
        /// <param name="number">The document's number, or -1 to leave the text as authored.</param>
        /// <param name="team">The document's team, or -1 to leave the colour as authored.</param>
        public void SetVariant(int number, int team)
        {
            if (_text == null)
                return;

            if (number >= 0)
                _text.text = number.ToString();

            if (_teamColours == null || team < 0 || team >= _teamColours.Length)
                return;

            _text.color = _teamColours[team];
        }
    }
}
