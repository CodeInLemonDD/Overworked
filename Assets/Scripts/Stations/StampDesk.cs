using System.Collections.Generic;
using FishNet.Connection;
using Overworked.Documents;
using Overworked.Interaction;
using UnityEngine;

namespace Overworked.Stations
{
    /// <summary>
    /// The stamp: a blank sheet laid on it becomes finished paperwork.
    /// </summary>
    /// <remarks>
    /// **It is the only intake in the office that does not take anything.** A printer swallows its
    /// paper, a folder swallows documents, a customer swallows a folder — and a stamp desk leaves
    /// the sheet exactly where it is and marks it. That is not an inconsistency to be tidied away
    /// later: the other three consume because what they are handed stops being the thing the player
    /// wants back, and a stamped contract is the same sheet it was a moment ago, now worth
    /// something. Anything that ate it would be a machine that takes a contract and returns a
    /// contract, which is a printer.
    ///
    /// **The one station you hold the key for.** Put the contract down on it — it is a table, and
    /// the contract snaps to it like anything else left there — then stand at the desk and hold E
    /// for a second. A tap takes the contract back, because that is what a tap means everywhere
    /// else in the office; only the hold reaches the desk.
    ///
    /// **That distinction is the whole reason a station can state a hold time.** See
    /// <see cref="StationBase.HoldSeconds"/> and <see cref="Interaction.PlayerInteraction"/>: at a
    /// machine, a tap and a hold used to mean the same thing, and the machine was only reached when
    /// there was nothing in front of it to pick up. A desk with a contract on it is exactly the case
    /// that breaks — the thing you want to use and the thing you would pick up are in the same place.
    ///
    /// **It never had a verb before, and it does now.** The project's rule is that an intake station
    /// has no active verb, because E while carrying goes to dropping rather than to the station — so
    /// this was built the other way round, stamping whatever came to rest on it. That worked and was
    /// wrong in the same breath: putting a contract down is not signing it, and a machine that signs
    /// on its own takes the decision away from the player. What makes a press reachable here is not
    /// the carrying rule changing, but the player having put the contract down first.
    ///
    /// **Blank means blank in both senses.** The printer leaves an unstamped sheet carrying no
    /// number, so what lies on the desk looks like what it is; this writes the number and the team
    /// back on as it stamps. See <see cref="NetworkGrabbable.IsStamped"/>, which is what a folder
    /// refuses, and which is therefore the whole of why an unsigned contract cannot be delivered.
    ///
    /// **Every unstamped sheet is stamped, with no question asked about its kind.** Only the printer
    /// makes those, and it only makes them for kinds whose catalogue entry asks for a stamp, so
    /// there is nothing to check — and re-checking would mean this machine knew about the catalogue
    /// for no gain. A sheet that is already stamped is left alone, which costs one comparison.
    ///
    /// **Its own collider has to stop at the desk surface, and this is the one station where that
    /// matters.** Every other intake takes what it is given out of the world — the printer swallows
    /// the sheet, a folder swallows the document, a customer swallows the whole folder — so an
    /// object that briefly overlaps a machine's collider is gone before anything resolves it. This
    /// one leaves the sheet exactly where it is, and a collider taller than the desk means the sheet
    /// is set down *inside* it: physics resolves the overlap by pushing it out, and it leaves at
    /// whatever speed that takes.
    ///
    /// The symptom is a contract that flies off the desk the moment it is put down. That reads as
    /// the physics engine misbehaving, not as a collider nobody sized, and it is worth saying here
    /// because the obvious first move is to go looking in this file — where there is nothing to
    /// find. Author the collider to the desktop and no higher: not over the stamp and the pen
    /// standing on it, which would put the invisible wall back.
    /// </remarks>
    [DisallowMultipleComponent]
    public class StampDesk : StationBase
    {
        /// <summary>
        /// Middle of the box a sheet has to be lying in, in this object's own space.
        /// </summary>
        /// <remarks>
        /// Same arrangement as every other intake: expressed locally so it follows the desk round,
        /// and raised above the pivot because a station's root sits on the floor beneath its table.
        /// </remarks>
        [Tooltip("Middle of the box, in this object's own space. Raised above the desk surface.")]
        [SerializeField]
        private Vector3 _intakeCentre = new(0f, 0.7f, 0f);

