using System.Collections.Generic;
using System.Text;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using Overworked.Stations;
using Overworked.Interaction;
using Overworked.Player;
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
        /// Lines submitted before, oldest first.
        /// </summary>
        private readonly List<string> _history = new();

        /// <summary>
        /// Where the arrow keys are in <see cref="_history"/>, or -1 for the line being typed.
        /// </summary>
        private int _historyIndex = -1;

        /// <summary>
        /// The half-typed line, kept while the history is being browsed so it can be given back.
        /// </summary>
        private string _stashedInput = string.Empty;

        /// <summary>
        /// Every first word the console knows.
        /// </summary>
        private static readonly string[] Verbs = { "spawn", "clear", "give", "tp", "pos", "help" };

        /// <summary>
        /// Frame the console was toggled on, so the backtick that opened it is not typed.
        /// </summary>
        /// <remarks>
        /// A frame rather than a flag. A flag would survive until some later character arrived
        /// and eat it instead, so the first letter typed after opening would vanish.
        /// </remarks>
        private int _swallowFrame = -1;

        private void OnEnable()
        {
            if (Keyboard.current != null)
                Keyboard.current.onTextInput += OnTextInput;
        }

        private void OnDisable()
        {
            if (Keyboard.current != null)
                Keyboard.current.onTextInput -= OnTextInput;

            /* The console may be destroyed or switched off while it is open. Leaving the player
             * unable to move, with nothing on screen to say why, is the worst way to find out. */
            SetGameplayInputEnabled(true);
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
                _input = string.Empty;
                _swallowFrame = Time.frameCount;

                /* Typing a command uses the same keys the game reads, so the player would walk
                 * around and press E while the console is up. Every input-owning component holds
                 * its own copy of the action asset, so each has to be told. */
                SetGameplayInputEnabled(!_open);
            }

            if (!_open)
                return;

            if (Keyboard.current.backspaceKey.wasPressedThisFrame && _input.Length > 0)
                _input = _input.Substring(0, _input.Length - 1);

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
                _open = false;

            if (Keyboard.current.upArrowKey.wasPressedThisFrame)
                Browse(-1);

            if (Keyboard.current.downArrowKey.wasPressedThisFrame)
                Browse(1);

            if (Keyboard.current.tabKey.wasPressedThisFrame)
                Complete();

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

            /* The toggle key is a character key, so opening the console also delivers its own
             * backtick. Only that character, and only on the frame it happened. */
            if (Time.frameCount == _swallowFrame && (character == '`' || character == '~'))
                return;

            /* Control characters are the keys that are not text — backspace, escape, the arrows.
             * They are handled as keys, and letting them into the buffer would leave invisible
             * junk in the line that backspace then has to chew through one character at a time. */
            if (char.IsControl(character))
                return;

            _input += character;
        }

        private void OnGUI()
        {
            if (!_commandsEnabled || !_open)
                return;

            float width = Screen.width;
            float height = Screen.height * _panelHeight;

            /* Laid out with explicit rectangles rather than GUILayout. With a layout group the
             * output grows downward and pushes the input line past the bottom of the clipped
             * area — where it is still live and still receives typing, but invisible, which reads
             * as the console having lost its prompt. Pinning the two lines to the bottom and
             * drawing only as much history as fits above them cannot do that. */
            /* Measured rather than taken from lineHeight. That value is the style's own idea of a
             * line, and a GUI.Label clips to the rectangle it is given, so a rectangle built from
             * it cuts the bottom off every line. CalcSize asks the style how tall this text
             * actually is, descenders included. */
            float line = GUI.skin.label.CalcSize(new GUIContent("Ag")).y;
            if (line <= 0f)
                line = 16f;

            const float pad = 8f;
            float inputHeight = line * 2f;

            GUI.Box(new Rect(0f, 0f, width, height), GUIContent.none);

            /* The bottom margin is doubled so the last line has room below its baseline instead
             * of sitting on the panel's edge. */
            Rect inputRect = new(pad, height - pad * 2f - inputHeight, width - pad * 2f, inputHeight);
            Rect logRect = new(pad, pad, width - pad * 2f, inputRect.y - pad * 2f);

            int fits = Mathf.Max(1, Mathf.FloorToInt(logRect.height / line));
            int first = Mathf.Max(0, _log.Count - fits);

            for (int i = first; i < _log.Count; i++)
            {
                float y = logRect.y + (i - first) * line;
                GUI.Label(new Rect(logRect.x, y, logRect.width, line), _log[i]);
            }

            GUI.Label(new Rect(inputRect.x, inputRect.y, inputRect.width, line), $"> {_input}_");
            GUI.Label(new Rect(inputRect.x, inputRect.y + line, inputRect.width, line), "Enter run, Esc close, Tab complete, Up/Down history.");
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

            string trimmed = line.Trim();

            /* Kept only when it differs from the last one, so holding Enter on a repeated command
             * does not fill the history with copies of it. */
            if (_history.Count == 0 || _history[_history.Count - 1] != trimmed)
                _history.Add(trimmed);

            _historyIndex = -1;
            _stashedInput = string.Empty;

            string[] parts = trimmed.Split(' ');
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
            Log("spawn entity <payload> <count> <x> <y> [number] [team]");
            Log("                                         numbers run consecutively from <number>");
            Log("spawn furniture <name> <x> <y>           place a station by prefab name");
            Log("clear entities                           destroy every loose object");
            Log("clear all                                also destroy spawned stations");
            Log("give <payload> [number] [team]           put one into the local player's hands");
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
                Log("usage: spawn entity <payload> <count> <x> <y> [number] [team]");
                return;
            }
            if (!TryParse(parts[2], "payload", out int payload)
                || !TryParse(parts[3], "count", out int count)
                || !TryParse(parts[4], "x", out int x)
                || !TryParse(parts[5], "y", out int y))
                return;

            /* Optional, and -1 means "as the template was authored" — the same meaning it has
             * everywhere else, so leaving them off gives a plain unnumbered sheet. */
            int number = -1;
            int team = -1;

            if (parts.Length > 6 && !TryParse(parts[6], "number", out number))
                return;
            if (parts.Length > 7 && !TryParse(parts[7], "team", out team))
                return;

            count = Mathf.Clamp(count, 1, 64);

            Vector3 centre = WorldGrid.CellCentre(new Vector2Int(x, y));

            for (int i = 0; i < count; i++)
            {
                /* Spread upward rather than stacking them on one point: physics sorts out where
                 * they land, but a pile of bodies spawned at the same place gets flung apart by
                 * the solver instead, which reads as the command having misfired. */
                Vector3 position = centre + Vector3.up * (0.5f + i * 0.3f);

                /* Consecutive numbers rather than the same one on all of them: a round hands out
                 * Excel 1, Excel 2 and so on, and seeing several different numbers on the pile is
                 * the point of testing this at all. */
                int thisNumber = number >= 0 ? number + i : -1;

                if (GrabbableSpawner.SpawnGrabbable(payload, position, Quaternion.identity, null, thisNumber, team) == null)
                {
                    Log("spawn failed; is the object prefab and a spawner assigned?");
                    return;
                }
            }

            string numbered = number >= 0 ? $" numbered {number}..{number + count - 1}" : string.Empty;
            Log($"spawned {count} x payload {payload}{numbered} at cell ({x}, {y}).");
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
                Log("usage: give <payload> [number] [team]");
                return;
            }
            if (!TryParse(parts[1], "payload", out int payload))
                return;

            int number = -1;
            int team = -1;

            if (parts.Length > 2 && !TryParse(parts[2], "number", out number))
                return;
            if (parts.Length > 3 && !TryParse(parts[3], "team", out team))
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

            NetworkObject nob = GrabbableSpawner.SpawnGrabbable(
                payload, interaction.HandPosition, Quaternion.identity, player.Owner, number, team);
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
        /// Steps through the submitted lines, and back to the half-typed one.
        /// </summary>
        /// <param name="direction">-1 for older, 1 for newer.</param>
        /// <remarks>
        /// The line being typed is put aside before the first step into the history, so walking
        /// back down past the newest entry gives it back rather than leaving an empty prompt.
        /// </remarks>
        private void Browse(int direction)
        {
            if (_history.Count == 0)
                return;

            if (_historyIndex < 0)
            {
                if (direction > 0)
                    return;

                _stashedInput = _input;
                _historyIndex = _history.Count;
            }

            int next = Mathf.Clamp(_historyIndex + direction, 0, _history.Count);

            if (next == _history.Count)
            {
                _historyIndex = -1;
                _input = _stashedInput;
                return;
            }

            _historyIndex = next;
            _input = _history[next];
        }

        /// <summary>
        /// Completes the word being typed from whatever could come next.
        /// </summary>
        /// <remarks>
        /// Fills in as far as the candidates agree, and lists them when they agree on nothing
        /// further. That is what a shell does, and it is the behaviour that stays useful when the
        /// thing being completed is a prefab name nobody can remember the spelling of.
        /// </remarks>
        private void Complete()
        {
            string[] tokens = _input.Split(' ');

            /* Whether the last word is partly typed or not started, its position in the line is
             * the same — a trailing space simply leaves an empty last token. */
            int position = tokens.Length - 1;
            bool newWord = _input.Length == 0 || _input.EndsWith(" ");
            string partial = newWord ? string.Empty : tokens[position];

            List<string> matches = new();
            foreach (string candidate in CandidatesFor(tokens, position))
            {
                if (candidate.StartsWith(partial, System.StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
            }

            if (matches.Count == 0)
                return;

            if (matches.Count == 1)
            {
                ReplaceLastToken(tokens, position, newWord, matches[0]);
                return;
            }

            /* Several matches: extend to what they share, and only list them when that shares
             * nothing more than what has already been typed — otherwise a second Tab on an
             * unchanged line would print the same list again. */
            string common = CommonPrefix(matches);
            if (common.Length > partial.Length)
                ReplaceLastToken(tokens, position, newWord, common);
            else
                Log("  " + string.Join("  ", matches));
        }

        /// <summary>
        /// Puts a completed word back into the line.
        /// </summary>
        private void ReplaceLastToken(string[] tokens, int position, bool newWord, string word)
        {
            if (newWord)
            {
                _input = _input + word + " ";
                return;
            }

            tokens[position] = word;
            _input = string.Join(" ", tokens);
        }

        /// <summary>
        /// What could come next at a position in the line.
        /// </summary>
        private List<string> CandidatesFor(string[] tokens, int position)
        {
            List<string> result = new();

            if (position == 0)
            {
                result.AddRange(Verbs);
                return result;
            }

            string verb = tokens[0].ToLowerInvariant();

            if (verb == "spawn" && position == 1)
            {
                result.Add("entity");
                result.Add("furniture");
                return result;
            }

            if (verb == "clear" && position == 1)
            {
                result.Add("entities");
                result.Add("all");
                return result;
            }

            bool placingFurniture = verb == "spawn" && position == 2
                && tokens.Length > 1 && tokens[1].ToLowerInvariant() == "furniture";

            if (placingFurniture && _furniture != null)
            {
                foreach (NetworkObject prefab in _furniture)
                {
                    if (prefab != null)
                        result.Add(prefab.name);
                }
            }

            return result;
        }

        /// <summary>
        /// The longest start every candidate shares.
        /// </summary>
        private static string CommonPrefix(List<string> values)
        {
            if (values.Count == 0)
                return string.Empty;

            string prefix = values[0];

            for (int i = 1; i < values.Count; i++)
            {
                int j = 0;
                while (j < prefix.Length && j < values[i].Length
                       && char.ToLowerInvariant(prefix[j]) == char.ToLowerInvariant(values[i][j]))
                {
                    j++;
                }

                prefix = prefix.Substring(0, j);
                if (prefix.Length == 0)
                    break;
            }

            return prefix;
        }

        /// <summary>
        /// Turns the local player's gameplay input on or off.
        /// </summary>
        /// <remarks>
        /// With the console open, typing a command also walks the character and presses E: the
        /// keystrokes are the same ones the game reads, and the console is only a second reader
        /// of them. There is no single switch to throw, because each of these components keeps
        /// its own copy of the action asset — a shared one would let one player's Disable turn
        /// another player's input off. So each is told separately.
        /// </remarks>
        private void SetGameplayInputEnabled(bool enabled)
        {
            NetworkObject player = FindLocalPlayer();
            if (player == null)
                return;

            PlayerInteraction interaction = player.GetComponent<PlayerInteraction>();
            if (interaction != null)
                interaction.SetInputEnabled(enabled);

            PlayerMovementPrediction movement = player.GetComponent<PlayerMovementPrediction>();
            if (movement != null)
                movement.SetInputEnabled(enabled);

            PlayerStamina stamina = player.GetComponent<PlayerStamina>();
            if (stamina != null)
                stamina.SetInputEnabled(enabled);
        }

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
