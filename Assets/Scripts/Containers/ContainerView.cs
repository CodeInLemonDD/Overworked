using System.Collections.Generic;
using Overworked.Visuals;
using UnityEngine;

namespace Overworked.Containers
{
    /// <summary>
    /// Draws a container's contents as a stack of visual stand-ins.
    /// </summary>
    /// <remarks>
    /// Local only, never networked, and entirely cosmetic. The stand-ins have no colliders
    /// and are not grabbable — walking up and taking an item goes through the container, not
    /// through these. Pulling one out spawns a real object rather than moving what is drawn
    /// here, which is what keeps the contents out of the grid world entirely.
    ///
    /// Rebuilt from the entry list on every change rather than diffed. The list is short and
    /// changes are rare, so a rebuild is cheaper than the bookkeeping a diff would need.
    /// </remarks>
    [DisallowMultipleComponent]
    public class ContainerView : MonoBehaviour
    {
        /// <summary>
        /// The container to draw.
        /// </summary>
        [Tooltip("The container to draw.")]
        [SerializeField]
        private ContainerBase _container;

        /// <summary>
        /// Where payload prefabs are looked up.
        /// </summary>
        [Tooltip("Where payload prefabs are looked up. Must be the same catalogue the grabbables use.")]
        [SerializeField]
        private PayloadCatalogue _catalogue;

        /// <summary>
        /// Parent for the stand-ins. Falls back to this transform.
        /// </summary>
        [Tooltip("Parent for the stand-ins. Falls back to this transform.")]
        [SerializeField]
        private Transform _stackRoot;

        /// <summary>
        /// Local offset of the first stand-in.
        /// </summary>
        [Tooltip("Local offset of the first stand-in.")]
        [SerializeField]
        private Vector3 _itemOffset = new(0f, 0.55f, 0f);

        /// <summary>
        /// Added once per item going up the stack.
        /// </summary>
        [Tooltip("Added once per item going up the stack.")]
        [SerializeField]
        private Vector3 _itemStep = new(0f, 0.04f, 0f);

        /// <summary>
        /// Uniform scale applied to each stand-in.
        /// </summary>
        [Tooltip("Uniform scale applied to each stand-in.")]
        [SerializeField]
        private float _itemScale = 1f;

        /// <summary>
        /// Whether physical entries are drawn.
        /// </summary>
        [Tooltip("Whether physical entries are drawn.")]
        [SerializeField]
        private bool _showEntities = true;

        /// <summary>
        /// Whether document entries are drawn. Off by default: a document's data has no
        /// representation yet, so drawing it would leave holes in the stack.
        /// </summary>
        [Tooltip("Whether document entries are drawn. Off by default; document data has no visual yet.")]
        [SerializeField]
        private bool _showData;

        /// <summary>
        /// Stand-ins currently drawn.
        /// </summary>
        private readonly List<GameObject> _visuals = new();

        private Transform Root => _stackRoot != null ? _stackRoot : transform;

        private void OnEnable()
        {
            if (_container != null)
                _container.ContentsChanged += Rebuild;

            Rebuild();
        }

        private void OnDisable()
        {
            if (_container != null)
                _container.ContentsChanged -= Rebuild;

            Clear();
        }

        /// <summary>
        /// Throws away the drawn stand-ins.
        /// </summary>
        private void Clear()
        {
            foreach (GameObject visual in _visuals)
            {
                if (visual != null)
                    Destroy(visual);
            }

            _visuals.Clear();
        }

        /// <summary>
        /// Recreates the stack from the container's current entries.
        /// </summary>
        private void Rebuild()
        {
            Clear();

            if (_container == null || _catalogue == null)
                return;

            int slot = 0;
            int count = _container.Count;

            for (int i = 0; i < count; i++)
            {
                if (!_container.TryGetEntry(i, out ContainerEntry entry))
                    continue;

                ContainerEntryKind kind = (ContainerEntryKind)entry.Kind;

                bool wanted = kind switch
                {
                    ContainerEntryKind.Entity => _showEntities,
                    ContainerEntryKind.Data => _showData,
                    _ => false,
                };

                if (!wanted)
                    continue;

                GameObject prefab = _catalogue.Get(entry.PayloadIndex);
                if (prefab == null)
                    continue;

                GameObject visual = CosmeticCopy.Instantiate(Root, prefab);
                if (visual == null)
                    continue;

                visual.transform.localPosition = _itemOffset + _itemStep * slot;

                /* The stand-ins are a stack, so it is drawn at a scale of its own choosing --
                 * applied on top of the copy's true size rather than instead of it. Replacing the
                 * scale outright would flatten every prefab authored with one axis shorter than
                 * the others. */
                visual.transform.localScale *= _itemScale;

                _visuals.Add(visual);
                slot++;
            }
        }
    }
}
