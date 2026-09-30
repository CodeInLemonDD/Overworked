using System.Collections.Generic;
using System.Text;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using Overworked.Stations;
using Overworked.Interaction;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overworked.Dev
{
    /// <summary>
    /// A typing console for building and tearing down scenes while the game runs.
    /// </summary>
    /// <remarks>
    /// Placing a station or changing a count otherwise means editing a prefab, and a prefab edit
    /// means leaving play mode, which means losing the state you were testing. The commands here
    /// do it in place, so the loop is typing rather than rebuilding.
    ///
    /// **Drawn by OnGUI, read by the Input System.** These are two separate jobs and they had to
    /// be split: the Input System cannot feed IMGUI at all — "The Input System cannot generate
    /// input for IMGUI", in its known limitations — and this project runs the Input System
    /// exclusively. So GUI.TextField would draw a box that swallows nothing, and every keystroke
    /// would go nowhere. Drawing still works, so the panel is IMGUI and every character comes
    /// from Keyboard.onTextInput instead.
    ///
    /// **Server only.** Every command that makes or removes something is a server operation, and
    /// this project runs a listen server, so the host is the server and normally has the console.
    /// A client typing into this is refused rather than obeyed. If it ever has to work on a
    /// client, the command has to travel as a ServerRpc — do not simply relax the check.
    ///
    /// **Gate it before shipping.** _commandsEnabled is what stands between a dev tool and a
    /// cheat. Nothing here is reachable from a remote client today, but that is a property of
    /// where it runs, not of what it allows.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DevConsole : MonoBehaviour
    {
        [Header("Gating")]

        /// <summary>
        /// Whether commands may run at all.
        /// </summary>
        [Tooltip("Whether commands may run at all. Turn this off for anything you hand to another person.")]
        [SerializeField]
        private bool _commandsEnabled = true;

        [Header("Input")]

        /// <summary>
        /// Key that opens and closes the panel.
        /// </summary>
        [Tooltip("Key that opens and closes the panel.")]
        [SerializeField]
        private Key _toggleKey = Key.Backquote;

        [Header("Spawning")]

        /// <summary>
        /// Station prefabs the furniture command can place, matched by name.
        /// </summary>
        /// <remarks>
        /// By name rather than by index because this is a typing tool: remembering that the
        /// printer is 2 is harder than remembering that it is called Printer. A registry keyed by
        /// id can come later if something needs to place stations from a script.
        ///
        /// Every prefab here must also be registered in the spawnable prefabs collection, or
        /// placing it fails. A scene object cannot be placed this way at all — it is spawned from
        /// its SceneId and never from the collection.
        /// </remarks>
        [Tooltip("Station prefabs the furniture command can place, matched by name.")]
        [SerializeField]
        private NetworkObject[] _furniture;

        [Header("Display")]

        /// <summary>
        /// How many lines of output to keep.
        /// </summary>
        [Tooltip("How many lines of output to keep.")]
        [Min(4)]
        [SerializeField]
        private int _historyLines = 64;

        /// <summary>
        /// Height of the open panel, as a fraction of the screen.
        /// </summary>
        [Tooltip("Height of the open panel, as a fraction of the screen.")]
        [Range(0.2f, 1f)]
        [SerializeField]
        private float _panelHeight = 0.45f;

        /// <summary>
        /// What has been typed so far.
        /// </summary>
        private string _input = string.Empty;

        /// <summary>
        /// Output lines, oldest first.
        /// </summary>
        private readonly List<string> _log = new();

        /// <summary>
        /// Whether the panel is open.
        /// </summary>
        private bool _open;

        /// <summary>
        /// Reused when removing objects. See GrabbableSpawner for why the spawned collection is
        /// never walked while despawning.
        /// </summary>
        private readonly List<NetworkGrabbable> _grabbableBuffer = new();

        /// <summary>
        /// Reused by the clear commands, which reach past grabbables to stations.
        /// </summary>
        private readonly List<NetworkObject> _objectBuffer = new();

        /// <summary>
        /// Set while the toggle key is handled, so the character it also produces is not typed.
        /// </summary>
        private bool _swallowNextCharacter;

        private void OnEnable()
        {
            if (Keyboard.current != null)
                Keyboard.current.onTextInput += OnTextInput;
        }

        private void OnDisable()
        {
            if (Keyboard.current != null)
                Keyboard.current.onTextInput -= OnTextInput;
        }

        private void Start()
        {
            if (!_commandsEnabled)
                return;

            if (!Application.isEditor)
            {
                Debug.LogWarning(
                    $"{nameof(DevConsole)} is enabled in a build. It can create and destroy anything " +
                    "the scene holds; turn _commandsEnabled off unless this is a build you are testing with.",
                    this);
            }

            Log("Type 'help' for commands.");
        }

        private void Update()
        {
            if (!_commandsEnabled || Keyboard.current == null)
                return;

            if (Keyboard.current[_toggleKey].wasPressedThisFrame)
            {
                _open = !_open;

                /* The toggle key is a character key, so pressing it also produces a backtick
                 * through onTextInput. Swallowing exactly one keeps the buffer clean without
                 * turning text input off for the frame, which would drop a fast typist's keys. */
                _swallowNextCharacter = true;
                _input = string.Empty;
            }

            if (!_open)
                return;

            if (Keyboard.current.backspaceKey.wasPressedThisFrame && _input.Length > 0)
                _input = _input.Substring(0, _input.Length - 1);

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
                _open = false;

            if (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)
            {
                Submit(_input);
                _input = string.Empty;
            }
        }

        /// <summary>
        /// Appends a typed character, unless it is the toggle key's own character.
        /// </summary>
        private void OnTextInput(char character)
        {
            if (!_open || !_commandsEnabled)
                return;

            if (_swallowNextCharacter)
            {
                _swallowNextCharacter = false;
                return;
            }

            if (character == '\n' || character == '\r' || character == '\t')
                return;

            _input += character;
        }

        private void OnGUI()
        {
            if (!_commandsEnabled || !_open)
                return;

            float height = Screen.height * _panelHeight;

            GUI.Box(new Rect(0f, 0f, Screen.width, height), GUIContent.none);
            GUILayout.BeginArea(new Rect(8f, 8f, Screen.width - 16f, height - 16f));

            for (int i = 0; i < _log.Count; i++)
                GUILayout.Label(_log[i]);

            GUILayout.FlexibleSpace();
            GUILayout.Label($"> {_input}_");
            GUILayout.Label("Enter to run, Esc to close.");

            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ commands

        /// <summary>
        /// Splits a line and runs it.
        /// </summary>
        private void Submit(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            Log($"> {line}");

            string[] parts = line.Trim().Split(' ');
            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "help":
                    Help();
                    return;

                case "spawn":
                    Spawn(parts);
                    return;

                case "clear":
                    Clear(parts);
                    return;

                case "give":
                    Give(parts);
                    return;

                case "tp":
                    Teleport(parts);
                    return;

                case "pos":
                    Position();
                    return;

                default:
                    Log($"unknown command '{verb}'. Try 'help'.");
                    return;
            }
        }

        private void Help()
        {
            Log("spawn entity <payload> <count> <x> <y>   place objects; payload is a catalogue index");
            Log("spawn furniture <name> <x> <y>           place a station by prefab name");
            Log("clear entities                           destroy every loose object");
            Log("clear all                                also destroy spawned stations");
            Log("give <payload>                           put one into the local player's hands");
            Log("tp <x> <y>                               move the local player to a cell");
            Log("pos                                      print the local player's cell");
            Log("help                                     this");
            Log("Coordinates are grid cells; objects land on the cell centre.");
        }

        /// <summary>
        /// Handles <c>spawn entity</c> and <c>spawn furniture</c>.
        /// </summary>
        private void Spawn(string[] parts)
        {
            if (parts.Length < 2)
            {
                Log("spawn what? 'entity' or 'furniture'.");
                return;
            }

            switch (parts[1].ToLowerInvariant())
            {
                case "entity":
                    SpawnEntities(parts);
                    return;

                case "furniture":
                    SpawnFurniture(parts);
                    return;

                default:
                    Log($"unknown spawn target '{parts[1]}'.");
                    return;
            }
        }

        /// <summary>
        /// <c>spawn entity &lt;payload&gt; &lt;count&gt; &lt;x&gt; &lt;y&gt;</c>
        /// </summary>
        /// <remarks>
        /// The payload index is the catalogue's, not a table of the console's own, so the console
        /// and the game cannot disagree about what a given number means.
        /// </remarks>
        private void SpawnEntities(string[] parts)
        {
            if (!RequireServer())
                return;
            if (parts.Length < 6)
            {
                Log("usage: spawn entity <payload> <count> <x> <y>");
                return;
            }
            if (!TryParse(parts[2], "payload", out int payload)
                || !TryParse(parts[3], "count", out int count)
                || !TryParse(parts[4], "x", out int x)
                || !TryParse(parts[5], "y", out int y))
                return;

            count = Mathf.Clamp(count, 1, 64);

            Vector3 centre = WorldGrid.CellCentre(new Vector2Int(x, y));

            for (int i = 0; i < count; i++)
            {
                /* Spread upward rather than stacking them on one point: physics sorts out where
                 * they land, but a pile of bodies spawned at the same place gets flung apart by
                 * the solver instead, which reads as the command having misfired. */
                Vector3 position = centre + Vector3.up * (0.5f + i * 0.3f);

                if (GrabbableSpawner.SpawnGrabbable(payload, position, Quaternion.identity) == null)
                {
                    Log("spawn failed; is the object prefab and a spawner assigned?");
                    return;
                }
            }

            Log($"spawned {count} x payload {payload} at cell ({x}, {y}).");
        }

        /// <summary>
        /// <c>spawn furniture &lt;name&gt; &lt;x&gt; &lt;y&gt;</c>
        /// </summary>
        private void SpawnFurniture(string[] parts)
        {
            if (!RequireServer())
                return;
            if (parts.Length < 5)
            {
                Log("usage: spawn furniture <name> <x> <y>");
                return;
            }
            if (!TryParse(parts[3], "x", out int x) || !TryParse(parts[4], "y", out int y))
                return;

            NetworkObject prefab = FindFurniture(parts[2]);
            if (prefab == null)
            {
                Log($"no furniture named '{parts[2]}'. Known: {FurnitureNames()}");
                return;
            }

            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null || !manager.IsServerStarted)
                return;

            Vector3 position = WorldGrid.CellCentre(new Vector2Int(x, y));

            /* A placed station is an ordinary spawnable prefab, not a scene object — it has no
             * SceneId, so it has to go through the prefab collection. That is why anything
             * listed here must also be registered in it. */
            NetworkObject nob = manager.GetPooledInstantiated(prefab, position, Quaternion.identity, asServer: true);
            if (nob == null)
            {
                Log($"could not instantiate '{parts[2]}'; is it registered in the spawnable prefabs?");
                return;
            }

            manager.ServerManager.Spawn(nob);
            Log($"placed '{parts[2]}' at cell ({x}, {y}).");
        }

        /// <summary>
        /// Handles <c>clear entities</c> and <c>clear all</c>.
        /// </summary>
        private void Clear(string[] parts)
        {
            if (!RequireServer())
                return;

            bool everything = parts.Length > 1 && parts[1].ToLowerInvariant() == "all";

            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null)
                return;

            GrabbableSpawner.CollectSpawnedGrabbables(manager, _grabbableBuffer);

            for (int i = 0; i < _grabbableBuffer.Count; i++)
            {
                NetworkGrabbable grabbable = _grabbableBuffer[i];
                if (grabbable == null || !grabbable.IsSpawned)
                    continue;

                grabbable.NetworkObject.Despawn(DespawnType.Destroy);
            }

            int destroyed = _grabbableBuffer.Count;
            _grabbableBuffer.Clear();

            if (everything)
                destroyed += ClearStations(manager);

            Log($"destroyed {destroyed} object(s).");
        }

        /// <summary>
        /// Destroys spawned stations, and reports how many went.
        /// </summary>
        /// <remarks>
        /// Players and scene objects are left alone. A scene object despawns to SetActive(false)
        /// with no way back, so this must not reach one — the station placed by hand in the editor
        /// is not the console's to remove.
        /// </remarks>
        private int ClearStations(NetworkManager manager)
        {
            _objectBuffer.Clear();

            foreach (NetworkObject spawned in manager.ServerManager.Objects.Spawned.Values)
            {
                if (spawned == null || spawned.IsSceneObject)
                    continue;
                if (spawned.GetComponent<StationBase>() == null)
                    continue;

                _objectBuffer.Add(spawned);
            }

            for (int i = 0; i < _objectBuffer.Count; i++)
                _objectBuffer[i].Despawn(DespawnType.Destroy);

            int count = _objectBuffer.Count;
            _objectBuffer.Clear();
            return count;
        }

        /// <summary>
        /// <c>give &lt;payload&gt;</c>
        /// </summary>
        /// <remarks>
        /// The same two steps a station uses: spawn it owned by the player, then tell the player
        /// to carry it. Skipping the second leaves it on the floor, and ServerHandToPlayer marks
        /// it held itself — a caller that tried to do that by hand would be doing it twice.
        /// </remarks>
        private void Give(string[] parts)
        {
            if (!RequireServer())
                return;
            if (parts.Length < 2)
            {
                Log("usage: give <payload>");
                return;
            }
            if (!TryParse(parts[1], "payload", out int payload))
                return;

            NetworkObject player = FindLocalPlayer();
            if (player == null)
            {
                Log("no local player to give anything to.");
                return;
            }

            PlayerInteraction interaction = player.GetComponent<PlayerInteraction>();
            if (interaction == null)
            {
                Log("the local player has no PlayerInteraction.");
                return;
            }

            NetworkObject nob = GrabbableSpawner.SpawnGrabbable(payload, interaction.HandPosition, Quaternion.identity, player.Owner);
            if (nob == null)
            {
                Log("spawn failed.");
                return;
            }

            interaction.ServerHandToPlayer(nob);
            Log($"gave a payload {payload}.");
        }

        /// <summary>
        /// <c>tp &lt;x&gt; &lt;y&gt;</c>
        /// </summary>
        /// <remarks>
        /// Runs on whichever peer owns the player, because that peer is the one simulating it.
        /// The controller is switched off around the write for the usual reason: an enabled
        /// CharacterController pulls the transform back to where it thinks the capsule is.
        /// </remarks>
        private void Teleport(string[] parts)
        {
            if (parts.Length < 3)
            {
                Log("usage: tp <x> <y>");
                return;
            }
            if (!TryParse(parts[1], "x", out int x) || !TryParse(parts[2], "y", out int y))
                return;

            NetworkObject player = FindLocalPlayer();
            if (player == null)
            {
                Log("no local player to move.");
                return;
            }

            Vector3 position = WorldGrid.CellCentre(new Vector2Int(x, y));

            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null)
                controller.enabled = false;

            player.transform.position = position;

            if (controller != null)
                controller.enabled = true;

            Log($"moved to cell ({x}, {y}).");
        }

        /// <summary>
        /// Prints the local player's cell.
        /// </summary>
        private void Position()
        {
            NetworkObject player = FindLocalPlayer();
            if (player == null)
            {
                Log("no local player.");
                return;
            }

            Vector3 p = player.transform.position;
            Vector2Int cell = WorldGrid.CellCoord(new Vector2(p.x, p.z));
            Log($"local player at cell ({cell.x}, {cell.y}); world ({p.x:0.##}, {p.z:0.##}).");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Refuses a command that needs the server, and says so.
        /// </summary>
        private bool RequireServer()
        {
            if (InstanceFinder.IsServerStarted)
                return true;

            Log("this command runs on the server only.");
            return false;
        }

        /// <summary>
        /// Finds a station prefab by name.
        /// </summary>
        private NetworkObject FindFurniture(string name)
        {
            if (_furniture == null)
                return null;

            foreach (NetworkObject prefab in _furniture)
            {
                if (prefab != null && string.Equals(prefab.name, name, System.StringComparison.OrdinalIgnoreCase))
                    return prefab;
            }

            return null;
        }

        /// <summary>
        /// Lists the furniture names, for an error message.
        /// </summary>
        private string FurnitureNames()
        {
            if (_furniture == null || _furniture.Length == 0)
                return "(none assigned)";

            StringBuilder builder = new();
            foreach (NetworkObject prefab in _furniture)
            {
                if (prefab == null)
                    continue;

                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(prefab.name);
            }

            return builder.Length > 0 ? builder.ToString() : "(none assigned)";
        }

        /// <summary>
        /// Finds the player this peer owns.
        /// </summary>
        /// <remarks>
        /// By ownership, matching how the camera and the debug HUD pick theirs: exactly one player
        /// belongs to this client, and on a host that is the host's own.
        /// </remarks>
        private static NetworkObject FindLocalPlayer()
        {
            NetworkObject[] candidates = FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude);

            foreach (NetworkObject candidate in candidates)
            {
                if (candidate != null && candidate.IsOwner && candidate.CompareTag("Player"))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Parses an argument, or reports which one was wrong.
        /// </summary>
        private bool TryParse(string text, string label, out int value)
        {
            if (int.TryParse(text, out value))
                return true;

            Log($"'{text}' is not a number ({label}).");
            return false;
        }

        /// <summary>
        /// Adds a line to the output, dropping the oldest when it is full.
        /// </summary>
        private void Log(string line)
        {
            _log.Add(line);

            while (_log.Count > _historyLines)
                _log.RemoveAt(0);
        }
    }
}
