using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Timing;
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
    /// **So there is no press here, and there cannot be one.** E on a station is only reached when
    /// there was nothing to pick up, so a player holding a contract can never press E on a desk —
    /// see CONSTRAINTS.md, "进料型工位没有主动动词". The paper has to be put down, which is what
    /// makes this read as stamping something rather than as using a machine.
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
        /// Reused by the scan, so a frame allocates nothing.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        /// <summary>
        /// The TimeManager this object subscribed to.
        /// </summary>
        private TimeManager _timeManager;

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Subscribed by hand rather than through TickNetworkBehaviour, for the reason the
             * printer and the scoreboard both give. There is no clock here — nothing about stamping
             * takes time — so this is only the frame to look in the box on. */
            _timeManager = TimeManager;
            if (_timeManager != null)
                _timeManager.OnUpdate += ServerUpdate;
        }

        public override void OnStopServer()
        {
            if (_timeManager != null)
            {
                _timeManager.OnUpdate -= ServerUpdate;
                _timeManager = null;
            }

            base.OnStopServer();
        }

        /// <summary>
        /// Server: stamps whatever is lying on the desk.
        /// </summary>
        /// <remarks>
        /// A sheet that is only passing through gets stamped too — it is inside the box for a frame
        /// or two on its way past, and being thrown at the stamp is as clear an intent as being laid
        /// on it. Narrowing the test to objects that have come to rest would be a rule about intent
        /// that the box is already expressing.
        /// </remarks>
        private void ServerUpdate()
        {
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

        /// <summary>
        /// Does nothing, and that is the design rather than an omission.
        /// </summary>
        /// <remarks>
        /// A press only reaches a station when there was nothing in reach to pick up, so a player
        /// carrying a contract cannot press this at all — and a player who is not carrying one has
        /// nothing to stamp. What E does at a desk is pick up whatever is lying on it, which is the
        /// sheet they just stamped. See the class remarks.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)
        {
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
