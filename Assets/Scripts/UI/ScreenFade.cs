using Overworked.Match;
using UnityEngine;
using UnityEngine.UI;

namespace Overworked.UI
{
    /// <summary>
    /// Covers the screen while the players are moved from the lobby into the office.
    /// </summary>
    /// <remarks>
    /// **It exists to hide one frame.** Everything about the transition — the elevators closing,
    /// the ride, the office being assembled — is a teleport and a shuffle, and the whole of the
    /// illusion is that nobody sees the moment the two halves join. A cut to black and back is all
    /// that takes, and it is deliberately all this does: there is no loading behind it, because
    /// there is nothing to load. The office is in the same scene and always was.
    ///
    /// **Two different things make it black, and they meet.** The last second of the elevator
    /// countdown darkens the screen, and the handoff keeps it dark for
    /// <see cref="OfficeLayout.FlightDelaySeconds"/> while the players arrive and the office is
    /// staged. The second window is read off the layout rather than configured here, because two
    /// durations that have to agree is a transition that breaks the first time somebody tunes one
    /// of them.
    ///
    /// **Built in code, like the banner and the debug readout.** Adding a canvas by hand is a
    /// prefab to keep in step with this script; the other two panels in this folder already decided
    /// that, and following them means this one arrives working. See MATCHBANNER for the rules that
    /// apply to every canvas this project creates: no GraphicRaycaster, nothing is a raycast
    /// target, and the sorting order is above everything else.
    ///
    /// **It is drawn over the countdown, not under it.** That is what makes the lobby fade read as
    /// the screen going dark rather than as the number going dim — and the countdown comes back
    /// when the fade does, which is a second after the descent starts.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ScreenFade : MonoBehaviour
    {
        /// <summary>
        /// Resolution the layout is authored against, matching the other code-built panels.
        /// </summary>
        private static readonly Vector2 ReferenceResolution = new(1920f, 1080f);

        /// <summary>
        /// Sorting order given to the created canvas.
        /// </summary>
        /// <remarks>
        /// Above the banner at two, and above everything else in the project. A screen fade that
        /// something can draw on top of is not a screen fade — the one thing this has to be able to
        /// promise is that nothing is visible through it.
        /// </remarks>
        [Tooltip("Sorting order of the created canvas. Above the match banner, which sits at 2.")]
        [SerializeField]
        private int _sortingOrder = 3;

        /// <summary>
        /// How long the screen takes to go dark, or to come back.
        /// </summary>
        [Tooltip("How long the screen takes to darken or to clear.")]
        [Min(0.01f)]
        [SerializeField]
        private float _fadeSeconds = 0.45f;

        /// <summary>
        /// How many seconds of countdown are left when the screen starts going dark.
        /// </summary>
        /// <remarks>
        /// **The last second of the elevator countdown, and it is a whole number on purpose** —
        /// the countdown is replicated in whole seconds, so a value between two of them would spend
        /// most of its time comparing against a number that has not been sent yet. One means the
        /// screen starts going dark as the display reads "1", which is what an elevator door closing
        /// looks like.
        ///
        /// A countdown that is **cancelled clears the screen again**, because this is read off the
        /// starter rather than latched. Somebody stepping out of their elevator and back in gets the
        /// full countdown, and gets the lights back on with it.
        /// </remarks>
        [Tooltip("Seconds of countdown left when the screen starts going dark.")]
        [Min(1f)]
        [SerializeField]
        private int _closingCountdownSeconds = 1;

        /// <summary>
        /// How long to stay dark for when there is no layout in the scene.
        /// </summary>
        /// <remarks>
        /// A scene that has not been wired to a layout yet still has a handoff, and a handoff with
        /// no black over it is a teleport players watch happen. This is the fallback for exactly
        /// that case and is never used once an <see cref="OfficeLayout"/> exists.
        /// </remarks>
        [Tooltip("How long to stay dark when the scene has no OfficeLayout to read the duration from.")]
        [Min(0f)]
        [SerializeField]
        private float _defaultHoldSeconds = 1f;

        /// <summary>
        /// The colour the screen goes.
        /// </summary>
        [Tooltip("What the screen fades to. Black, unless the lobby is going somewhere bright.")]
        [SerializeField]
        private Color _colour = Color.black;

        /// <summary>
        /// The canvas, or null until the first refresh.
        /// </summary>
        private GameObject _canvasObject;

        /// <summary>
        /// Drives the whole overlay's alpha.
        /// </summary>
        private CanvasGroup _group;

        /// <summary>
        /// Seconds since this peer saw the handoff.
        /// </summary>
        private float _sinceDepart;

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            if (_canvasObject != null)
                Destroy(_canvasObject);
        }

