using FishNet;
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
        /// NetworkManager this spawner belongs to.
        /// </summary>
        private NetworkManager _networkManager;

        private void Awake()
        {
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

                NetworkObject nob = _networkManager.GetPooledInstantiated(
                    _objectPrefab,
                    position,
                    Quaternion.identity,
                    asServer: true);

                // No owner: the server simulates it until someone picks it up.
                _networkManager.ServerManager.Spawn(nob);
            }
        }
    }
}
