using System;
using System.Net;
using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overworked.UI
{
    /// <summary>
    /// Types the host's address and starts a connection — the way into a session that is not
    /// running on this machine.
    /// </summary>
    /// <remarks>
    /// **The client address has never been set, and nothing in the Inspector can set it.** The
    /// transport is not on the NetworkManager prefab: <c>TransportManager.TryAddDefaultTransport</c>
    /// adds a Tugboat at runtime when it finds none, so the component holding <c>_clientAddress</c>
    /// does not exist until the game is already running, and the field keeps its declared default
    /// of <c>localhost</c>. The server half is fine — an empty bind address means <c>0.0.0.0</c>,
    /// so it has been listening on every adapter all along. The failure is one-sided, which is why
    /// two instances on one machine never showed it: there, localhost is the right answer.
    ///
    /// **It is a bridge, and it should be deleted rather than extended.** The Steam transport has
    /// no addresses — a lobby is joined by identity, not by an IP — so the moment that lands, every
    /// idea in this file is obsolete. Keep it small for that reason.
    ///
    /// **Drawn by <c>OnGUI</c>, read by the Input System.** The same split
    /// <see cref="Overworked.Dev.DevConsole"/> makes, and for the same reason: this project runs the
    /// Input System exclusively and it cannot generate input for IMGUI, so a <c>GUI.TextField</c>
    /// here would draw a box that swallows nothing. Characters arrive through
    /// <c>Keyboard.onTextInput</c> instead.
    ///
    /// **Only digits, dots and backspace are accepted, and that is doing more work than it looks.**
    /// It keeps this panel out of the problem the console has to solve with
    /// <c>SetGameplayInputEnabled</c>: a character typed here can never reach the movement or
    /// interact bindings, because those are letters and no letter can get into the box. So nothing
    /// has to be switched off, no other component has to be told, and two text-accepting panels can
    /// coexist without an arbitration rule that neither of them owns. An address needs nothing else.
    ///
    /// **The bottom-left corner, because it is the only square metre left.** The console draws a
    /// full-width band across the top 45%; the debug readout is top-right; the computer panel is
    /// right-centre; the banner is top-centre; the fade covers everything. IMGUI draws over all of
    /// them — including the fade, which is why this stays clickable in the middle of a transition.
    ///
    /// The FishNet demo's own <c>OnGUI</c> sets <c>GUI.matrix</c> and never restores it, so this
    /// sets its own before drawing rather than assuming which of the two runs first. The demo's two
    /// buttons still work and are worth leaving alone; "Host" below is what they cannot do, since
    /// the demo has no single button that starts both halves.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ConnectionPanel : MonoBehaviour
    {
        #region Types.
        /// <summary>
        /// A connection the command line asked for, held until the transport exists to serve it.
        /// </summary>
        private enum AutoConnect
        {
            None,
            Host,
            Client
        }
        #endregion

        #region Layout constants.
        /// <summary>
        /// Height the layout is authored against, matching how the FishNet demo sizes its buttons.
        /// </summary>
        private const float ReferenceHeight = 1080f;

        private const float Margin = 12f;
        private const float Padding = 8f;
        private const float RowHeight = 24f;
        private const float ButtonHeight = 26f;
        private const float PanelWidth = 320f;
        private const float ButtonGap = 6f;
        private const float PanelHeight = Padding + RowHeight + 2f + RowHeight + 6f + ButtonHeight + Padding;
        #endregion

        #region Configuration.
        /// <summary>
        /// Address the box starts with, before the command line gets a say.
        /// </summary>
        /// <remarks>
        /// Loopback, because the common case while wiring something up is two copies on one machine
        /// and because it is the address a host would use to join itself.
        /// </remarks>
        [Tooltip("Address the box starts with. 127.0.0.1 is right when the client is on the host's machine.")]
        [SerializeField]
        private string _address = "127.0.0.1";

        /// <summary>
        /// Key that hides and shows the panel.
        /// </summary>
        [Tooltip("Key that hides and shows the panel.")]
        [SerializeField]
        private Key _toggleKey = Key.F1;
        #endregion

        #region Runtime.
        /// <summary>
        /// How long the command line's request waits for a transport before giving up out loud.
        /// </summary>
        private const float AutoConnectGraceSeconds = 10f;

        /// <summary>
        /// The only address that is correct on both sides of a connection.
        /// </summary>
        private const string LoopbackAddress = "127.0.0.1";

        /// <summary>
        /// Longest an IPv4 address can be, so the box cannot be filled with arbitrary length.
        /// </summary>
        private const int MaxAddressLength = 15;

        private NetworkManager _networkManager;
        private bool _subscribed;
        private bool _focused;
        private bool _shown = true;
        private LocalConnectionState _clientState = LocalConnectionState.Stopped;
        private LocalConnectionState _serverState = LocalConnectionState.Stopped;
        private AutoConnect _autoConnect = AutoConnect.None;
        private float _autoConnectDeadline;
        #endregion

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
            ReadCommandLine();
            _autoConnectDeadline = Time.unscaledTime + AutoConnectGraceSeconds;
        }

        private void OnDestroy()
        {
            if (!_subscribed || _networkManager == null)
                return;

            _networkManager.ClientManager.OnClientConnectionState -= ClientManager_OnClientConnectionState;
            _networkManager.ServerManager.OnServerConnectionState -= ServerManager_OnServerConnectionState;
        }

        private void Update()
        {
            ResolveNetworkManager();

            if (Keyboard.current != null && Keyboard.current[_toggleKey].wasPressedThisFrame)
            {
                _shown = !_shown;
                _focused = false;
            }

            if (_focused && Keyboard.current != null)
            {
                if (Keyboard.current.backspaceKey.wasPressedThisFrame && _address.Length > 0)
                    _address = _address.Substring(0, _address.Length - 1);

                if (Keyboard.current.escapeKey.wasPressedThisFrame)
                    _focused = false;
            }

            RunPendingAutoConnect();
        }

        /// <summary>
        /// Appends a typed character, if it is one an address can contain.
        /// </summary>
        /// <remarks>
        /// Gated on the box being focused, so this and the console can both hold a subscription to
        /// <c>onTextInput</c> without either having to know about the other.
        /// </remarks>
        private void OnTextInput(char character)
        {
            if (!_focused)
                return;

            /* Control characters are the keys that are not text — backspace, escape, the arrows.
             * Backspace is handled as a key in Update, and letting it in here would leave an
             * invisible character that backspace then has to chew through one press at a time. */
            if (char.IsControl(character))
                return;

            if (!char.IsDigit(character) && character != '.')
                return;

            if (_address.Length >= MaxAddressLength)
                return;

            _address += character;
        }

        /// <summary>
        /// Finds the NetworkManager, and subscribes to connection state once it can.
        /// </summary>
        /// <remarks>
        /// Subscribed here rather than in <c>Awake</c>: FishNet builds <c>ClientManager</c> and
        /// <c>ServerManager</c> lazily, so they can be absent while components are still enabling.
        /// </remarks>
        private void ResolveNetworkManager()
        {
            if (_networkManager == null)
                _networkManager = InstanceFinder.NetworkManager;

            if (_subscribed || _networkManager == null)
                return;

            if (_networkManager.ClientManager == null || _networkManager.ServerManager == null)
                return;

            _networkManager.ClientManager.OnClientConnectionState += ClientManager_OnClientConnectionState;
            _networkManager.ServerManager.OnServerConnectionState += ServerManager_OnServerConnectionState;
            _subscribed = true;
        }

        /// <summary>
        /// Reads <c>-address</c>, <c>-connect</c> and <c>-host</c> off the command line.
        /// </summary>
        /// <remarks>
        /// So that the far end of a VPN link does not have to be told an address at all: a shortcut
        /// with <c>-address</c> in it, or a <c>.bat</c> beside the build, is the entire setup.
        /// </remarks>
        private void ReadCommandLine()
        {
            string address = ArgumentValue("-address");
            if (!string.IsNullOrWhiteSpace(address))
                _address = address.Trim();

            if (HasArgument("-host"))
                _autoConnect = AutoConnect.Host;
            else if (HasArgument("-connect"))
                _autoConnect = AutoConnect.Client;
        }

        private void RunPendingAutoConnect()
        {
            if (_autoConnect == AutoConnect.None)
                return;

            /* Waiting for the transport rather than for the NetworkManager: ClientManager hands the
             * address straight to it, so a transport that is not there yet is the real precondition. */
            if (TryGetTransport(out _))
            {
                AutoConnect wanted = _autoConnect;
                _autoConnect = AutoConnect.None;

                if (wanted == AutoConnect.Host)
                    StartHost();
                else
                    Connect();

                return;
            }

            /* A command-line switch that quietly does nothing is the worst version of this panel:
             * the build is on somebody else's machine and there is nothing on screen to look at. */
            if (Time.unscaledTime > _autoConnectDeadline)
            {
                AutoConnect wanted = _autoConnect;
                _autoConnect = AutoConnect.None;
                Report($"was asked to {wanted} from the command line, but no transport turned up " +
                       $"within {AutoConnectGraceSeconds:0} seconds. Start it by hand from the panel.");
            }
        }

        private void StartHost()
        {
            if (_networkManager == null)
            {
                Report("cannot host — no NetworkManager in the scene.");
                return;
            }

            /* The host's own client dials loopback. Sending it out over the adapter it is also
             * listening on would make the host's own seat depend on that adapter being healthy,
             * which is the one thing a host cannot afford to lose. */
            if (!_networkManager.ServerManager.StartConnection())
                Report("the server would not start — it may already be running.");

            if (!_networkManager.ClientManager.StartConnection(LoopbackAddress))
                Report("the server started, but this machine's own client would not join it.");
            else
                Debug.Log($"{nameof(ConnectionPanel)}: hosting{PortSuffix()}. " +
                          "Clients connect to this machine's LAN address.", this);
        }

        private void Connect()
        {
            if (_networkManager == null)
            {
                Report("cannot connect — no NetworkManager in the scene.");
                return;
            }

            string address = _address.Trim();

            if (address.Length == 0)
            {
                Report("no address typed. Click the box and type the host's address.");
                return;
            }

            /* Parsed rather than trusted. LiteNetLib would take a hostname to DNS and a typo would
             * come back as five seconds of nothing followed by "ConnectionFailed" — which never
             * names the address that was tried. The address is the one thing worth printing. */
            if (!IPAddress.TryParse(address, out _))
            {
                Report($"'{address}' is not an address. Type the host's address, digits and dots only.");
                return;
            }

            if (!_networkManager.ClientManager.StartConnection(address))
            {
                Report($"the client would not start toward {address} — it may already be running.");
                return;
            }

            /* The address only. Naming the port here would name the client's own, which is not the
             * one being connected to — see PortSuffix. */
            Debug.Log($"{nameof(ConnectionPanel)}: connecting to {address}.", this);
        }

        private void Disconnect()
        {
            if (_networkManager == null)
                return;

            /* Both halves, because the button appears when either is up and a host that stops only
             * its client looks fine to itself while every guest stays connected to a server nobody
             * is playing on. */
            _networkManager.ClientManager.StopConnection();

            if (_serverState != LocalConnectionState.Stopped)
                _networkManager.ServerManager.StopConnection(true);

            Debug.Log($"{nameof(ConnectionPanel)}: disconnected.", this);
        }

        private void ClientManager_OnClientConnectionState(ClientConnectionStateArgs args)
        {
            _clientState = args.ConnectionState;
        }

        private void ServerManager_OnServerConnectionState(ServerConnectionStateArgs args)
        {
            _serverState = args.ConnectionState;
        }

        private void OnGUI()
        {
            if (!_shown)
                return;

            /* Set rather than inherited. The demo HUD scales this matrix and never puts it back, and
             * it is also what makes mousePosition below land in the same space as the rects. */
            float scale = Mathf.Max(1f, Screen.height / ReferenceHeight);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            float viewWidth = Screen.width / scale;
            float viewHeight = Screen.height / scale;

            Rect panel = new(Margin, viewHeight - Margin - PanelHeight, Mathf.Min(PanelWidth, viewWidth - Margin * 2f), PanelHeight);
            GUI.Box(panel, GUIContent.none);

            float x = panel.x + Padding;
            float width = panel.width - Padding * 2f;
            float y = panel.y + Padding;

            DrawAddressField(new Rect(x, y, width, RowHeight));
            y += RowHeight + 2f;

            GUI.Label(new Rect(x, y, width, RowHeight), StatusText());
            y += RowHeight + 6f;

            DrawButtons(new Rect(x, y, width, ButtonHeight));
        }

        private void DrawAddressField(Rect rect)
        {
            GUI.Box(rect, GUIContent.none);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                _focused = true;
                Event.current.Use();
            }

            string shown = _address.Length > 0 ? _address : "click here and type the host's address";
            GUI.Label(new Rect(rect.x + 5f, rect.y, rect.width - 10f, rect.height), _focused ? shown + "_" : shown);
        }

        private void DrawButtons(Rect row)
        {
            float width = (row.width - ButtonGap * 2f) / 3f;
            bool hosting = _serverState != LocalConnectionState.Stopped;
            bool clientBusy = _clientState != LocalConnectionState.Stopped;

            bool wasEnabled = GUI.enabled;

            GUI.enabled = !hosting && !clientBusy;
            if (GUI.Button(new Rect(row.x, row.y, width, row.height), "Host"))
                StartHost();

            GUI.enabled = !clientBusy;
            if (GUI.Button(new Rect(row.x + width + ButtonGap, row.y, width, row.height), "Connect"))
                Connect();

            GUI.enabled = hosting || clientBusy;
            if (GUI.Button(new Rect(row.x + (width + ButtonGap) * 2f, row.y, width, row.height), "Disconnect"))
                Disconnect();

            GUI.enabled = wasEnabled;
        }

        private string StatusText()
        {
            string status = _clientState switch
            {
                LocalConnectionState.Starting => $"connecting to {_address}...",
                LocalConnectionState.Started => "connected",
                LocalConnectionState.Stopping => "disconnecting...",
                _ => "not connected",
            };

            if (_serverState == LocalConnectionState.Started)
                status += "  ·  hosting";

            return status;
        }

        /// <summary>
        /// The transport, once FishNet has made one.
        /// </summary>
        /// <remarks>
        /// Read to know the transport is ready and for the port in a log line, not to set anything:
        /// the address goes in through <c>ClientManager.StartConnection(address)</c>, which is the
        /// documented way and one call instead of two.
        /// </remarks>
        private bool TryGetTransport(out Transport transport)
        {
            transport = null;

            if (_networkManager == null || _networkManager.TransportManager == null)
                return false;

            transport = _networkManager.TransportManager.Transport;
            return transport != null;
        }

        /// <summary>
        /// The port to name in a log line, when there is a meaningful one to name.
        /// </summary>
        /// <remarks>
        /// **Only meaningful for a server, and actively wrong for a client.** Tugboat answers
        /// <c>GetPort</c> with the port the local socket bound to. A server binds the port it was
        /// configured with, so that answer is the one to open a firewall for. A client does not
        /// choose its port — the operating system hands it a free one — so asking after a client
        /// has started returns an ephemeral number that changes every run, and naming it in a log
        /// sends whoever reads it to the wrong firewall rule. The target port is the server's, and
        /// on a client there is nothing here worth printing.
        /// </remarks>
        private string PortSuffix()
        {
            return TryGetTransport(out Transport transport) ? $" on port {transport.GetPort()}" : string.Empty;
        }

        private void Report(string message)
        {
            Debug.LogError($"{nameof(ConnectionPanel)}: {message}", this);
        }

        private static bool HasArgument(string name)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string ArgumentValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();

            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[i + 1];
            }

            return null;
        }
    }
}
