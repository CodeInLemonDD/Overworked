using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Overworked.Interaction
{
    /// <summary>
    /// Draws the arc a thrown object would follow, as a row of dots.
    /// </summary>
    /// <remarks>
    /// Local only, never networked. Built at runtime so the project needs no preview prefab.
    ///
    /// Dots rather than a LineRenderer on purpose. A LineRenderer with view alignment has to
    /// work out a quad orientation per segment from the camera, and that degenerates when the
    /// segment points along the view direction — the arc then collapses and disappears at
    /// particular player facings. Spheres look the same from every angle and need no such
    /// computation, so the preview cannot go missing based on which way the player is turned.
    ///
    /// The arc is a plain ballistic simulation stopped by a raycast. It deliberately does not
    /// work out which cell or which surface the object would land on: the shape of the throw
    /// is the whole point, and anything more would have to be re-derived every frame and would
    /// disagree with physics anyway.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ThrowTrajectoryPreview : MonoBehaviour
    {
        /// <summary>
        /// URP material property: 0 opaque, 1 transparent.
        /// </summary>
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");

        /// <summary>
        /// URP material property: blend mode.
        /// </summary>
        private static readonly int BlendId = Shader.PropertyToID("_Blend");

        /// <summary>
        /// URP material property: alpha clip toggle.
        /// </summary>
        private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");

        /// <summary>
        /// URP material property: source blend factor.
        /// </summary>
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");

        /// <summary>
        /// URP material property: destination blend factor.
        /// </summary>
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");

        /// <summary>
        /// URP material property: depth write toggle.
        /// </summary>
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

        /// <summary>
        /// URP material property: tint.
        /// </summary>
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// Longest the arc may be drawn, in seconds of flight.
        /// </summary>
        [Tooltip("Longest the arc may be drawn, in seconds of flight.")]
        [SerializeField]
        private float _maxFlightTime = 2.5f;

        /// <summary>
        /// Simulation step. Smaller is smoother and costs more.
        /// </summary>
        [Tooltip("Simulation step. Smaller is smoother and costs more.")]
        [SerializeField]
        private float _timeStep = 0.04f;

        /// <summary>
        /// How many dots to spread along the arc.
        /// </summary>
        [Tooltip("How many dots to spread along the arc.")]
        [Range(2, 64)]
        [SerializeField]
        private int _dotCount = 18;

        /// <summary>
        /// Diameter of each dot, in world units.
        /// </summary>
        [Tooltip("Diameter of each dot, in world units.")]
        [SerializeField]
        private float _dotSize = 0.055f;

        /// <summary>
        /// Colour of the dots.
        /// </summary>
        [Tooltip("Colour of the dots.")]
        [SerializeField]
        private Color _colour = new(1f, 1f, 1f, 0.55f);

        /// <summary>
        /// Dots, created once and reused.
        /// </summary>
        private readonly List<Transform> _dots = new();

        /// <summary>
        /// Simulated arc, reused between frames.
        /// </summary>
        private readonly List<Vector3> _arc = new();

        /// <summary>
        /// Hit buffer for the arc raycast.
        /// </summary>
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

        /// <summary>
        /// Material shared by every dot, released on destroy.
        /// </summary>
        private Material _material;

        /// <summary>
        /// Creates a trajectory preview with a full set of dots.
        /// </summary>
        public static ThrowTrajectoryPreview Create()
        {
            GameObject instance = new(nameof(ThrowTrajectoryPreview));

            ThrowTrajectoryPreview preview = instance.AddComponent<ThrowTrajectoryPreview>();
            preview.Build();
            preview.Hide();

            return preview;
        }

        /// <summary>
        /// Simulates and draws an arc.
        /// </summary>
        /// <param name="origin">Where the object leaves the hand.</param>
        /// <param name="velocity">Initial velocity, in world units per second.</param>
        /// <param name="ignore">Colliders belonging to this transform do not stop the arc.</param>
        public void Show(Vector3 origin, Vector3 velocity, Transform ignore)
        {
            Simulate(origin, velocity, ignore);

            if (_arc.Count < 2)
            {
                Hide();
                return;
            }

            int used = Mathf.Min(_dotCount, _arc.Count);
            for (int i = 0; i < _dots.Count; i++)
            {
                Transform dot = _dots[i];

                if (i >= used)
                {
                    dot.gameObject.SetActive(false);
                    continue;
                }

                /* Spread the dots evenly along the arc so they stay legible whether the arc
                 * was cut short by geometry or ran its full simulated length. */
                float t = used == 1 ? 0f : i / (float)(used - 1);
                int index = Mathf.Clamp(Mathf.RoundToInt(t * (_arc.Count - 1)), 0, _arc.Count - 1);

                dot.position = _arc[index];
                dot.gameObject.SetActive(true);
            }

            gameObject.SetActive(true);
        }

        /// <summary>
        /// Hides every dot.
        /// </summary>
        public void Hide()
        {
            foreach (Transform dot in _dots)
                dot.gameObject.SetActive(false);

            gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }

        /// <summary>
        /// Steps the arc forward under gravity until it hits something or runs out of time.
        /// </summary>
        private void Simulate(Vector3 origin, Vector3 velocity, Transform ignore)
        {
            _arc.Clear();

            int capacity = Mathf.Max(2, Mathf.CeilToInt(_maxFlightTime / _timeStep) + 1);

            Vector3 gravity = Physics.gravity;
            Vector3 position = origin;
            Vector3 currentVelocity = velocity;

            _arc.Add(position);

            for (int i = 1; i < capacity; i++)
            {
                currentVelocity += gravity * _timeStep;
                Vector3 next = position + currentVelocity * _timeStep;

                /* Stop at the first thing the arc would touch. No surface or cell logic is
                 * wanted here — the arc simply ends where it hits. */
                if (TryHit(position, next, ignore, out Vector3 hitPoint))
                {
                    _arc.Add(hitPoint);
                    return;
                }

                _arc.Add(next);
                position = next;
            }
        }

        /// <summary>
        /// Returns the closest hit along a segment, skipping <paramref name="ignore"/>.
        /// </summary>
        /// <remarks>
        /// A plain Linecast cannot be used: the arc starts at the hand, which sits just
        /// outside the player's own capsule, and any segment that clips it would end the arc
        /// immediately and leave a preview that looks like it is missing.
        /// </remarks>
        private bool TryHit(Vector3 from, Vector3 to, Transform ignore, out Vector3 point)
        {
            point = default;

            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.0001f)
                return false;

            int count = Physics.RaycastNonAlloc(from, delta / distance, _hitBuffer, distance, ~0, QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hitBuffer[i];

                if (ignore != null && hit.collider.transform.IsChildOf(ignore))
                    continue;
                if (hit.distance >= bestDistance)
                    continue;

                bestDistance = hit.distance;
                point = hit.point;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Builds the dots and their material.
        /// </summary>
        private void Build()
        {
            _material = CreateMaterial();

            /* **No material means no dots.** A renderer left with a null material is drawn with
             * Unity's missing-material shader, which is bright magenta — a colour that says "this
             * is broken" to everybody except the player, who is the one person it cannot be
             * explained to. The arc simply not being there reads as the game not having an arc,
             * which is the honest description of a build where its shader was stripped. */
            if (_material == null)
                return;

            for (int i = 0; i < _dotCount; i++)
            {
                GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dot.name = $"Dot{i}";
                dot.transform.SetParent(transform, worldPositionStays: false);
                dot.transform.localScale = Vector3.one * _dotSize;

                /* Disabled before Destroy because Destroy is deferred to the end of the
                 * frame, and a stray collider would be picked up by pickup queries and by
                 * the placement probe. */
                Collider collider = dot.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = false;
                    Destroy(collider);
                }

                MeshRenderer renderer = dot.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = _material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                _dots.Add(dot.transform);
            }
        }

        /// <summary>
        /// Creates the transparent unlit material every dot shares.
        /// </summary>
        private Material CreateMaterial()
        {
            /* **A shader that nothing references is not in the build.** Shader.Find searches the
             * whole project in the editor and only what was included at build time everywhere else,
             * so URP's Unlit -- which no material in this project uses -- resolves in Play mode and
             * comes back null in a build. That is the worst shape for the failure to have: the one
             * place it cannot be seen until it is too late to check.
             *
             * URP's Lit is the fallback precisely because something does reference it: every
             * material in the project is on it, so it is in the build whether or not anybody
             * arranged for it. The dots are shaded rather than flat, which is a fair price for
             * being drawn at all. */
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");

            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Lit");

            if (shader == null)
            {
                /* Said as an error and with the fix in it, because the alternative that ships is
                 * a row of magenta spheres — see Build, which refuses to draw them. */
                Debug.LogError(
                    $"{nameof(ThrowTrajectoryPreview)} found no shader for its dots, so the throw arc will not be drawn " +
                    "in this build. Add 'Universal Render Pipeline/Unlit' to Project Settings → Graphics → " +
                    "Always Included Shaders to get it back.",
                    this);

                return null;
            }

            Material material = new(shader);

            /* URP reads surface type and blend state from material properties rather than from
             * the shader's render state, so these all have to be set explicitly. */
            material.SetFloat(SurfaceId, 1f);
            material.SetFloat(BlendId, 0f);
            material.SetFloat(AlphaClipId, 0f);
            material.SetInt(SrcBlendId, (int)BlendMode.SrcAlpha);
            material.SetInt(DstBlendId, (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt(ZWriteId, 0);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");

            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetColor(BaseColorId, _colour);

            return material;
        }
    }
}
