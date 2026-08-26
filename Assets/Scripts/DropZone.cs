using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// A labelled pallet/zone the player drops a crate onto.
    /// Holds at most one crate; the TrialManager reads Occupant to score the trial.
    /// </summary>
    public class DropZone : MonoBehaviour
    {
        [Tooltip("True if this is the zone where the LAGGY crate belongs.")]
        public bool isLaggyZone;

        public DraggableCrate Occupant { get; private set; }

        public bool IsFree => Occupant == null;

        public void Place(DraggableCrate crate)
        {
            Occupant = crate;
        }

        public void Clear(DraggableCrate crate)
        {
            if (Occupant == crate) Occupant = null;
        }

        public void Reset()
        {
            Occupant = null;
        }

        /// <summary>World position a snapped crate should sit at.</summary>
        public Vector3 SnapPoint(float crateHalfHeight)
        {
            Vector3 p = transform.position;
            p.y += crateHalfHeight + 0.06f; // rest just above the pallet surface
            return p;
        }
    }
}
