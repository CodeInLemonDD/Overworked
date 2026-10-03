using System.Collections.Generic;
using System.Text;
using FishNet.Object;
using Overworked.Containers;
using Overworked.Documents;
using Overworked.Interaction;
using Overworked.Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// A read-only debug overlay: what every container is holding, where the local player's
    /// stamina sits, and what the player is aiming at.
    /// </summary>
    /// <remarks>
    /// The whole hierarchy is built in code — canvas, panel, text — rather than authored into
    /// the scene, so this can be dropped onto any object without a scene or prefab edit. Both
    /// are YAML and cannot be merged, and only one window may touch them.
    ///
    /// Three properties of what gets built are load-bearing, and all three exist for the same
    /// reason: the FishNet demo keeps its logo and its Host/Client buttons in the top-left at
    /// sorting order 0, and MPPM testing cannot start without them. The canvas draws above
    /// them, it carries no GraphicRaycaster anywhere in the hierarchy, and every graphic it
    /// creates is marked as not a raycast target. Drop any one of the three and a full-screen
    /// overlay silently eats those two clicks. See CONSTRAINTS.md, convention C.
    ///
    /// The panel is anchored top-right for the same reason — a readout has no business sitting
    /// on top of the buttons it depends on.
    /// </remarks>
    [DisallowMultipleComponent]
    public class DebugHud : MonoBehaviour
    {
        #region Configuration.
        /// <summary>
        /// Font for the readout.
        /// </summary>
        [Tooltip("Font for the readout. Assign Assets/Font/simhei SDF.asset — the built-in TMP font has no Chinese glyphs.")]
        [SerializeField]
        private TMP_FontAsset _font;

        /// <summary>
        /// Sorting order given to the created canvas.
        /// </summary>
        [Tooltip("Sorting order of the created canvas. Clamped to at least 1 so the readout draws above the FishNet demo UI, which sits at 0.")]
        [SerializeField]
        private int _sortingOrder = 1;

        /// <summary>
        /// Text size, in reference-resolution pixels.
        /// </summary>
        [Tooltip("Text size, in reference-resolution pixels.")]
        [SerializeField]
        private float _fontSize = 22f;

        /// <summary>
        /// How often the readout is rebuilt, in seconds.
        /// </summary>
        [Tooltip("How often the readout is rebuilt, in seconds.")]
        [SerializeField]
        private float _refreshInterval = 0.1f;

        /// <summary>
        /// Tag identifying the player objects.
        /// </summary>
        [Tooltip("Tag identifying player objects, used to pick out the one this client owns.")]
        [SerializeField]
        private string _playerTag = "Player";

        /// <summary>
        /// Where entity payload indices are resolved to names.
        /// </summary>
        [Tooltip("Where entity payload indices are resolved to names. Optional — without it, entries are listed by index.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        /// <summary>
        /// How far ahead the aim readout looks, in world units.
        /// </summary>
        [Tooltip("How far ahead the aim readout looks, in world units.")]
        [SerializeField]
        private float _lookDistance = 4f;
        #endregion

        #region Layout constants.
        /// <summary>
        /// Margin between the panel and the screen edge.
        /// </summary>
        private const float Margin = 12f;

        /// <summary>
        /// Padding inside the panel.
        /// </summary>
        private const int Padding = 12;

        /// <summary>
        /// Panel width. Height is left to the content.
        /// </summary>
        private const float PanelWidth = 560f;

        /// <summary>
        /// Reference resolution the readout is scaled against.
        /// </summary>
        private static readonly Vector2 ReferenceResolution = new(1920f, 1080f);

        /// <summary>
        /// Cells in the stamina bar.
        /// </summary>
        private const int BarCells = 10;

        /// <summary>
        /// Cell drawn for a filled portion of the stamina bar.
        /// </summary>
        private const char BarFull = '█';

        /// <summary>
        /// Cell drawn for an empty portion of the stamina bar.
        /// </summary>
        private const char BarEmpty = '░';
        #endregion

        #region Runtime.
        /// <summary>
        /// Canvas created by this component.
        /// </summary>
        private Canvas _canvas;

        /// <summary>
        /// The one text object everything is written into.
        /// </summary>
        private TextMeshProUGUI _text;

        /// <summary>
        /// Reused so a rebuild allocates nothing but the strings TMP keeps anyway.
        /// </summary>
        private readonly StringBuilder _builder = new();

        /// <summary>
        /// Containers found on the last refresh.
        /// </summary>
        private readonly List<ContainerBase> _containers = new();

        /// <summary>
        /// Reused tally of payload indices within one container.
        /// </summary>
        /// <remarks>
        /// A list rather than a dictionary so the order entries were first seen in is the
        /// order they print in: a row that reshuffles itself every refresh is unreadable.
        /// </remarks>
        private readonly List<KeyValuePair<int, int>> _tally = new();

        /// <summary>
        /// Reused tally of document ids within one container.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="_tally"/> because the key means something else: there it is a
        /// payload index, here it is a document. Two documents of one kind are two documents, so
        /// counting them into one bucket would be the same as not naming them. The same
        /// list-of-pairs shape, for the same reason — first seen, first printed.
        /// </remarks>
        private readonly List<KeyValuePair<int, int>> _documentTally = new();

        /// <summary>
        /// When the readout is next rebuilt.
        /// </summary>
        private float _nextRefresh;

        /// <summary>
        /// The player this client owns, or null while there is none.
        /// </summary>
        private NetworkObject _localPlayer;
        #endregion

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            /* The canvas is a child, so destroying this object takes it along. This covers the
             * other case: the component removed at runtime while its object lives on. */
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        private void Update()
        {
            /* Unscaled, so the overlay keeps updating while the game is paused or running at a
             * reduced time scale — which is when a tester most wants to read it. */
            if (Time.unscaledTime < _nextRefresh)
                return;

            _nextRefresh = Time.unscaledTime + Mathf.Max(0.02f, _refreshInterval);

            Refresh();
        }

        /// <summary>
        /// Creates the canvas, panel and text.
        /// </summary>
        private void Build()
        {
            /* Created with its RectTransform and then given one component at a time, rather
             * than handing the whole list to the constructor. Same result either way, but
             * "does adding a Canvas imply a RectTransform" is not a question worth leaving open
             * in a file that cannot be compiled until someone switches to Unity. */
            GameObject canvasObject = new("Debug HUD", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, worldPositionStays: false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            /* Above the FishNet demo canvas at 0. Clamped rather than trusted, because the
             * failure mode of getting this wrong is an invisible overlay that still blocks
             * clicks, which is a genuinely confusing thing to debug. */
            _canvas.sortingOrder = Mathf.Max(1, _sortingOrder);

            /* No GraphicRaycaster is added anywhere in this hierarchy, deliberately. That is
             * what keeps the demo's Host/Client buttons clickable; see the class remarks. */

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;

            /* Split between the two axes, so the readout holds its proportions whether the
             * window is a full screen or one half of an MPPM pair. */
            scaler.matchWidthOrHeight = 0.5f;

            GameObject panelObject = new("Panel", typeof(RectTransform));
            panelObject.transform.SetParent(canvasObject.transform, worldPositionStays: false);

            RectTransform panel = (RectTransform)panelObject.transform;
            panel.anchorMin = new Vector2(1f, 1f);
            panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-Margin, -Margin);

            /* Width fixed, height left at zero for the fitter below to grow. A readout with two
             * containers should not sit in a box sized for twenty. */
            panel.sizeDelta = new Vector2(PanelWidth, 0f);

            Image background = panelObject.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.65f);
            background.raycastTarget = false;

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(Padding, Padding, Padding, Padding);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = panelObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject textObject = new("Text", typeof(RectTransform));
            textObject.transform.SetParent(panelObject.transform, worldPositionStays: false);

            /* Configured after AddComponent, not before: TMP's Awake loads defaults off TMP
             * Settings, so anything set before it would be overwritten. */
            _text = textObject.AddComponent<TextMeshProUGUI>();
            _text.raycastTarget = false;

            /* Every line here is one record. A wrapped one reads as two, so wrapping is off
             * even when that pushes a long container path past the panel edge. */
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.fontSize = _fontSize;
            _text.color = Color.white;

            if (_font != null)
            {
                _text.font = _font;
            }
            else
            {
                Debug.LogError(
                    $"{nameof(DebugHud)} on {gameObject.name} has no {nameof(TMP_FontAsset)} assigned; " +
                    "the readout will fall back to the built-in font, which draws no Chinese. " +
                    "Assign Assets/Font/simhei SDF.asset.",
                    this);
            }
        }

        /// <summary>
        /// Rebuilds the whole readout.
        /// </summary>
        /// <remarks>
        /// The container list is rescanned on every refresh rather than cached. Stations spawn
        /// containers while the game runs, and at this size — a scene with a handful of them,
        /// ten times a second — a scan costs less than the bookkeeping needed to notice a new
        /// one any other way.
        /// </remarks>
        private void Refresh()
        {
            if (_text == null)
                return;

            /* Re-acquired whenever it is gone or no longer ours, which covers the first refresh
             * before anything has spawned as well as a disconnect. */
            if (_localPlayer == null || !_localPlayer.IsOwner)
                AcquireLocalPlayer();

            _builder.Clear();

            AppendContainers(_builder);
            AppendStamina(_builder);
            AppendAim(_builder);

            _text.SetText(_builder);
        }

        /// <summary>
        /// Finds the player this client owns, and the stamina component on it.
        /// </summary>
        /// <remarks>
        /// Ownership is the discriminator, matching how the camera picks its target: on a client
        /// exactly one player belongs to it, and on a host that is the host's own player.
        /// </remarks>
        private void AcquireLocalPlayer()
        {
            _localPlayer = null;

            NetworkObject[] candidates = FindObjectsByType<NetworkObject>(FindObjectsInactive.Exclude);

            foreach (NetworkObject candidate in candidates)
            {
                if (candidate == null || !candidate.IsOwner)
                    continue;

                /* Guarded because CompareTag throws on an empty or undefined tag, and this runs
                 * from Update — a cleared field would fill the console ten times a second. */
                if (!string.IsNullOrEmpty(_playerTag) && !candidate.CompareTag(_playerTag))
                    continue;

                _localPlayer = candidate;
                break;
            }
        }

        /// <summary>
        /// Writes one line per container in the scene.
        /// </summary>
        private void AppendContainers(StringBuilder builder)
        {
            _containers.Clear();
            _containers.AddRange(FindObjectsByType<ContainerBase>(FindObjectsInactive.Exclude));

            /* Sorted so rows keep their places between refreshes; FindObjectsByType returns them
             * in an order that has nothing to do with anything on screen. */
            _containers.Sort(CompareContainers);

            /* Every grabbable in the game carries a container: they are all made from the one
             * object prefab, and only the folders have any use for it. Empty ones are left out —
             * a sheet of paper going about its business is not a stock level anybody is asking
             * about, and there is one of these per object in the world, which would bury the
             * machines this list exists to report on. A folder with documents in it still shows. */
            int shown = 0;
            for (int i = 0; i < _containers.Count; i++)
            {
                if (_containers[i] != null && ShouldReport(_containers[i]))
                    shown++;
            }

            builder.Append("容器 (").Append(shown).Append(')');

            if (shown == 0)
            {
                builder.Append("\n  (无)");
                return;
            }

            foreach (ContainerBase container in _containers)
            {
                if (container == null || !ShouldReport(container))
                    continue;

                builder.Append("\n  ").Append(Label(container));
                builder.Append("  ").Append(container.Count).Append('/');
                builder.Append(container.IsUnlimited ? "∞" : container.Capacity.ToString());
                builder.Append("  ");
                AppendContents(builder, container);
            }
        }

        /// <summary>
        /// True when a container is worth a line.
        /// </summary>
        /// <remarks>
        /// A scene container always is: those are the machines, and an empty one is exactly what
        /// somebody is looking for when they open this. A container on a spawned object is one
        /// somebody is carrying, and an empty one of those is the object itself.
        /// </remarks>
        private static bool ShouldReport(ContainerBase container)
        {
            if (container.Count > 0)
                return true;

            NetworkObject nob = container.NetworkObject;
            return nob == null || nob.IsSceneObject;
        }

        /// <summary>
        /// Writes what one container holds, grouped by payload.
        /// </summary>
        private void AppendContents(StringBuilder builder, ContainerBase container)
        {
            int count = container.Count;
            if (count == 0)
            {
                builder.Append("(空)");
                return;
            }

            _tally.Clear();
            _documentTally.Clear();

            for (int i = 0; i < count; i++)
            {
                if (!container.TryGetEntry(i, out ContainerEntry entry))
                    continue;

                switch ((ContainerEntryKind)entry.Kind)
                {
                    case ContainerEntryKind.Entity:
                        Tally(_tally, entry.PayloadIndex);
                        break;

                    case ContainerEntryKind.Data:
                        Tally(_documentTally, entry.DataId);
                        break;
                }
            }

            bool first = true;

            foreach (KeyValuePair<int, int> entry in _tally)
            {
                if (!first)
                    builder.Append(", ");
                first = false;

                builder.Append(PayloadName(entry.Key)).Append(" x").Append(entry.Value);
            }

            /* One name per document, not one count per kind. "Excel 1" and "Excel 2" are
             * different documents, and a request names one of them: a readout that collapses
             * both into "x2" cannot answer the question it is being read to answer. */
            foreach (KeyValuePair<int, int> entry in _documentTally)
            {
                if (!first)
                    builder.Append(", ");
                first = false;

                builder.Append(DocumentName(entry.Key)).Append(" x").Append(entry.Value);
            }

            /* Entries were held but none of them were an entity or a document — a kind this
             * readout does not know about. Say so rather than leaving the line blank. */
            if (first)
                builder.Append("(?)");
        }

        /// <summary>
        /// Counts one more of a payload index.
        /// </summary>
        private static void Tally(List<KeyValuePair<int, int>> tally, int payloadIndex)
        {
            for (int i = 0; i < tally.Count; i++)
            {
                if (tally[i].Key != payloadIndex)
                    continue;

                tally[i] = new KeyValuePair<int, int>(payloadIndex, tally[i].Value + 1);
                return;
            }

            tally.Add(new KeyValuePair<int, int>(payloadIndex, 1));
        }

        /// <summary>
        /// Writes the local player's stamina, when there is a local player to read it from.
        /// </summary>
        /// <remarks>
        /// Taken from the static rather than found by scanning the scene. Every client holds a
        /// copy of this component for every player, so a scan would need its own answer to "which
        /// one is mine" — and PlayerStamina already keeps that answer, which is the whole reason
        /// the static exists. Reading a remote player's copy would show a permanently full bar,
        /// because only the owner simulates stamina at all.
        /// </remarks>
        private void AppendStamina(StringBuilder builder)
        {
            PlayerStamina stamina = PlayerStamina.Local;
            if (stamina == null)
                return;

            float cap = stamina.StaminaCap;
            float current = stamina.Stamina;

            builder.Append("\n\n体力  ");
            builder.Append(Mathf.RoundToInt(current));
            builder.Append(" / ");
            builder.Append(Mathf.RoundToInt(cap));
            builder.Append("  [");
            AppendBar(builder, cap > 0f ? current / cap : 0f);
            builder.Append("]  速度 x");
            builder.Append(stamina.SpeedMultiplier.ToString("0.00"));
        }

        /// <summary>
        /// Writes a fixed-width bar for a fraction between zero and one.
        /// </summary>
        private static void AppendBar(StringBuilder builder, float fraction)
        {
            int filled = Mathf.Clamp(Mathf.RoundToInt(fraction * BarCells), 0, BarCells);

            for (int i = 0; i < BarCells; i++)
                builder.Append(i < filled ? BarFull : BarEmpty);
        }

        /// <summary>
        /// Writes whatever the player is aiming at.
        /// </summary>
        /// <remarks>
        /// Fired from the player along its own forward rather than from the camera. The camera
        /// sits behind and above the player looking down at them, so a camera ray would report
        /// the player every single time. The player's forward is the same direction pickups and
        /// throws are measured along, so this reads as "what E would target".
        ///
        /// The nearest NetworkObject up the hierarchy is preferred to the collider's own object,
        /// because the container on a machine is a child of it and "which machine is this" is
        /// the useful answer. Nothing here needs an interface from the stations; a station the
        /// player looks at simply shows up by name once one exists.
        /// </remarks>
        private void AppendAim(StringBuilder builder)
        {
            if (_localPlayer == null)
                return;

            Transform aim = _localPlayer.transform;

            /* From roughly eye height rather than from the player's feet, which would spend the
             * whole ray inside the floor. */
            Vector3 origin = aim.position + Vector3.up * 0.8f;

            if (!Physics.Raycast(origin, aim.forward, out RaycastHit hit, _lookDistance, ~0, QueryTriggerInteraction.Ignore))
                return;

            NetworkObject target = hit.collider.GetComponentInParent<NetworkObject>();

            builder.Append("\n\n注视  ");
            builder.Append(target != null ? target.gameObject.name : hit.collider.gameObject.name);
        }

        /// <summary>
        /// A readable name for a container.
        /// </summary>
        /// <remarks>
        /// Qualified by the parent, because a machine carries several containers — a printer has
        /// an input and an output — and they are named "Input" and "Output" on child objects.
        /// The parent is what tells them apart.
        ///
        /// A carried container has neither: it is made from the one object prefab, so it is called
        /// "Object" and has no parent. What it actually is, is its payload.
        /// </remarks>
        private string Label(ContainerBase container)
        {
            NetworkGrabbable grabbable = container.GetComponent<NetworkGrabbable>();
            if (grabbable != null)
                return PayloadName(grabbable.PayloadIndex);

            Transform parent = container.transform.parent;

            return parent != null ? $"{parent.name}/{container.name}" : container.name;
        }

        /// <summary>
        /// Orders containers by the label they print under.
        /// </summary>
        private int CompareContainers(ContainerBase left, ContainerBase right)
        {
            if (left == null)
                return right == null ? 0 : 1;
            if (right == null)
                return -1;

            return string.CompareOrdinal(Label(left), Label(right));
        }

        /// <summary>
        /// A document's identity: which kind it is, and which number.
        /// </summary>
        /// <remarks>
        /// Two fallbacks rather than one, because there are two ways to be told less than the
        /// whole story. A document whose kind has been removed from the catalogue still has a
        /// number and a kind index; a store that has not replicated yet has neither, and the id
        /// is all there is. Neither is a reason to leave the entry out — a readout that says a
        /// container holds less than it does is worse than one with an ugly label.
        /// </remarks>
        private static string DocumentName(int documentId)
        {
            DocumentStore store = DocumentStore.Instance;
            if (store == null || !store.TryGet(documentId, out DocumentRecord record))
                return $"文档 {documentId}";

            string name = store.TryGetSpec(documentId, out DocumentCatalogue.Spec spec)
                ? $"{spec.DisplayName} {record.Number}"
                : $"种类 {record.SpecIndex} #{record.Number}";

            /* Which side it belongs to, because both teams have an Excel 1 and a queue can be
             * holding both — they would otherwise print as the same name twice, and a readout that
             * cannot tell apart what it is listing has failed at its one job.
             *
             * The number as-is rather than a letter, matching what the console's 'docs' prints.
             * A letter would be a second copy of the panel's mapping, in a file that has no
             * business knowing what the round calls its teams. */
            return $"{name} team {record.Team}";
        }

        /// <summary>
        /// Names a payload index.
        /// </summary>
        /// <remarks>
        /// The index is printed when there is no catalogue wired or it points at nothing. That
        /// still tells a tester which entry is which, and it keeps the readout honest instead of
        /// showing the same placeholder for every unidentified entry.
        /// </remarks>
        private string PayloadName(int payloadIndex)
        {
            if (_catalogue != null)
            {
                GameObject prefab = _catalogue.Get(payloadIndex);
                if (prefab != null)
                    return prefab.name;
            }

            return $"payload {payloadIndex}";
        }
    }
}
