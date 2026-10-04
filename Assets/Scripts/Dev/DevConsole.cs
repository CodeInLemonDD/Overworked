using System.Collections.Generic;
using System.Text;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using Overworked.Stations;
using Overworked.Interaction;
using Overworked.Player;
using Overworked.Documents;
using Overworked.Containers;
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

        [Header("Documents")]

        /// <summary>
        /// The document specs the console can create, and what Tab completes to.
        /// </summary>
        /// <remarks>
        /// Optional. Left empty, <see cref="Catalogue"/> finds the project's own asset in the
        /// editor, so the console works the moment it is dropped into a scene and nothing has to
        /// be wired by hand. The field is what points it at a different one, and it is the only
        /// way this works in a build — see the remarks on that property.
        /// </remarks>
        [Tooltip("Document specs. Leave empty to use the project's DocumentCatalogue (editor only).")]
        [SerializeField]
        private DocumentCatalogue _catalogue;

        /// <summary>
        /// The escalating request sequence the tier command writes from.
        /// </summary>
        /// <remarks>
        /// Optional, and found the same way <see cref="Catalogue"/> is. The console is the only
        /// thing in the project that can write a request before the customer spawner exists, and
        /// until that spawner is in the scene there is nothing to drag a reference from.
        /// </remarks>
        [Tooltip("Request tiers. Leave empty to use the project's RequestCatalogue (editor only).")]
        [SerializeField]
        private RequestCatalogue _requestCatalogue;

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
        /// Reused while assembling a request's rows.
        /// </summary>
        /// <remarks>
        /// A field rather than a local so a command that runs on a keypress allocates nothing,
        /// which is the rule every other buffer in the project follows.
        /// </remarks>
        private readonly List<DocumentRequest> _requestBuffer = new();

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
        private static readonly string[] Verbs =
            { "spawn", "clear", "give", "tp", "pos", "team", "document", "docs", "queue", "printers",
              "unlock", "unlocks", "tier", "requests", "score", "round", "help" };

#if UNITY_EDITOR
        /// <summary>
        /// What <see cref="Catalogue"/> found when the field was left empty. Cached because the
        /// lookup walks the asset database, and this is read once per command.
        /// </summary>
        private DocumentCatalogue _foundCatalogue;

        /// <summary>
        /// What <see cref="RequestTiers"/> found when the field was left empty.
        /// </summary>
        private RequestCatalogue _foundRequestCatalogue;
