using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Attach to your Tower Pack prefab. Holds its ground/base position and toggles
    /// its renderers so the player only sees it when a shot reveals it.
    /// </summary>
    public class RadioTower : MonoBehaviour
    {
        [Tooltip("Renderers toggled on reveal/hide. If empty, all child renderers are used.")]
        public Renderer[] renderers;

        [Tooltip("Optional explicit base/ground point. If set, MoveTo plants that point at the target Y.")]
        public Transform basePoint;

        bool  _visible = true;
        float _groundOffset; // pivot height above the mesh's lowest point

        void Awake()
        {
            if (renderers == null || renderers.Length == 0)
                renderers = GetComponentsInChildren<Renderer>(true);
            ComputeGroundOffset();
            SetVisible(false);
        }

        // Pivot height above the mesh bottom (computed once at Start when bounds are valid).
        void ComputeGroundOffset()
        {
            if (basePoint != null) { _groundOffset = 0f; return; }

            float minY = float.MaxValue;
            foreach (var r in renderers)
                if (r != null) minY = Mathf.Min(minY, r.bounds.min.y);

            _groundOffset = minY < float.MaxValue ? transform.position.y - minY : 0f;
        }

        public Vector3 BasePosition => basePoint != null ? basePoint.position : transform.position;
        public bool IsVisible => _visible;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            foreach (var r in renderers)
                if (r != null) r.enabled = visible;
        }

        /// <summary>
        /// Moves the tower so its ground point lands at worldPos.
        /// Uses basePoint if assigned, otherwise the lowest mesh bound.
        /// </summary>
        public void MoveTo(Vector3 worldPos)
        {
            if (basePoint != null)
            {
                // Shift transform so basePoint.position == worldPos.
                Vector3 delta = basePoint.position - transform.position;
                transform.position = worldPos - delta;
            }
            else
            {
                // Raise pivot by _groundOffset so the mesh bottom sits at worldPos.y.
                transform.position = new Vector3(worldPos.x, worldPos.y + _groundOffset, worldPos.z);
            }
        }
    }
}
