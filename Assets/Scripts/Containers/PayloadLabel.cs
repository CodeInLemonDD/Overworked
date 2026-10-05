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
    /// it, and that is data. Baking the number into the prefab would mean a prefab per number, and
    /// the number has no ceiling — a catalogue that grows for the whole life of the game.
    ///
    /// The team goes the same way for the same reason. If it only tints, it is a value rather than
    /// a second prefab.
    ///
    /// **The team goes into whichever of two places the payload has, and a payload may have
    /// either, both, or neither.** A printed document has text, so the team is the colour of the
    /// text. A folder has no text at all — it is three slabs of mesh — so its team has to be the
    /// colour of one of those slabs. Without that second path a folder has no marking on it
    /// whatsoever, and the only way to find out whose it is is to try to put something in it and
    /// see whether it is accepted. Both paths are the same fact arriving in two media, so both
    /// belong here: a second component would be a second thing the grabbable has to know to talk
    /// to, and the one that got forgotten would fail silently.
    ///
    /// Every field is optional. A payload with no label — a sheet of blank paper, an ink cartridge
    /// — simply has no component of this type, and the grabbable skips it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PayloadLabel : MonoBehaviour
    {
        /// <summary>
        /// What a surface is tinted through when nothing else is configured.
        /// </summary>
        /// <remarks>
        /// The universal pipeline's Lit shader, which is what every payload in this project is
        /// made of. The built-in Standard shader calls the same thing <c>_Color</c>, so a material
        /// swap would need the field changed — and would do nothing at all until it was, which is
        /// worth knowing before spending time looking for a bug in the tinting code.
        /// </remarks>
        private const string DefaultColourProperty = "_BaseColor";

        /// <summary>
        /// Every text the document's number is written into.
        /// </summary>
        /// <remarks>
        /// An array because one label is often drawn twice — the real one and a backing copy
        /// behind it — and both have to say the same thing.
        /// </remarks>
        [Tooltip("Every text the document's number is written into. Leave empty to ignore the number.")]
        [SerializeField]
        private TMP_Text[] _texts;

        /// <summary>
        /// Surfaces tinted with the team colour.
        /// </summary>
        /// <remarks>
        /// For a payload with no text of its own. A folder is the reason this exists: it carries a
        /// team and has nothing to write it on, so the marking has to be the colour of the folder.
        ///
        /// Tinted through a property block rather than by changing the material, which matters:
        /// every folder in the office shares one material asset, so writing a colour into it would
        /// recolour all of them — including the ones already in people's hands — to whichever team
        /// was stamped last.
        /// </remarks>
        [Tooltip("Surfaces tinted with the team colour. For a payload with no text — a folder. Leave empty for none.")]
        [SerializeField]
        private Renderer[] _tinted;

        /// <summary>
        /// The material colour property the tinted surfaces are written through.
        /// </summary>
        [Tooltip("Material colour property to tint. '_BaseColor' for URP Lit.")]
        [SerializeField]
        private string _colourProperty = DefaultColourProperty;

        /// <summary>
        /// Text colour per team id; index 0 is team 0.
        /// </summary>
        /// <remarks>
        /// Drives both paths — the texts and the tinted surfaces — so a team is one colour across
        /// everything it appears on. Two arrays would be two places to get a team's colour wrong,
        /// and the wrong one would be the one nobody was looking at.
        /// </remarks>
        [Tooltip("Colour per team id; index 0 is team 0. Applied to the texts and to the tinted surfaces. Leave empty to leave the colour alone.")]
        [SerializeField]
        private Color[] _teamColours;

        /// <summary>
        /// Reused so tinting a surface allocates nothing.
        /// </summary>
        private MaterialPropertyBlock _block;

        /// <summary>
        /// Writes the number and tints whatever is there to tint.
        /// </summary>
        /// <param name="number">
        /// The document's number, or -1 for a sheet that has none to show — a contract that has not
        /// been stamped yet, which is blank until it is.
        /// </param>
        /// <param name="team">The document's team, or -1 to leave the colour as authored.</param>
        public void SetVariant(int number, int team)
        {
            /* -1 is not "team zero" and not an error: it is what a material carries — paper, ink,
             * a cartridge — and a sheet of paper tinted by faction would be a claim about who owns
             * something that comes out of a shared pool. See SupplyBox, which is where the decision
             * to stamp a folder and not a ream is made. */
            bool tint = _teamColours != null && team >= 0 && team < _teamColours.Length;

            Color colour = tint ? _teamColours[team] : default;

            /* **No number clears the text rather than leaving it alone.** This used to treat -1 as
             * "as authored", which is the right reading for a payload that does not use numbers —
             * except that such a payload has no component of this type at all, so the case cannot
             * arise. What -1 actually means on a payload that does have one is "there is nothing to
             * write here", and the two readings only look alike until the first kind of document
             * that starts life blank: an unstamped contract came out showing the placeholder the
             * prefab was authored with, which is a number that belongs to nobody. */
            string text = number >= 0 ? number.ToString() : string.Empty;

            if (_texts != null)
            {
                foreach (TMP_Text label in _texts)
                {
                    if (label == null)
                        continue;

                    label.text = text;

                    if (tint)
                        label.color = colour;
                }
            }

            TintSurfaces(colour, tint);
        }

        /// <summary>
        /// Puts the team colour on the tinted surfaces, or takes it back off.
        /// </summary>
        /// <remarks>
        /// Cleared rather than overwritten with white when there is no team, so that a payload
        /// whose authored colour is not white keeps it. Setting the block to null is the documented
        /// way to say "this renderer has no overrides", and it costs the same as writing one.
        ///
        /// A property name the shader does not have is a silent no-op — the renderer simply keeps
        /// its authored colour and nothing anywhere says so. That is left alone deliberately: the
        /// failure is a folder that is the wrong colour, which is on screen in front of whoever
        /// configured it, unlike the silent failures this project spends words on.
        /// </remarks>
        private void TintSurfaces(Color colour, bool tint)
        {
            if (_tinted == null || _tinted.Length == 0)
                return;

            for (int i = 0; i < _tinted.Length; i++)
            {
                Renderer surface = _tinted[i];

                if (surface == null)
                    continue;

                if (!tint)
                {
                    surface.SetPropertyBlock(null);
                    continue;
                }

                _block ??= new MaterialPropertyBlock();

                string property = string.IsNullOrWhiteSpace(_colourProperty) ? DefaultColourProperty : _colourProperty;
                _block.SetColor(property, colour);

                surface.SetPropertyBlock(_block);
            }
        }
    }
}