#endif

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

                case "document":
                    CreateDocument(parts);
                    return;

                case "docs":
                    ListDocuments();
                    return;

                case "queue":
                    QueueDocument(parts);
                    return;

                case "printers":
                    ListPrinters();
                    return;

                case "unlock":
                    Unlock(parts);
                    return;

                case "unlocks":
                    ListUnlocks();
                    return;

                case "team":
                    SetTeam(parts);
                    return;

                case "tier":
                    CreateRequest(parts);
                    return;

                case "requests":
                    ListRequests();
                    return;

                case "score":
                    Award(parts);
                    return;

                case "round":
                    ResetRound();
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
            Log("team <n>                                 put the local player on a team (test seam)");
            Log("document <spec> [team]                   name a document; prints its id");
            Log("docs                                     list the round's documents and their ids");
            Log("queue <printer> <document>               put a document in a machine's job queue");
            Log("printers                                 list the printers and their queues");
            Log("unlock <document>                        hand one over so it can be printed");
            Log("unlock reset                             take every document back");
            Log("unlocks                                  list every document and whether it is handed over");
            Log("tier [n]                                 write request n from the tier table (default: the next one)");
            Log("requests                                 list the live requests and what they name");
            Log("score <team> <points>                    move a team's score; negative takes points off");
            Log("round                                    zero the scores and restart the clock");
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

        /// <summary>
        /// <c>team &lt;n&gt;</c>
        /// </summary>
        /// <remarks>
        /// Moves the local player onto a team, and is the only way to do it: nothing in the game
        /// assigns teams yet. Without it every player is on team A, and the two things this round
        /// added cannot be exercised at all — a document's number counting per team, and the other
        /// team's being refused.
        ///
        /// It sets the team on this peer's own player, which on a listen server is the host's; a
        /// client is refused before it gets that far anyway. **A test seam, not match making** —
        /// the round that assigns teams properly will have more to decide than this one line.
        /// </remarks>
        private void SetTeam(string[] parts)
        {
            if (!RequireServer())
                return;
            if (parts.Length < 2)
            {
                Log("usage: team <n>");
                return;
            }
            if (!TryParse(parts[1], "team", out int team))
                return;

            NetworkObject player = FindLocalPlayer();
            if (player == null)
            {
                Log("no local player to put on a team.");
                return;
            }

            PlayerInteraction interaction = player.GetComponent<PlayerInteraction>();
            if (interaction == null)
            {
                Log("the local player has no PlayerInteraction.");
                return;
            }

            interaction.ServerSetTeam(team);

            /* Says which command to reach for next, because the team does not travel on its own:
             * the panel filters on it, and 'document' takes one as an argument rather than
             * reading it. */
            Log($"team {team}. The panel filters on this; pass it to 'document' to name one for this side.");
        }

        /// <summary>
        /// <c>document &lt;spec&gt; [team]</c>
        /// </summary>
        /// <remarks>
        /// Creates the record, not an object. A document that is lying around is a
        /// NetworkGrabbable some station handed out; what this stands in for is the computer, so
        /// that the machine can be tested before the computer exists. <c>queue</c> is what then
        /// puts it in a printer.
        ///
        /// The spec index is the document catalogue's, for the same reason the payload index in
        /// <c>spawn entity</c> is the payload catalogue's: the console and the game must not be
        /// able to disagree about what a number means, and a list of its own here would be one
        /// more place for it to drift.
        /// </remarks>
        private void CreateDocument(string[] parts)
        {
            if (!RequireServer())
                return;

            DocumentCatalogue catalogue = Catalogue;
            if (catalogue == null)
            {
                Log("no document catalogue assigned or found.");
                return;
            }

            if (parts.Length < 2)
            {
                Log("usage: document <spec> [team]");
                LogSpecs(catalogue);
                return;
            }
            if (!TryParse(parts[1], "spec", out int spec))
                return;

            /* Team A by default, which is where every player is until teams are assigned — the
             * same stopgap the computer panel takes. -1 is "no colour" and can be asked for. */
            int team = 0;
            if (parts.Length > 2 && !TryParse(parts[2], "team", out team))
                return;

            if (!catalogue.TryGet(spec, out DocumentCatalogue.Spec entry))
            {
                Log($"no document spec {spec}.");
                LogSpecs(catalogue);
                return;
            }

            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }

            int id = store.ServerCreate(spec, team);

            /* Read back rather than predicting what it became: the number is the store's to give,
             * and the printed id is the argument the next two commands want. */
            store.TryGet(id, out DocumentRecord record);

            Log($"named document {id}: kind {spec} '{entry.DisplayName}', number {record.Number}, " +
                $"team {record.Team}, source {(DocumentSource)entry.Source}. " +
                "'unlock' still has to hand it to somebody before it can be printed.");
        }

        /// <summary>
        /// <c>docs</c>
        /// </summary>
        /// <remarks>
        /// Reads the store rather than keeping a list of its own, which makes this the way a
        /// client checks that it has been told about everything the server has — so it is not a
        /// server command. It makes nothing and removes nothing.
        ///
        /// The kind column is the payload index, because that is what a document's kind is: its
        /// appearance. A record deliberately does not remember which spec it was ordered from,
        /// since several specs may share one payload.
        /// </remarks>
        private void ListDocuments()
        {
            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }
            if (store.Count == 0)
            {
                Log("no documents yet. 'document <spec>' names one.");
                return;
            }

            for (int id = 0; id < store.Count; id++)
            {
                if (!store.TryGet(id, out DocumentRecord record))
                    continue;
                if (!store.TryGetSpec(id, out DocumentCatalogue.Spec entry))
                {
                    Log($"#{id}  (kind {record.SpecIndex} is not in the catalogue)");
                    continue;
                }

                Log($"#{id}  {entry.DisplayName} {record.Number}  team {record.Team}  " +
                    $"{(DocumentSource)entry.Source}");
            }

            Log($"{store.Count} document(s).");
        }

        /// <summary>
        /// <c>tier [n]</c>
        /// </summary>
        /// <remarks>
        /// **The stand-in for the customer spawner, and deliberately the same shape.** It reads
        /// the tier the board is on, names every document that tier asks for — for every team, so
        /// both sides have their own copy of each name — and then writes the request in the
        /// numbers the store handed back. When the spawner arrives it should do exactly this;
        /// the console is where the sequence can be walked through before any customer exists.
        ///
        /// The numbers agreeing across teams is not an assumption, it is a consequence: every
        /// document in the round is created here or by the spawner, in lockstep for both teams,
        /// because players print documents rather than making them. It is checked anyway, because
        /// the failure — one team's Excel 2 being a different document from the other's — is
        /// invisible until a delivery mysteriously will not count.
        /// </remarks>
        private void CreateRequest(string[] parts)
        {
            if (!RequireServer())
                return;

            RequestCatalogue tiers = RequestTiers;
            if (tiers == null)
            {
                Log("no request catalogue assigned or found.");
                return;
            }

            RequestBoard board = RequestBoard.Instance;
            if (board == null)
            {
                Log("no RequestBoard in the scene.");
                return;
            }

            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }

            ScoreBoard scores = ScoreBoard.Instance;
            int teamCount = scores != null ? scores.TeamCount : 1;

            /* Defaulting to the board's own cursor rather than to zero is what makes the command
             * walk the sequence: each call asks for the next tier, and a round that reached the
             * end of the table keeps asking for the last one. */
            int tier = board.RequestsMade;
            if (parts.Length > 1 && !TryParse(parts[1], "tier", out tier))
                return;

            if (!tiers.TryGet(tier, out RequestCatalogue.RequestTier wanted))
            {
                Log("the request catalogue has no tiers authored.");
                return;
            }

            _requestBuffer.Clear();

            if (wanted.Wanted != null)
            {
                for (int i = 0; i < wanted.Wanted.Length; i++)
                {
                    RequestCatalogue.RequestEntry entry = wanted.Wanted[i];

                    for (int n = 0; n < entry.Count; n++)
                    {
                        int number = -1;

                        for (int team = 0; team < teamCount; team++)
                        {
                            int id = store.ServerCreate(entry.SpecIndex, team);
                            store.TryGet(id, out DocumentRecord record);

                            if (team == 0)
                                number = record.Number;
                            else if (record.Number != number)
                                Log($"warning: team {team} was given {SpecName(entry.SpecIndex)}{record.Number} where team 0 was given {number}. The teams have drifted apart.");
                        }

                        _requestBuffer.Add(new DocumentRequest { SpecIndex = entry.SpecIndex, Number = number });
                    }
                }
            }

            int requestId = board.ServerCreate(_requestBuffer);

            if (requestId < 0)
            {
                Log($"tier {tier} asks for nothing, so no request was written.");
                return;
            }

            Log($"request {requestId} (tier {tier}) wants {_requestBuffer.Count} document(s). Nothing has been printed — 'queue' and a printer still have to make them.");
        }

        /// <summary>
        /// <c>requests</c>
        /// </summary>
        /// <remarks>
        /// Reads the board rather than a list of its own, the same way <c>docs</c> reads the
        /// store, so it is the way a client checks it has been told about everything the server
        /// has. It makes nothing and removes nothing, so it is not a server command.
        /// </remarks>
        private void ListRequests()
        {
            RequestBoard board = RequestBoard.Instance;
            if (board == null)
            {
                Log("no RequestBoard in the scene.");
                return;
            }
            if (board.Count == 0)
            {
                Log("no requests yet. 'tier' writes one.");
                return;
            }

            int lastRequest = -1;

            for (int i = 0; i < board.Count; i++)
            {
                if (!board.TryGet(i, out DocumentRequest row))
                    continue;

                if (row.RequestId != lastRequest)
                {
                    lastRequest = row.RequestId;
                    Log($"request {row.RequestId}");
                }

                Log($"    {SpecName(row.SpecIndex)}{row.Number}");
            }

            Log($"{board.Count} row(s), {board.RequestCount} live, {board.RequestsMade} written this round.");
        }

        /// <summary>
        /// <c>score &lt;team&gt; &lt;points&gt;</c>
        /// </summary>
        /// <remarks>
        /// The stand-in for delivery and for the patience penalty, so a scoring test does not
        /// need a customer. Points may be negative, and the before/after pair is printed because
        /// <see cref="ScoreBoard.ServerAward"/> refuses to move anything once the round is over —
        /// a line that shows no change is how that shows up as a fact rather than as a guess.
        /// </remarks>
        private void Award(string[] parts)
        {
            if (!RequireServer())
                return;

            ScoreBoard board = ScoreBoard.Instance;
            if (board == null)
            {
                Log("no ScoreBoard in the scene.");
                return;
            }

            if (parts.Length < 3)
            {
                Log("usage: score <team> <points>");
                return;
            }

            if (!TryParse(parts[1], "team", out int team))
                return;
            if (!TryParse(parts[2], "points", out int points))
                return;

            if (team < 0 || team >= board.TeamCount)
            {
                Log($"no team {team}; this board scores {board.TeamCount} of them.");
                return;
            }

            int before = board.ScoreOf(team);
            board.ServerAward(team, points);

            Log($"team {team}: {before} -> {board.ScoreOf(team)}");
        }

        /// <summary>
        /// <c>round</c>
        /// </summary>
        /// <remarks>
        /// Puts the clock and the board back to the start of a round. **Documents and unlocks are
        /// deliberately left alone**: a document is a permanent record of something that was
        /// named, and the store's own remarks say it is never removed — so a restart numbers its
        /// first contract after the last round's, which is untidy and harmless. Unlocks have their
        /// own command.
        /// </remarks>
        private void ResetRound()
        {
            if (!RequireServer())
                return;

            ScoreBoard board = ScoreBoard.Instance;
            if (board == null)
            {
                Log("no ScoreBoard in the scene.");
                return;
            }

            RequestBoard requests = RequestBoard.Instance;
            requests?.ServerClear();

            board.ServerReset();

            Log($"round reset: {board.TeamCount} team(s) back to 0, {Mathf.RoundToInt(board.Remaining)}s on the clock, " +
                $"{requests?.Count ?? 0} request row(s) left. Documents and unlocks were not touched.");
        }

        /// <summary>
        /// Names a kind of document by its catalogue index.
        /// </summary>
        /// <remarks>
        /// The index is printed when there is no catalogue wired or it points at nothing, matching
        /// how the HUD falls back. It still tells a tester which entry is which.
        /// </remarks>
        private string SpecName(int specIndex)
        {
            DocumentStore store = DocumentStore.Instance;

            if (store == null || !store.TryGetSpecAt(specIndex, out DocumentCatalogue.Spec spec))
                return $"kind {specIndex} ";

            return $"{spec.DisplayName} ";
        }

        /// <summary>
        /// <c>queue &lt;printer&gt; &lt;document&gt;</c>
        /// </summary>
        /// <remarks>
        /// Moves a document that already exists, rather than making one: <c>document</c> is what
        /// creates records, and taking a spec index here as well would be a second way to make
        /// the same thing — the thing this round spent its frozen interfaces avoiding.
        ///
        /// It says out loud when the queue is full, unlike the computer, which refuses in silence
        /// like every other full container in the game. The difference is who is listening: the
        /// console has someone standing at it who just typed a line and is owed an answer.
        ///
        /// Server only, because it changes what a machine will do next. Not because a client
        /// could not be trusted with the read — <c>printers</c> is that read, and it is open to
        /// anyone.
        /// </remarks>
        private void QueueDocument(string[] parts)
        {
            if (!RequireServer())
                return;
            if (parts.Length < 3)
            {
                Log("usage: queue <printer> <document>");
                return;
            }
            if (!TryParse(parts[1], "printer", out int printer) || !TryParse(parts[2], "document", out int id))
                return;

            List<Printer> printers = FindPrinters();
            if (printer < 0 || printer >= printers.Count)
            {
                Log($"no printer {printer}. 'printers' lists them.");
                return;
            }

            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }
            if (!store.TryGet(id, out DocumentRecord record))
            {
                Log($"no document {id}. 'docs' lists them.");
                return;
            }

            Printer machine = printers[printer];
            ContainerBase queue = machine.Queue;
            if (queue == null)
            {
                Log($"'{machine.name}' has no job queue wired.");
                return;
            }

            if (!queue.ServerTryAdd(ContainerEntry.ForData(id)))
            {
                Log($"'{machine.name}' refused it; its queue is full at {Describe(queue)}.");
                return;
            }

            Log($"queued document {id} (#{record.Number}) on '{machine.name}'; queue {Describe(queue)}.");
        }

        /// <summary>
        /// <c>printers</c>
        /// </summary>
        /// <remarks>
        /// The numbering the <c>queue</c> command takes, and what each machine is holding. Like
        /// <c>docs</c> this reads and changes nothing, so it is not a server command: a client
        /// seeing a different queue length from the server is exactly the kind of thing worth
        /// being able to ask about from the client.
        /// </remarks>
        private void ListPrinters()
        {
            List<Printer> printers = FindPrinters();
            if (printers.Count == 0)
            {
                Log("no printers in the scene.");
                return;
            }

            for (int i = 0; i < printers.Count; i++)
            {
                Printer machine = printers[i];
                Vector3 position = machine.transform.position;
                Vector2Int cell = WorldGrid.CellCoord(new Vector2(position.x, position.z));

                ContainerBase queue = machine.Queue;
                string fill = queue != null ? Describe(queue) : "no queue wired";
                string printing = machine.PrintingDocument >= 0
                    ? $"  printing document {machine.PrintingDocument}"
                    : string.Empty;

                Log($"#{i}  {machine.name}  cell ({cell.x}, {cell.y})  queue {fill}{printing}");
            }
        }

        /// <summary>
        /// <c>unlock &lt;document&gt;</c> and <c>unlock reset</c>
        /// </summary>
        /// <remarks>
        /// The only way to hand a document over this round. That is meant to be something an NPC
        /// does, and there are no NPCs yet, so the call they will eventually make is reachable from
        /// here — which is what lets the panel and the data layer be finished and tested before
        /// anything exists to hand a document over.
        ///
        /// Takes a **document id**, not a kind. What a player earns is an Excel 2, not "Excel" —
        /// and two teams earn two different Excel 2s. <c>docs</c> lists the ids.
        ///
        /// Deliberately separate from <c>document</c>. Naming a document and handing it over are
        /// two acts in the game now, and a console that could only do both at once could not set up
        /// the state the difference is about: a task that names three documents and hands over one.
        ///
        /// Server only. The list is replicated state, and a client that could append to it would be
        /// handing documents to everybody.
        /// </remarks>
        private void Unlock(string[] parts)
        {
            if (!RequireServer())
                return;

            DocumentUnlocks unlocks = DocumentUnlocks.Instance;
            if (unlocks == null)
            {
                Log("no DocumentUnlocks in the scene, so there is nothing to hand over.");
                return;
            }

            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }

            if (parts.Length < 2)
            {
                Log("usage: unlock <document>, or 'unlock reset'. 'docs' lists the documents and their ids.");
                return;
            }

            if (parts[1].ToLowerInvariant() == "reset")
            {
                unlocks.ServerResetUnlocks();
                Log("reset; nothing is handed over.");
                return;
            }

            if (!TryParse(parts[1], "document", out int id))
                return;

            if (!store.TryGet(id, out DocumentRecord record))
            {
                Log($"no document {id}. 'docs' lists them.");
                return;
            }

            /* Named before handing over purely so the answer can say which document it was.
             * ServerUnlock reports "no such document" and "already handed over" the same way,
             * which is the right answer for a caller that only wants to know whether it can be
             * printed. */
            string name = DescribeDocument(store, id, record);

            if (unlocks.IsUnlocked(id))
            {
                Log($"{name} is already handed over.");
                return;
            }

            if (!unlocks.ServerUnlock(id))
            {
                /* Reached only when the store knows the document and the component says it is not
                 * handed over — so the component cannot see the store. Says so rather than
                 * reporting a refusal, because "refused" would send whoever reads it looking for a
                 * rule instead of a missing component. */
                Log($"{name} could not be handed over; check that the scene has a DocumentStore.");
                return;
            }

            Log($"handed over {name}; {Ordinal(unlocks, id)} of {unlocks.UnlockedCount} so far.");
        }

        /// <summary>
        /// A document as one readable phrase.
        /// </summary>
        /// <remarks>
        /// The kind and number when the catalogue can say what the kind is, and the raw kind index
        /// when it cannot. A record whose kind has been removed from the catalogue is still a real
        /// document that somebody may be holding, so it is reported rather than skipped — the
        /// alternative is a document that exists and cannot be seen.
        /// </remarks>
        private static string DescribeDocument(DocumentStore store, int id, in DocumentRecord record)
        {
            if (store.TryGetSpec(id, out DocumentCatalogue.Spec spec))
                return $"#{id} '{spec.DisplayName} {record.Number}' team {record.Team}";

            return $"#{id} (kind {record.SpecIndex} is not in the catalogue)";
        }

        /// <summary>
        /// <c>unlocks</c>
        /// </summary>
        /// <remarks>
        /// Every document the round has named and whether it has been handed over — the ones that
        /// have not included, which is the whole point. A list showing only what a player holds
        /// would look exactly like the same list from before this feature existed.
        ///
        /// Reads and changes nothing, so like <c>docs</c> and <c>printers</c> it is not a server
        /// command: a client being told a different set from the server is worth being able to ask
        /// about from the client.
        /// </remarks>
        private void ListUnlocks()
        {
            DocumentStore store = DocumentStore.Instance;
            if (store == null)
            {
                Log("no DocumentStore in the scene.");
                return;
            }

            DocumentUnlocks unlocks = DocumentUnlocks.Instance;

            /* Said before the list rather than instead of it. With no component every row below
             * reads as handed over, and that is otherwise indistinguishable from a round where
             * everything has been — which is a wiring mistake worth being able to see. */
            if (unlocks == null)
                Log("no DocumentUnlocks in the scene; nothing is locked.");

            if (store.Count == 0)
            {
                Log("no documents yet. 'document <spec>' names one.");
                return;
            }

            int handed = 0;

            for (int id = 0; id < store.Count; id++)
            {
                if (!store.TryGet(id, out DocumentRecord record))
                    continue;

                bool open = unlocks == null || unlocks.IsUnlocked(id);
                if (open)
                    handed++;

                Log($"{Mark(open)} {DescribeDocument(store, id, record)}");
            }

            Log($"{handed} of {store.Count} handed over.");
        }

        /// <summary>
        /// The tick and dot the document lists are drawn with.
        /// </summary>
        /// <remarks>
        /// A middle dot and a check mark rather than box drawing or an emoji: the dot is Latin-1
        /// and the tick is one of the oldest dingbats, so these are the two a status column has
        /// the best chance of getting from whatever font it ends up rendered in. A glyph the font
        /// does not have comes out as a box, and a status column that reads differently on every
        /// machine is worse than one with no marks at all.
        /// </remarks>
        private static string Mark(bool handed) => handed ? "✓" : "·";

        /// <summary>
        /// Where a document sits in the handed-over list, counting from one; zero when it is not
        /// there.
        /// </summary>
        private static int Ordinal(DocumentUnlocks unlocks, int documentId)
        {
            for (int i = 0; i < unlocks.UnlockedCount; i++)
            {
                if (unlocks.TryGetUnlocked(i, out int handed) && handed == documentId)
                    return i + 1;
            }

            return 0;
        }

        /// <summary>
        /// Lists the specs and what they resolve to, for a usage line or an unknown spec.
        /// </summary>
        private void LogSpecs(DocumentCatalogue catalogue)
        {
            for (int i = 0; i < catalogue.Count; i++)
            {
                if (!catalogue.TryGet(i, out DocumentCatalogue.Spec spec))
                    continue;

                Log($"  {i}  {spec.DisplayName}  payload {spec.PayloadIndex}  {(DocumentSource)spec.Source}");
            }
        }

        /// <summary>
        /// The document catalogue to make documents from.
        /// </summary>
        /// <remarks>
        /// The assignment when there is one, and otherwise the project's own asset. The fallback is
        /// what keeps the console a drop-in tool: this is the one thing in the project that no
        /// scene object holds a reference to yet — the computer prefab will, and until it exists
        /// there is nothing to drag from.
        ///
        /// Editor only. A build has no AssetDatabase, and this console is not meant to be shipped
        /// enabled anyway; a build that wants it fills the field, which works everywhere.
        /// </remarks>
        private DocumentCatalogue Catalogue
        {
            get
            {
                if (_catalogue != null)
                    return _catalogue;

#if UNITY_EDITOR
                if (_foundCatalogue == null)
                {
                    string[] found = UnityEditor.AssetDatabase.FindAssets($"t:{nameof(DocumentCatalogue)}");
                    if (found.Length > 0)
                    {
                        _foundCatalogue = UnityEditor.AssetDatabase.LoadAssetAtPath<DocumentCatalogue>(
                            UnityEditor.AssetDatabase.GUIDToAssetPath(found[0]));
                    }
                }

                return _foundCatalogue;
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// The request tiers <c>tier</c> writes from.
        /// </summary>
        /// <remarks>
        /// The same assignment-then-find arrangement as <see cref="Catalogue"/>, and for the same
        /// reason: until the customer spawner is in the scene, no object holds a reference to this
        /// asset, so the console would otherwise be unable to write a request at all.
        /// </remarks>
        private RequestCatalogue RequestTiers
        {
            get
            {
                if (_requestCatalogue != null)
                    return _requestCatalogue;

#if UNITY_EDITOR
                if (_foundRequestCatalogue == null)
                {
                    string[] found = UnityEditor.AssetDatabase.FindAssets($"t:{nameof(RequestCatalogue)}");
                    if (found.Length > 0)
                    {
                        _foundRequestCatalogue = UnityEditor.AssetDatabase.LoadAssetAtPath<RequestCatalogue>(
                            UnityEditor.AssetDatabase.GUIDToAssetPath(found[0]));
                    }
                }

                return _foundRequestCatalogue;
#else
                return null;
#endif
            }
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

            if (verb == "document" && position == 1)
            {
                DocumentCatalogue catalogue = Catalogue;
                if (catalogue != null)
                {
                    for (int i = 0; i < catalogue.Count; i++)
                        result.Add(i.ToString());
                }

                return result;
            }

            if (verb == "queue" && position == 1)
            {
                List<Printer> printers = FindPrinters();
                for (int i = 0; i < printers.Count; i++)
                    result.Add(i.ToString());

                return result;
            }

            if (verb == "queue" && position == 2)
            {
                DocumentStore store = DocumentStore.Instance;
                if (store != null)
                {
                    for (int id = 0; id < store.Count; id++)
                        result.Add(id.ToString());
                }

                return result;
            }

            if (verb == "unlock" && position == 1)
            {
                result.Add("reset");

                /* Every document, including the ones already handed over. Completing only what
                 * could still be handed over would make the list shrink as the round goes on, and
                 * the one thing this command has to be able to answer is whether a given document
                 * has been. */
                DocumentStore store = DocumentStore.Instance;
                if (store != null)
                {
                    for (int id = 0; id < store.Count; id++)
                        result.Add(id.ToString());
                }

                return result;
            }

            if (verb == "tier" && position == 1)
            {
                RequestCatalogue tiers = RequestTiers;
                if (tiers != null)
                {
                    for (int i = 0; i < tiers.Count; i++)
                        result.Add(i.ToString());
                }

                return result;
            }

            if (verb == "score" && position == 1)
            {
                ScoreBoard board = ScoreBoard.Instance;
                int teamCount = board != null ? board.TeamCount : 0;

                for (int i = 0; i < teamCount; i++)
                    result.Add(i.ToString());

                return result;
            }

            if (verb == "score" && position == 2)
            {
                /* The two numbers the round actually moves by, so the common cases are one Tab
                 * each. Anything else can still be typed. */
                result.Add("10");
                result.Add("-5");
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
        /// Every printer in the scene, in the order the console numbers them.
        /// </summary>
        /// <remarks>
        /// Sorted by name, because these numbers are printed by one command and typed into
        /// another. An order taken from the scene hierarchy would be a different order after the
        /// next load, and after that a different order again once someone adds a machine.
        /// </remarks>
        private static List<Printer> FindPrinters()
        {
            List<Printer> printers = new(FindObjectsByType<Printer>(FindObjectsInactive.Exclude));
            printers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return printers;
        }

        /// <summary>
        /// How full a container is, as count/capacity, or count/unlimited when it has no ceiling.
        /// </summary>
        /// <remarks>
        /// Unlimited containers report a capacity of zero or less, so printing the raw pair would
        /// read as "3/0" and look like a bug in the machine rather than a property of the
        /// container.
        /// </remarks>
        private static string Describe(ContainerBase container) =>
            container.IsUnlimited ? $"{container.Count}/unlimited" : $"{container.Count}/{container.Capacity}";

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
