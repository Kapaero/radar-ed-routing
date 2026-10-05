using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>A place along a solid corridor wall where a trolley can wait when no cubicle is free ("corridor care").</summary>
    public sealed class CorridorSpot
    {
        public int Index;
        public CorridorCell Cell;
        public int Side;
        public Vector3 Position;      // trolley centre
        public Vector3 StandPosition; // where a companion stands, on the corridor side of the trolley
        public Quaternion Rotation;
        public PatientAgent Occupant;
    }

    /// <summary>
    /// Scene references of the hospital wing: key points, functional zones, corridor graph, corridor cells, cubicles,
    /// corridor waiting spots, and the route libraries for walking patients and stretcher transports.
    /// The building model has no labelled rooms, so functional zones are assigned to existing markers:
    /// staff base = "EmergencyArea", radiology = "EmergencyArea (3)" (entered from the top corridor), amenities =
    /// the waiting room, wards = the main entrance.
    /// </summary>
    public class HospitalLayout : MonoBehaviour
    {
        const string StaffBaseName = "EmergencyArea";
        const string RadiologyName = "EmergencyArea (3)";
        public const float SpotGap = 0.6f;   // m between consecutive waiting trolleys

        [SerializeField] Transform entrance;
        [SerializeField] Transform registration;
        [SerializeField] Transform wingEntry;
        [SerializeField] Transform ambulanceEntrance;
        [SerializeField] int stretcherAgentTypeId;
        [SerializeField] CorridorNode[] nodes = new CorridorNode[0];
        [SerializeField] CorridorCell[] cells = new CorridorCell[0];

        readonly List<Cubicle> cubicles = new List<Cubicle>();
        readonly List<Vector3> walkerEndpoints = new List<Vector3>();
        readonly List<CorridorSpot> spots = new List<CorridorSpot>();

        public Vector3 EntrancePos { get; private set; }
        public Vector3 RegistrationPos { get; private set; }
        public Vector3 WingEntryPos { get; private set; }
        public Vector3 AmbulanceEntrancePos { get; private set; }
        public Vector3 StaffBasePos { get; private set; }
        public Vector3 RadiologyPos { get; private set; }
        public Vector3 RadiologyAccessPos { get; private set; }   // point of the top corridor in front of radiology
        public Vector3 AmenitiesPos => RegistrationPos;
        public Vector3 WardExitPos => EntrancePos;
        public int StretcherAgentTypeId => stretcherAgentTypeId;
        public bool HasStretcherNavMesh => stretcherAgentTypeId != 0 && ambulanceEntrance != null;
        public IReadOnlyList<CorridorCell> Cells => cells;
        public IReadOnlyList<CorridorNode> Nodes => nodes;
        public IReadOnlyList<Cubicle> Cubicles => cubicles;
        public IReadOnlyList<Vector3> WalkerEndpoints => walkerEndpoints;
        public IReadOnlyList<CorridorSpot> Spots => spots;
        public RouteBook Routes { get; private set; }
        public RouteBook StretcherRoutes { get; private set; }

        public RouteBook RoutesFor(PatientAgent patient) => patient.Stretcher && StretcherRoutes != null ? StretcherRoutes : Routes;

        public void Configure(Transform entrancePoint, Transform registrationPoint, Transform wingEntryPoint,
            CorridorNode[] graphNodes, CorridorCell[] corridorCells)
        {
            entrance = entrancePoint;
            registration = registrationPoint;
            wingEntry = wingEntryPoint;
            nodes = graphNodes;
            cells = corridorCells;
        }

        public void ConfigureStretchers(Transform ambulanceEntrancePoint, int agentTypeId)
        {
            ambulanceEntrance = ambulanceEntrancePoint;
            stretcherAgentTypeId = agentTypeId;
        }

        public void Initialize(float maxDetourRatio, int maxRoutes, float trolleyWidth, float trolleyLength)
        {
            EntrancePos = RouteBook.Snap(entrance.position);
            RegistrationPos = RouteBook.Snap(registration.position);
            WingEntryPos = RouteBook.Snap(wingEntry.position);
            for (int i = 0; i < cells.Length; i++)
                cells[i].Index = i;

            StaffBasePos = RouteBook.Snap(FindByName(StaffBaseName, WingEntryPos));
            RadiologyPos = RouteBook.Snap(FindByName(RadiologyName, WingEntryPos));
            RadiologyAccessPos = RouteBook.Snap(new Vector3(RadiologyPos.x, 0f, WingEntryPos.z));

            cubicles.Clear();
            var tagged = GameObject.FindGameObjectsWithTag("Cubicle").Select(g => (g, wing: "R"))
                .Concat(GameObject.FindGameObjectsWithTag("CubicleLeft").Select(g => (g, wing: "L")))
                .OrderBy(t => t.g.name, System.StringComparer.Ordinal);
            foreach (var (g, wing) in tagged)
                cubicles.Add(new Cubicle { Index = cubicles.Count, Name = g.name, Wing = wing, Position = RouteBook.Snap(g.transform.position) });

            walkerEndpoints.Clear();
            walkerEndpoints.AddRange(nodes.Where(n => n.WalkerEndpoint).Select(n => RouteBook.Snap(n.transform.position)));

            List<CorridorNode> graph = nodes.Where(n => n.Neighbours.Length > 0).ToList();
            Routes = RouteBook.Build(WingEntryPos, cubicles, graph, cells, maxDetourRatio, maxRoutes, RouteBook.Walking);
            if (HasStretcherNavMesh)
            {
                var filter = RouteBook.FilterFor(stretcherAgentTypeId);
                AmbulanceEntrancePos = RouteBook.Snap(ambulanceEntrance.position, filter);
                StretcherRoutes = RouteBook.Build(RouteBook.Snap(wingEntry.position, filter), cubicles, graph, cells,
                    maxDetourRatio, maxRoutes, filter);
            }
            BuildSpots(trolleyWidth, trolleyLength);
        }

        /// <summary>
        /// Narrows every corridor cell to a clear width with fixed lining along its walls (built-in cupboards, parked
        /// equipment): carving boxes along the solid stretches of wall, door openings and side corridors left open.
        /// </summary>
        /// <remarks>Call before Initialize and let the NavMesh be re-carved (two frames) before routes are built.</remarks>
        public void LineCorridors(float clearWidth)
        {
            const float step = 0.3f;
            var root = new GameObject("Corridor lining").transform;
            foreach (CorridorCell cell in cells)
            {
                if (cell.PhysicalWidth <= clearWidth + 0.05f)
                    continue;
                float half = cell.Length * 0.5f;
                foreach (int side in new[] { -1, 1 })
                {
                    float runStart = float.NaN, last = float.NaN;
                    for (float z = -half; z <= half + 1e-3f; z += step)
                    {
                        bool solid = CorridorCare.SolidWall(cell, side, z, 0f);   // closed within DoorClearance around z
                        if (solid)
                        {
                            if (float.IsNaN(runStart)) runStart = z;
                            last = z;
                        }
                        bool endOfRun = !solid || z + step > half + 1e-3f;
                        if (endOfRun && !float.IsNaN(runStart))
                        {
                            if (last - runStart >= step)
                                AddLining(root, cell, side, runStart - 0.5f * step, last + 0.5f * step, clearWidth);
                            runStart = float.NaN;
                        }
                    }
                }
                cell.SetClearWidth(clearWidth);
            }
        }

        static void AddLining(Transform root, CorridorCell cell, int side, float from, float to, float clearWidth)
        {
            float inner = clearWidth * 0.5f, outer = cell.PhysicalWidth * 0.5f + 0.1f;
            var go = new GameObject("Lining " + cell.CellId + (side < 0 ? " L" : " R"));
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(cell.ToWorld(side * 0.5f * (inner + outer), 0.5f * (from + to)), cell.transform.rotation);
            var obstacle = go.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.size = new Vector3(outer - inner, 2f, to - from);
            obstacle.center = new Vector3(0f, 1f, 0f);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;   // carve at once, not after standing still for 0.5 s
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            visual.transform.localScale = new Vector3(outer - inner, 1.2f, to - from);
        }

        static Vector3 FindByName(string name, Vector3 fallback)
        {
            GameObject go = GameObject.Find(name);
            if (go == null)
            {
                Debug.LogWarning("HospitalLayout: marker '" + name + "' not found");
                return fallback;
            }
            return go.transform.position;
        }

        /// <summary>Waiting spots along solid walls of every corridor cell, nearest to the wing entry first.</summary>
        void BuildSpots(float trolleyWidth, float trolleyLength)
        {
            spots.Clear();
            var found = new List<CorridorSpot>();
            foreach (CorridorCell cell in cells)
            {
                // in narrow corridors trolleys facing each other leave less than a stretcher's width (staff push one
                // aside when a stretcher has to pass), and a relative stands in the gap at the head end, not beside it
                bool narrow = cell.Width < 2f * trolleyWidth + 1.2f;
                foreach (int side in new[] { -1, 1 })
                {
                    float half = cell.Length * 0.5f - trolleyLength * 0.5f - 0.5f;
                    for (float along = -half; along <= half + 1e-3f; along += trolleyLength + SpotGap)
                    {
                        if (!CorridorCare.SolidWall(cell, side, along, trolleyLength))
                            continue;
                        float across = side * (cell.Width * 0.5f - trolleyWidth * 0.5f - 0.05f);
                        Vector3 standPoint = narrow
                            ? cell.ToWorld(side * (cell.Width * 0.5f - 0.3f), along + trolleyLength * 0.5f + SpotGap * 0.5f)
                            : cell.ToWorld(side * (cell.Width * 0.5f - trolleyWidth - 0.05f - 0.35f), along);
                        found.Add(new CorridorSpot
                        {
                            Cell = cell,
                            Side = side,
                            Position = cell.ToWorld(across, along),
                            StandPosition = RouteBook.Snap(standPoint),
                            Rotation = cell.transform.rotation,
                        });
                    }
                }
            }
            foreach (CorridorSpot s in found.OrderBy(s => Vector3.Distance(s.Position, WingEntryPos)))
            {
                s.Index = spots.Count;
                spots.Add(s);
            }
        }
    }
}
