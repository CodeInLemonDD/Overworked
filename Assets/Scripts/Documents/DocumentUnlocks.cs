using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Overworked.Documents
{
    /// <summary>
    /// Which kinds of document this round has opened up.
    /// </summary>
    /// <remarks>
    /// One of these per session, on a scene NetworkObject, alongside <see cref="DocumentStore"/>.
    /// The server writes, every peer reads.
    ///
    /// **The store and this answer different questions, which is why they are two components.**
    /// The store holds documents that *exist* — instances, each with a number, created when
    /// somebody prints one. This holds kinds that may be *asked for* — the catalogue's indices,
    /// decided before anything is printed. A document can exist for a kind that is not unlocked
    /// (something created it directly) and a kind can be unlocked with no document behind it,
    /// which is the normal state of a round that has not started producing yet.
    ///
    /// **The list only grows.** Nothing is ever re-locked, so membership is the whole of the
    /// state and there is no ordering to keep. That is why it is a list of indices rather than a
    /// dictionary or a set: the same type <see cref="DocumentStore"/> and
    /// <see cref="Containers.ContainerBase"/> already use, and an item goes over the wire through
    /// the very path a <c>SyncVar&lt;int&gt;</c> already uses — which the printer runs on, so it
    /// is not a new thing to be right about. A linear scan over a handful of entries is cheaper
    /// than the machinery to avoid it.
    ///
    /// **One change, one event — but a reset is several changes.** <see cref="ServerResetUnlocks"/>
    /// clears and then adds once per spec, and every one of those raises
    /// <see cref="UnlockedChanged"/>. A view that rebuilds on this event will therefore rebuild a
    /// handful of times inside one frame when a round restarts. That is not worth machinery to
    /// avoid; it is worth knowing before wondering why the panel flickers on startup.
    ///
    /// **A null <see cref="Instance"/> means "nothing is locked".** Callers read it that way on
    /// purpose: a scene with no unlocks component should behave exactly as it did before this
    /// existed, rather than having every machine refuse everything. The failure that hides is
    /// "locks do not apply", whose symptom is a panel with nothing greyed out; the failure that
    /// would hide if this refused instead is "the computer stopped working", which looks like a
    /// station nobody wired — a much more expensive thing to go and find.
    ///
    /// The contrast worth knowing: a component that *is* present but has no catalogue assigned
    /// goes the other way and locks everything, with an error at startup. That is deliberate —
    /// an unset field is a mistake on a component that exists, not a scene that predates it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DocumentUnlocks : NetworkBehaviour
    {
        /// <summary>
        /// The catalogue this machine's indices mean something in.
        /// </summary>
        /// <remarks>
        /// Needed to answer two questions the list cannot: whether an index exists at all, and
        /// which kinds start open. Both are properties of the asset rather than of the round, so
        /// they are read from it rather than copied into replicated state.
        /// </remarks>
        [Tooltip("The same DocumentCatalogue the computers offer. Seeds what starts unlocked, and rejects indices that do not exist.")]
        [SerializeField]
        private DocumentCatalogue _catalogue;

        /// <summary>
        /// The unlocks in force, as catalogue indices, in the order they were opened.
        /// </summary>
        /// <remarks>
        /// Must stay readonly: the weaver rejects a SyncType field that is assigned to.
        /// </remarks>
        private readonly SyncList<int> _unlocked = new();

        /// <summary>
        /// The unlocks in the scene, or null before they have spawned — and null for good on a
        /// scene that has none. See the class remarks for what callers should do with that.
        /// </summary>
        public static DocumentUnlocks Instance { get; private set; }

        /// <summary>
        /// Raised once per change, on every peer, including the server.
        /// </summary>
        /// <remarks>
        /// Local only, never networked. Same shape and same duplicate-callback handling as
        /// <see cref="Containers.ContainerBase.ContentsChanged"/>, so a view has one pattern.
        /// </remarks>
        public event Action UnlockedChanged;

        /// <summary>
        /// How many kinds are open.
        /// </summary>
        public int UnlockedCount => _unlocked.Count;

        /// <summary>
        /// Reads an unlock by its position in the list.
        /// </summary>
        /// <remarks>
        /// Position in this list, not a catalogue index — see <see cref="TryGetUnlocked"/>.
        /// </remarks>
        public bool TryGetUnlocked(int index, out int specIndex)
        {
            if (index < 0 || index >= _unlocked.Count)
            {
                specIndex = -1;
                return false;
            }

            specIndex = _unlocked[index];
            return true;
        }

        /// <summary>
        /// True when a catalogue index may be asked for.
        /// </summary>
        /// <remarks>
        /// False for an index that does not exist, as well as for one that is merely still
        /// locked. The caller's answer to either is the same — do not offer it — and separating
        /// them would invite somebody to report the first as a problem when the second is the
        /// normal state of the round.
        /// </remarks>
        public bool IsUnlocked(int specIndex)
        {
            if (_catalogue == null || !_catalogue.TryGet(specIndex, out _))
                return false;

            for (int i = 0; i < _unlocked.Count; i++)
            {
                if (_unlocked[i] == specIndex)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Server: opens a kind of document.
        /// </summary>
        /// <remarks>
        /// This is the whole interface the rest of the game needs. Whatever ends up deciding that
        /// a player has earned a document — an NPC handing over a contract, a milestone, a debug
        /// command — calls this and nothing else; the panel and the machines never learn where it
        /// came from, because they have no business knowing.
        /// </remarks>
        /// <returns>False when the index is not a spec, or was already open.</returns>
        [Server]
        public bool ServerUnlock(int specIndex)
        {
            if (_catalogue == null || !_catalogue.TryGet(specIndex, out _))
                return false;
            if (IsUnlocked(specIndex))
                return false;

            _unlocked.Add(specIndex);
            return true;
        }

        /// <summary>
        /// Server: forgets every unlock and opens the ones the catalogue says start open.
        /// </summary>
        /// <remarks>
        /// Clearing first is what makes this safe to call at startup as well as by hand. A
        /// SyncList keeps its contents across sessions — nothing resets it — so seeding without
        /// clearing would append this round's starting set to whatever the last run left behind,
        /// and the list would grow by one set per play. See the same reasoning on
        /// <see cref="Stations.Computer"/>'s fetch list.
        /// </remarks>
        [Server]
        public void ServerResetUnlocks()
        {
            _unlocked.Clear();

            if (_catalogue == null)
                return;

            for (int i = 0; i < _catalogue.Count; i++)
            {
                if (_catalogue.TryGet(i, out DocumentCatalogue.Spec spec) && spec.UnlockedAtStart)
                    _unlocked.Add(i);
            }
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _unlocked.OnChange += OnUnlockedChanged;

            /* Two of these in one scene is a wiring mistake whose only symptom would be a panel
             * that disagrees with itself about what is on offer. */
            if (Instance != null && Instance != this)
            {
                Debug.LogError(
                    $"{nameof(DocumentUnlocks)} on {gameObject.name} found another one already running on {Instance.gameObject.name}. There must be exactly one; unlocks will disagree.",
                    this);
            }

            Instance = this;
        }

        public override void OnStopNetwork()
        {
            _unlocked.OnChange -= OnUnlockedChanged;

            if (Instance == this)
                Instance = null;

            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            /* Loud, and deliberately the strict direction. A component that is present with no
             * catalogue is a mistake on something that exists, not a scene that predates this
             * feature — so it locks everything and says so, rather than quietly behaving like the
             * version before it. Contrast the class remarks on a missing component. */
            if (_catalogue == null)
            {
                Debug.LogError(
                    $"{nameof(DocumentUnlocks)} on {gameObject.name} has no {nameof(DocumentCatalogue)} assigned, so nothing is unlocked and no document can be obtained. Assign the same asset the computers use.",
                    this);
            }

            ServerResetUnlocks();
        }

        /// <summary>
        /// Raises <see cref="UnlockedChanged"/> exactly once per change on every peer.
        /// </summary>
        /// <remarks>
        /// A host receives every change twice — once as the server's own write and once as the
        /// echoed client read — and letting both through rebuilds whatever is watching twice.
        /// Same handling as <see cref="Containers.ContainerBase"/>.
        /// </remarks>
        private void OnUnlockedChanged(SyncListOperation op, int index, int oldItem, int newItem, bool asServer)
        {
            if (asServer && IsClientStarted)
                return;

            UnlockedChanged?.Invoke();
        }
    }
}
