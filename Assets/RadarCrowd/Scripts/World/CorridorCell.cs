using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Rectangular stretch of corridor floor: the unit of sensing (one radar per cell by default) and of route cost.
    /// Local z runs along the corridor and local x across it; the transform must have unit scale.
    /// </summary>
    public class CorridorCell : MonoBehaviour
    {
        [SerializeField] string cellId = "A1-1";
        [SerializeField] string segmentId = "A1";
        [SerializeField] float width = 4f;    // m, physical corridor width
        [SerializeField] float length = 9f;   // m

        public string CellId => cellId;
        public string SegmentId => segmentId;
        public float Width => width;            // clear width (the physical width unless the corridor is lined)
        public float Length => length;
        public float Area => width * length;

        float physicalWidth = -1f;
        /// <summary>Width between the walls of the building, before any lining.</summary>
        public float PhysicalWidth => physicalWidth > 0f ? physicalWidth : width;

        /// <summary>Narrows the cell to a clear width (fixed lining along the walls); never wider than the building allows.</summary>
        public void SetClearWidth(float clearWidth)
        {
            if (physicalWidth < 0f)
                physicalWidth = width;
            width = Mathf.Min(clearWidth, physicalWidth);
        }

        /// <summary>Position in the layout's cell list, assigned at start-up.</summary>
        public int Index { get; set; }

        public void Configure(string id, string segment, float cellWidth, float cellLength)
        {
            cellId = id;
            segmentId = segment;
            width = cellWidth;
            length = cellLength;
        }

        public Vector2 ToLocal(Vector3 p)
        {
            Vector3 d = p - transform.position;
            return new Vector2(Vector3.Dot(d, transform.right), Vector3.Dot(d, transform.forward));
        }

        public Vector3 ToWorld(float across, float along)
        {
            return transform.position + transform.right * across + transform.forward * along;
        }

        public bool Contains(Vector3 p)
        {
            Vector2 l = ToLocal(p);
            return Mathf.Abs(l.x) <= width * 0.5f && Mathf.Abs(l.y) <= length * 0.5f;
        }

        // ------------------------------------------------------------------ ground truth

        public int CountAgents(out int standing)
        {
            int n = 0;
            standing = 0;
            IReadOnlyList<CrowdAgent> agents = AgentRegistry.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                if (!Contains(agents[i].Position))
                    continue;
                n++;
                if (agents[i].IsStanding)
                    standing++;
            }
            return n;
        }

        public float TrueDensity()
        {
            return CountAgents(out _) / Area;
        }

        /// <summary>
        /// Free width of the cell: in every cross-section along the corridor, the widest gap left by the obstacles that
        /// overlap that cross-section; the narrowest cross-section counts (trolleys staggered along opposite walls leave
        /// the corridor passable, two trolleys facing each other do not).
        /// </summary>
        public float TrueFreeWidth()
        {
            footprints.Clear();
            IReadOnlyList<CorridorObstacle> obstacles = AgentRegistry.Obstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (!obstacles[i].isActiveAndEnabled)
                    continue;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 corner in obstacles[i].Corners())
                {
                    Vector2 l = ToLocal(corner);
                    minX = Mathf.Min(minX, l.x);
                    maxX = Mathf.Max(maxX, l.x);
                    minZ = Mathf.Min(minZ, l.y);
                    maxZ = Mathf.Max(maxZ, l.y);
                }
                if (maxZ < -length * 0.5f || minZ > length * 0.5f || maxX < -width * 0.5f || minX > width * 0.5f)
                    continue;
                footprints.Add(new Vector4(Mathf.Max(minX, -width * 0.5f), Mathf.Min(maxX, width * 0.5f),
                    Mathf.Max(minZ, -length * 0.5f), Mathf.Min(maxZ, length * 0.5f)));
            }
            if (footprints.Count == 0)
                return width;
            float narrowest = width;
            var occupied = new List<Vector2>();
            foreach (Vector4 slice in footprints)
            {
                // the gap is smallest where the most obstacles overlap: test the cross-section at the start of each footprint
                occupied.Clear();
                foreach (Vector4 f in footprints)
                    if (f.z <= slice.z + 1e-4f && f.w > slice.z + 1e-4f)
                        occupied.Add(new Vector2(f.x, f.y));
                narrowest = Mathf.Min(narrowest, LargestGap(occupied, -width * 0.5f, width * 0.5f));
            }
            return narrowest;
        }

        readonly List<Vector4> footprints = new List<Vector4>();   // (minX, maxX, minZ, maxZ) in cell coordinates

        /// <summary>Largest free gap in [lo, hi] not covered by the given occupied intervals (x = start, y = end).</summary>
        public static float LargestGap(List<Vector2> occupied, float lo, float hi)
        {
            if (occupied.Count == 0)
                return hi - lo;
            occupied.Sort((a, b) => a.x.CompareTo(b.x));
            float best = 0f;
            float cursor = lo;
            foreach (Vector2 iv in occupied)
            {
                if (iv.x > cursor)
                    best = Mathf.Max(best, iv.x - cursor);
                cursor = Mathf.Max(cursor, iv.y);
            }
            return Mathf.Max(best, hi - cursor);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            Vector3 a = ToWorld(-width * 0.5f, -length * 0.5f);
            Vector3 b = ToWorld(width * 0.5f, -length * 0.5f);
            Vector3 c = ToWorld(width * 0.5f, length * 0.5f);
            Vector3 d = ToWorld(-width * 0.5f, length * 0.5f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }
    }
}