        /// <summary>
        /// Builds the canvas and the black panel.
        /// </summary>
        private void Build()
        {
            _canvasObject = new GameObject("Screen Fade", typeof(RectTransform));
            _canvasObject.transform.SetParent(transform, worldPositionStays: false);

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Mathf.Max(1, _sortingOrder);

            /* No GraphicRaycaster, for the reason the banner gives: this hierarchy has to stay
             * click-through or the demo's Host/Client buttons stop working. */
            CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            /* The alpha lives on the canvas root rather than on the image, so that whatever gets
             * put behind this later — a spinner, a line of text, a logo — fades with it instead of
             * sitting at full brightness on a black screen. */
            _group = _canvasObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            GameObject panel = new("Black", typeof(RectTransform));
            panel.transform.SetParent(_canvasObject.transform, worldPositionStays: false);

            RectTransform rect = (RectTransform)panel.transform;

            /* Stretched to all four edges rather than sized to the reference resolution, so this
             * covers a window of any shape. A fade with a letterbox around it is worse than no
             * fade at all. */
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = panel.AddComponent<Image>();
            image.color = _colour;
            image.raycastTarget = false;
        }

        /// <summary>
        /// Drives the overlay towards whatever the round is doing.
        /// </summary>
        /// <remarks>
        /// **A target it moves towards, rather than a coroutine that runs to completion.** Every
        /// reason to be black is a piece of replicated state, and a state read every frame is a
        /// state that can change its mind — which is what lets a cancelled countdown bring the
        /// lights back up without anything having to remember that it faded them down. The cost is
        /// that the fade is only as smooth as the state is stable, which for a countdown and a
        /// handoff it is.
        /// </remarks>
        private void Update()
        {
            if (_group == null)
                return;

            MatchStarter starter = MatchStarter.Instance;
            bool departing = starter != null && starter.IsDeparting;

            if (departing)
                _sinceDepart += Time.deltaTime;
            else
                _sinceDepart = 0f;

            float target = TargetAlpha(starter, departing);
            float step = Time.deltaTime / Mathf.Max(_fadeSeconds, 0.01f);

            _group.alpha = Mathf.MoveTowards(_group.alpha, target, step);
        }

        /// <summary>
        /// Whether the screen should be black this frame.
        /// </summary>
        private float TargetAlpha(MatchStarter starter, bool departing)
        {
            /* The ride. Both the move and the staging happen in here, and neither should be
             * watchable — see the class remarks. */
            if (departing)
            {
                OfficeLayout layout = OfficeLayout.Instance;
                float hold = layout != null ? layout.FlightDelaySeconds : _defaultHoldSeconds;

                return _sinceDepart < hold ? 1f : 0f;
            }

            /* Not departing, so the only other reason to be dark is that the elevators are about to
             * close. A countdown that is not running is a lobby with nothing decided in it, and that
             * is a screen the players should be able to see. */
            if (starter == null || !starter.IsCountingDown)
                return 0f;

            return starter.CountdownRemaining <= _closingCountdownSeconds ? 1f : 0f;
        }
    }
}