        /// <summary>
        /// Half the size of the box, in this object's own space.
        /// </summary>
        [Tooltip("Half the size of the box. Drawn as a gizmo when this object is selected.")]
        [SerializeField]
        private Vector3 _intakeHalfExtents = new(0.45f, 0.35f, 0.45f);

        /// <summary>
        /// How long the press has to be held for the stamp to take, in seconds.
        /// </summary>
        /// <remarks>
        /// **One second, and it is the only place in the game where the length of a press is the
        /// mechanic.** Every other machine answers to a tap because the verb is unambiguous — take a
        /// sheet, open the panel, hand over the folder. Stamping is the one action in the office
        /// that is worth a moment's deliberate effort, and it is also the one that takes something
        /// the player already made and changes it, so it should feel like a decision rather than
        /// like brushing past a table.
        ///
        /// It is read on every peer, through <see cref="StationBase.HoldSeconds"/>, to decide whether
        /// a press belongs to this desk or to the contract lying on it. See
        /// <see cref="Interaction.PlayerInteraction"/>.
        /// </remarks>
        [Tooltip("Seconds the press must be held for the stamp to take.")]
        [Min(0f)]
        [SerializeField]
        private float _stampSeconds = 1f;

        /// <summary>
        /// How long a press has to be held before this desk will take it rather than let the
        /// contract lying on it be picked up.
        /// </summary>
        public override float HoldSeconds => _stampSeconds;

        /// <summary>
        /// Reused by the scan, so a press allocates nothing.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        /// <summary>
        /// Server: stamps everything lying on the desk.
        /// </summary>
        /// <remarks>
        /// **Driven by a press, not by a clock.** It used to run every frame and stamp whatever
        /// turned up in its box, which worked and was wrong: putting a contract down is not the same
        /// act as signing it, and a machine that does the second one on its own takes the decision
        /// away from the player. See <see cref="_stampSeconds"/>.
        ///
        /// The length is checked here as well as on the client that sent it, because the client's
        /// answer is not evidence — see <see cref="StationBase.ServerInteract"/>. Too short is
        /// answered with nothing at all: no refusal, no message. Stamping is never urgent, and the
        /// player is still holding the key down, so a complaint would arrive in the middle of them
        /// doing it correctly.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, float heldSeconds)
        {
            if (heldSeconds < _stampSeconds)
                return;

            IntakeVolume.CollectInside(NetworkManager, transform, _intakeCentre, _intakeHalfExtents, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
                TryStamp(_scanBuffer[i]);

            _scanBuffer.Clear();
        }

        /// <summary>
        /// Server: finishes one sheet, if it is blank.
        /// </summary>
        /// <remarks>
        /// The number and the team come out of the record rather than off the object, because the
        /// object is carrying no number — that is what being blank means — and the record is where
        /// the printer found them in the first place. Writing the variant is what puts the number on
        /// the paper; the stamp flag is what lets it into a folder.
        ///
        /// A sheet whose document the store cannot resolve is left blank rather than reported. It
        /// means the store has not replicated yet, which is a state this machine shares with every
        /// other reader of it, and the sheet is still lying there to be stamped on the next frame.
        /// </remarks>
        private void TryStamp(NetworkGrabbable grabbable)
        {
            if (grabbable == null || grabbable.IsStamped)
                return;

            DocumentStore store = DocumentStore.Instance;

            if (store == null || !store.TryGet(grabbable.DataId, out DocumentRecord record))
                return;

            grabbable.ServerSetVariant(record.Number, record.Team);
            grabbable.ServerSetStamped(true);
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            /* The box a sheet has to be inside. Anything else about a stamp desk is a model, and a
             * model is not worth drawing a wireframe over. */
            Gizmos.color = new Color(0.9f, 0.6f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(_intakeCentre, _intakeHalfExtents * 2f);

            Gizmos.matrix = previous;
        }
    }
}
