using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace Overworked.Interaction
{
    /// <summary>
    /// Spawns the initial set of grabbable objects when the server starts.
    /// </summary>
    /// <remarks>
    /// Server-only in effect: the spawn event only fires on the server, and
    /// ServerManager.Spawn is a server operation. Clients receive the objects through the
    /// normal spawn path.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GrabbableSpawner : MonoBehaviour
    {
        /// <summary>
        /// The grabbable prefab to spawn. Must be registered in the spawnable prefabs collection.
        /// </summary>
        [Tooltip("The grabbable prefab to spawn. Must be registered in the spawnable prefabs collection.")]
        [SerializeField]
        private NetworkObject _objectPrefab;

        /// <summary>
        /// How many objects to spawn.
        /// </summary>
        [Tooltip("How many objects to spawn.")]
        [Min(0)]
        [SerializeField]
        private int _spawnCount = 4;

        /// <summary>
        /// Cell to start at; consecutive objects take consecutive cells along X.
        /// </summary>
        [Tooltip("Cell to start at; consecutive objects take consecutive cells along X.")]
        [SerializeField]
        private Vector2Int _originCell = new(-2, -2);

        /// <summary>
        /// Height to spawn at. Objects fall to whatever is beneath them.
        /// </summary>
        [Tooltip("Height to spawn at. Objects fall to whatever is beneath them.")]
        [SerializeField]
        private float _spawnHeight = 1.5f;

        /// <summary>
        /// Which payload every spawned object wears.
        /// </summary>
        /// <remarks>
        /// -1 leaves the prefab exactly as authored, which is what this did before payloads
        /// existed. Set it to a catalogue index to open the round with that kind of object
        /// already in play — paper, so a printer has something to eat without a paper box
        /// standing there, for instance.
        ///
        /// It is an index into the same catalogue everything else uses, so it means the same
        /// thing here as it does in a container entry.
        /// </remarks>
        [Tooltip("Payload every spawned object wears, or -1 for the prefab's authored look.")]
        [Min(-1)]
        [SerializeField]
        private int _payloadIndex = -1;

        /// <summary>
        /// NetworkManager this spawner belongs to.
        /// </summary>
        private NetworkManager _networkManager;

        /// <summary>
        /// The live instance, so systems that need to create an object can find the prefab
        /// without a scene lookup of their own.
        /// </summary>
        public static GrabbableSpawner Instance { get; private set; }

        /// <summary>
        /// The grabbable prefab every object is made from.
        /// </summary>
        public NetworkObject ObjectPrefab => _objectPrefab;

        private void Awake()
        {
            Instance = this;

            _networkManager = GetComponent<NetworkManager>();
            if (_networkManager == null)
                _networkManager = GetComponentInParent<NetworkManager>();
            if (_networkManager == null)
                _networkManager = InstanceFinder.NetworkManager;
        }

        private void Start()
        {
            /* Start rather than OnEnable: FishNet creates ServerManager lazily, so it may
             * not exist yet while components are still enabling. */
            if (_networkManager == null)
            {
                Debug.LogError($"{nameof(GrabbableSpawner)} on {gameObject.name} could not find a NetworkManager.", this);
                return;
            }

            _networkManager.ServerManager.OnServerConnectionState += ServerManager_OnServerConnectionState;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            if (_networkManager != null)
                _networkManager.ServerManager.OnServerConnectionState -= ServerManager_OnServerConnectionState;
        }

        private void ServerManager_OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started)
                return;

            SpawnAll();
        }

        /// <summary>
        /// Spawns the configured number of objects on consecutive grid cells.
        /// </summary>
        private void SpawnAll()
        {
            if (_objectPrefab == null)
            {
                Debug.LogError($"{nameof(GrabbableSpawner)} on {gameObject.name} has no object prefab assigned; nothing will be spawned.", this);
                return;
            }

            for (int i = 0; i < _spawnCount; i++)
            {
                Vector2Int cell = new(_originCell.x + i, _originCell.y);
                Vector3 position = WorldGrid.CellCentre(cell);
                position.y = _spawnHeight;

                /* Through the same entry point everything else uses, so there is one place that
                 * knows how an object is made rather than two that can drift apart. No owner:
                 * the server simulates it until someone picks it up. */
                SpawnGrabbable(_payloadIndex, position, Quaternion.identity);
            }
        }

        /// <summary>
        /// Server: creates one grabbable wearing the given payload.
        /// </summary>
        /// <remarks>
        /// The single place an object is created after the opening set, so stations,
        /// containers and folders cannot drift apart on how a real object is made.
        ///
        /// The payload is set before the spawn rather than after, so it travels inside the
        /// spawn message. Applying it later means swapping the look on an object that is
        /// already moving, and re-measuring the geometry moves the transform to the origin
        /// to measure it — harmless at spawn, visible mid-flight.
        /// </remarks>
        /// <param name="payloadIndex">
        /// Index into the payload catalogue, or -1 to leave the prefab as authored.
        /// </param>
        /// <param name="owner">
        /// Connection to hand ownership to, or null to leave the object with the server.
        /// </param>
        /// <param name="variantNumber">
        /// The document number drawn on the payload, or -1 to leave the text as authored.
        /// </param>
        /// <param name="variantTeam">
        /// The document's team, which tints the label, or -1 to leave the colour as authored.
        /// </param>
        /// <returns>The spawned object, or null when it could not be created.</returns>
        public static NetworkObject SpawnGrabbable(
            int payloadIndex,
            Vector3 position,
            Quaternion rotation,
            NetworkConnection owner = null,
            int variantNumber = -1,
            int variantTeam = -1)
        {
            GrabbableSpawner spawner = Instance;
            if (spawner == null || spawner._objectPrefab == null)
            {
                Debug.LogError($"{nameof(SpawnGrabbable)} found no {nameof(GrabbableSpawner)} with an object prefab assigned; nothing was spawned.");
                return null;
            }

            NetworkManager manager = spawner._networkManager;
            if (manager == null || !manager.IsServerStarted)
                return null;

            NetworkObject nob = manager.GetPooledInstantiated(spawner._objectPrefab, position, rotation, asServer: true);
            if (nob == null)
                return null;

            NetworkGrabbable grabbable = nob.GetComponent<NetworkGrabbable>();
            if (grabbable != null)
            {
                /* Both go on before the spawn, not after. SyncVar.OnChange does not fire for an
                 * initial value, so a value set afterwards arrives as a change — which means the
                 * object appears first as the bare template and then corrects itself, and a
                 * payload swap in between would re-measure the geometry and move the transform. */
                if (payloadIndex >= 0)
                    grabbable.ServerSetPayload(payloadIndex);

                if (variantNumber >= 0 || variantTeam >= 0)
                    grabbable.ServerSetVariant(variantNumber, variantTeam);
            }

            manager.ServerManager.Spawn(nob, owner);
            return nob;
        }

        /// <summary>
        /// Copies every spawned grabbable into a buffer.
        /// </summary>
        /// <remarks>
        /// Never walk <c>ServerManager.Objects.Spawned</c> directly while despawning. It is a
        /// live view over a plain Dictionary, and Despawn removes the entry synchronously, so
        /// the enumerator throws partway through and leaves the work half done. Nothing that
        /// only reads that collection has this problem — which is exactly what makes it easy
        /// to forget the moment something starts removing.
        ///
        /// The buffer is reused rather than allocated, so this is safe to call every frame.
        /// </remarks>
        public static void CollectSpawnedGrabbables(NetworkManager manager, List<NetworkGrabbable> buffer)
        {
            buffer.Clear();

            if (manager == null || !manager.IsServerStarted)
                return;

            foreach (NetworkObject spawned in manager.ServerManager.Objects.Spawned.Values)
            {
                if (spawned == null)
                    continue;

                NetworkGrabbable grabbable = spawned.GetComponent<NetworkGrabbable>();
                if (grabbable != null)
                    buffer.Add(grabbable);
            }
        }

        /// <summary>
        /// Server: destroys every grabbable in the scene.
        /// </summary>
        /// <remarks>
        /// The despawn type is passed explicitly instead of taking the prefab's default,
        /// because these objects carry per-life state — the placed cell, the placer, the
        /// settle timer — and pooling would carry all of it into the next life.
        /// </remarks>
        public static void DespawnAllGrabbables(NetworkManager manager, List<NetworkGrabbable> buffer)
        {
            CollectSpawnedGrabbables(manager, buffer);

            for (int i = 0; i < buffer.Count; i++)
            {
                NetworkGrabbable grabbable = buffer[i];
                if (grabbable == null || !grabbable.IsSpawned)
                    continue;

                grabbable.NetworkObject.Despawn(DespawnType.Destroy);
            }

            buffer.Clear();
        }
    }
}
