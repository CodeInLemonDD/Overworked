using System.Collections.Generic;
using Overworked.Containers;
using FishNet.Object;
using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// Lets a folder lying in the world take documents in.
    /// </summary>
    /// <remarks>
    /// A folder is a plate. It is put down, documents are dropped or thrown onto it, and it is
    /// picked up again carrying them. That is the whole of it, and it is why the intake only runs
    /// while the folder is **not** in someone's hands: a folder that swept documents up as it was
    /// carried would be a vacuum, and everything about which document went in where would stop
    /// being something the player decided.
    ///
    /// **Nothing hands a document in.** There is no key for it, and there cannot be one in the
    /// shape the project is in: E is read by the player, and a press only becomes a station
    /// interaction when there is nothing in reach to pick up — so an object that can be picked up
    /// can never also be the target of a press. See CONSTRAINTS.md, "进料型工位没有主动动词".
    /// Everything that takes material in watches a box instead.
    ///
    /// This component sits on the one object prefab every grabbable is made from, so it is on
    /// paper and cartridges too, and only the folders do anything. That is what the gate below is
    /// for — it is also what keeps the scan, which walks every grabbable in the world, off the
    /// frame for everything that is not a folder.
    ///
    /// What goes in is a **document**, recognised by carrying an id into
    /// <see cref="Documents.DocumentStore"/>. That id is the point: an object written down as an
    /// entity would keep its look and lose its number, and a folder of unnamed sheets is worth
    /// nothing to a customer who asked for Excel 3.
    ///
    /// There is deliberately no way to take a document back out. A container that only fills has
    /// to be unlimited, or a player can jam it with a legal action and never recover — so this one
    /// is unlimited, and a mistake costs the document. That is a real cost and it is the honest
    /// one for now; the round that adds delivery is the round that will have to decide whether a
    /// folder needs to be emptied.
    /// </remarks>
    [DisallowMultipleComponent]
    public class FolderIntake : MonoBehaviour
    {
        /// <summary>
        /// Middle of the intake box, in this object's own space.
        /// </summary>
        [Tooltip("Middle of the intake box, relative to the folder. Roughly where its opening is.")]
        [SerializeField]
        private Vector3 _centre = new(0f, 0.06f, 0f);

        /// <summary>
        /// Half the size of the intake box, in this object's own space.
        /// </summary>
        /// <remarks>
        /// Deliberately generous. This box is the whole of how a document is handed in, and a
        /// target the size of the folder's lid would make filing a game of aiming — the drop has
        /// to be able to be approximate for the mechanic to read as "put it in the folder" rather
        /// than "hit the folder".
        /// </remarks>
        [Tooltip("Half the size of the intake box. Generous on purpose: this is the target the player drops onto.")]
        [SerializeField]
        private Vector3 _halfExtents = new(0.28f, 0.22f, 0.32f);

        /// <summary>
        /// The grabbable this folder is, used for its payload and its state.
        /// </summary>
        private NetworkGrabbable _grabbable;

        /// <summary>
        /// Where documents go.
        /// </summary>
        private ContainerBase _container;

        /// <summary>
        /// Reused list of candidates, so the scan allocates nothing.
        /// </summary>
        private readonly List<NetworkGrabbable> _scanBuffer = new();

        private void Awake()
        {
            /* Looked up rather than serialized. Both are on this same object — the prefab is a
             * root with a handful of components and no children that matter — so a field here
             * would be a second place for the same fact, and one that can be left empty. */
            _grabbable = GetComponent<NetworkGrabbable>();
            _container = GetComponent<ContainerBase>();

            if (_grabbable == null)
            {
                Debug.LogError(
                    $"{nameof(FolderIntake)} on {gameObject.name} found no {nameof(NetworkGrabbable)} on the same object, so it has no folder to file into.",
                    this);
            }

            if (_container == null)
            {
                Debug.LogError(
                    $"{nameof(FolderIntake)} on {gameObject.name} found no {nameof(ContainerBase)} on the same object, so a document going in would have nowhere to go.",
                    this);
            }
        }

        /// <summary>
        /// Server: files whatever is lying in the box.
        /// </summary>
        /// <remarks>
        /// Plain Update rather than a network tick callback: this object is a spawned grabbable,
        /// not a station, and there is no timer here — just a box to look in once a frame. The
        /// work below is skipped on every peer that is not the server.
        /// </remarks>
        private void Update()
        {
            if (_grabbable == null || _container == null)
                return;

            if (!_grabbable.IsSpawned || !_grabbable.IsServerInitialized)
                return;

            /* The gate. Everything made from this prefab runs this method, and only folders go
             * past the first line — which also keeps every non-folder off the scan below. */
            if (!_grabbable.IsContainer)
                return;

            /* A folder in someone's hands is being carried, not used. See the class remarks. */
            if (_grabbable.State == GrabbableState.Held)
                return;

            GrabbableSpawner.CollectLooseGrabbables(_grabbable.NetworkManager, _scanBuffer);

            for (int i = 0; i < _scanBuffer.Count; i++)
                TryFile(_scanBuffer[i]);

            _scanBuffer.Clear();
        }

        /// <summary>
        /// Server: files one object if it is a document and it is in the box.
        /// </summary>
        /// <remarks>
        /// The order of the checks is the order of how cheap they are, and the last one is the
        /// only one that changes the world.
        /// </remarks>
        /// <returns>True when the document went in.</returns>
        private bool TryFile(NetworkGrabbable grabbable)
        {
            if (grabbable == null || grabbable == _grabbable)
                return false;

            /* A document, and nothing else. A folder is not a document, so a folder cannot be put
             * into a folder — which is what keeps this from being a way to make a container's
             * contents disappear, since anything stored is despawned and only an entry survives. */
            int dataId = grabbable.DataId;
            if (dataId < 0)
                return false;

            if (!IsInsideBox(grabbable.transform.position))
                return false;

            /* Full, or no container to speak of: the document stays in the world where its owner
             * can still pick it back up. The same rule every intake in the project follows —
             * nothing fed to a machine is destroyed for want of a slot, or for a mistake in the
             * prefab. */
            if (!_container.ServerTryAdd(ContainerEntry.ForData(dataId)))
                return false;

            /* Destroy rather than pool: these objects carry per-life state that a recycled
             * instance would bring back with it. Passed explicitly so this does not depend on the
             * prefab. Same as the printer's intake. */
            grabbable.NetworkObject.Despawn(DespawnType.Destroy);
            return true;
        }

        /// <summary>
        /// True when a world position falls inside the box.
        /// </summary>
        /// <remarks>
        /// Measured in this object's own space, so the box turns and moves with the folder rather
        /// than being a fixed patch of floor.
        /// </remarks>
        private bool IsInsideBox(Vector3 worldPosition)
        {
            Vector3 offset = transform.InverseTransformPoint(worldPosition) - _centre;

            return Mathf.Abs(offset.x) <= _halfExtents.x
                && Mathf.Abs(offset.y) <= _halfExtents.y
                && Mathf.Abs(offset.z) <= _halfExtents.z;
        }
    }
}
