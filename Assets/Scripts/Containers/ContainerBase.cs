using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Containers
{
    /// <summary>
    /// Ordered storage for items that have left the physical world.
    /// </summary>
    /// <remarks>
    /// This is the whole premise of the container model: what is inside is no longer
    /// physical. Grid snapping, placement probes, pickup sectors and the cleaner all work
    /// on colliders in the scene, so none of them can see anything in here. Everything a
    /// player sees on a machine is a view rebuilt from this list, never an object.
    ///
    /// This class deliberately knows nothing about triggers. When a container counts as
    /// full enough to do something, what it produces, and how long that takes, belong to
    /// the station that owns it.
    ///
    /// Concrete rather than abstract, and deliberately not limited to one per object. A
    /// printer needs an input list and an output list, and a station that had to invent an
    /// empty subclass for each of them would be inventing it differently from the next
    /// station. Attach one wherever a list is needed and let the station hold the references.
    ///
    /// Order is meaningful and preserved: a queue consumes from the front, a stack from the
    /// back. Use the front/back helpers rather than indexing where the intent is one of
    /// those two, so the meaning is readable at the call site.
    /// </remarks>
    public class ContainerBase : NetworkBehaviour
    {
        /// <summary>
        /// How many entries fit. Zero or less means unlimited.
        /// </summary>
        [Tooltip("How many entries fit. Zero or less means unlimited.")]
        [SerializeField]
        private int _capacity;

        /// <summary>
        /// Ordered contents.
        /// </summary>
        /// <remarks>
        /// Must stay readonly: the weaver rejects a SyncType field that is assigned to.
        /// Never mutate an element through the indexer's return value — see the remarks on
        /// <see cref="ServerReplaceAt"/>.
        /// </remarks>
        private readonly SyncList<ContainerEntry> _contents = new();

        /// <summary>
        /// Raised once per change, on every peer, including the server.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. Views subscribe to this instead of to the SyncList
        /// so the duplicate-callback handling stays in one place.
        /// </remarks>
        public event Action ContentsChanged;

        /// <summary>
        /// How many entries fit; zero or less means unlimited.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// How many entries are held.
        /// </summary>
        public int Count => _contents.Count;

        /// <summary>
        /// True when there is no limit on entries.
        /// </summary>
        public bool IsUnlimited => _capacity <= 0;

        /// <summary>
        /// True when nothing more fits.
        /// </summary>
        public bool IsFull => !IsUnlimited && _contents.Count >= _capacity;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _contents.OnChange += OnContentsChanged;
        }

        public override void OnStopNetwork()
        {
            _contents.OnChange -= OnContentsChanged;

            base.OnStopNetwork();
        }

        /// <summary>
        /// Reads an entry by position in the list.
        /// </summary>
        public bool TryGetEntry(int index, out ContainerEntry entry)
        {
            if (index < 0 || index >= _contents.Count)
            {
                entry = default;
                return false;
            }

            entry = _contents[index];
            return true;
        }

        /// <summary>
        /// Server: appends an entry.
        /// </summary>
        /// <returns>False when the container is full.</returns>
        [Server]
        public bool ServerTryAdd(ContainerEntry entry)
        {
            if (IsFull)
                return false;

            _contents.Add(entry);
            return true;
        }

        /// <summary>
        /// Server: rewrites the entry at a position.
        /// </summary>
        /// <remarks>
        /// Read the entry with <see cref="TryGetEntry"/> first if the caller needs the old
        /// value. There is deliberately no overload with an out parameter: the [Server]
        /// guard returns early on a client, which would leave the parameter unassigned.
        ///
        /// The element must be read out, changed, and written back. TaskState-style structs
        /// cannot be edited through the indexer — that is a compile error — and reaching
        /// the backing list through GetCollection would compile while silently never
        /// marking the value dirty.
        /// </remarks>
        [Server]
        public bool ServerReplaceAt(int index, ContainerEntry entry)
        {
            if (index < 0 || index >= _contents.Count)
                return false;

            _contents[index] = entry;
            return true;
        }

        /// <summary>
        /// Server: removes the entry at a position.
        /// </summary>
        [Server]
        public bool ServerTryRemoveAt(int index)
        {
            if (index < 0 || index >= _contents.Count)
                return false;

            _contents.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Server: removes the entry nearest the front.
        /// </summary>
        /// <remarks>
        /// The shape a queue wants.
        /// </remarks>
        [Server]
        public bool ServerTryRemoveFirst()
        {
            if (_contents.Count == 0)
                return false;

            _contents.RemoveAt(0);
            return true;
        }

        /// <summary>
        /// Server: removes the entry nearest the back.
        /// </summary>
        /// <remarks>
        /// The shape a stack wants.
        /// </remarks>
        [Server]
        public bool ServerTryRemoveLast()
        {
            if (_contents.Count == 0)
                return false;

            _contents.RemoveAt(_contents.Count - 1);
            return true;
        }

        /// <summary>
        /// Server: empties the container.
        /// </summary>
        [Server]
        public void ServerClear() => _contents.Clear();

        /// <summary>
        /// Raises <see cref="ContentsChanged"/> exactly once per change on every peer.
        /// </summary>
        /// <remarks>
        /// A host receives every change twice: once as the server's own write
        /// (asServer true) and once as the echoed client read (asServer false). Letting both
        /// through rebuilds the view twice per change, which is visible as flicker, and
        /// doubles any side effect. Prefer the client echo whenever this peer runs a client
        /// — which covers hosts and pure clients — and fall back to the server call on a
        /// dedicated server, where no echo exists.
        /// </remarks>
        private void OnContentsChanged(SyncListOperation op, int index, ContainerEntry oldItem, ContainerEntry newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            ContentsChanged?.Invoke();
        }
    }
}
