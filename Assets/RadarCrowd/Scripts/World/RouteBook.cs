using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>A treatment cubicle in one of the two wings.</summary>
    public sealed class Cubicle
    {
        public int Index;
        public string Name;
        public string Wing;          // "R" (tag Cubicle) or "L" (tag CubicleLeft)
        public Vector3 Position;     // snapped to the NavMesh
        public PatientAgent Occupant;
        public bool Dirty;            // vacated and waiting for cleaning
        public bool IsFree => Occupant == null && !Dirty;
    }

    /// <summary>One candidate path between the wing entry and a cubicle, measured on the obstacle-free NavMesh.</summary>
    public sealed class Route
    {
        public int Id;
        public Cubicle Cubicle;
        public bool Inbound;            // wing entry -> cubicle (false: cubicle -> wing entry)
        public string Signature;        // ordered corridor segments, e.g. "A1>M>B2"
        public Vector3[] Waypoints;     // polyline resampled every few metres; the last point is the destination
        public float Length;            // m
        public float[] CellLength;      // m travelled inside each corridor cell (indexed by CorridorCell.Index)
        public float FreeLength;        // m travelled outside all cells
        public bool UsesCell(int cell) => CellLength[cell] > 0.5f;
    }

    /// <summary>
    /// Library of candidate routes. For every cubicle, routes are generated as NavMesh paths through every simple
    /// sequence of corridor-graph junctions; routes that traverse the same corridor segments are merged, and only routes
    /// at most <c>maxDetourRatio</c> times longer than the shortest one are kept.
    /// </summary>
    public sealed class RouteBook
    {
        const float SampleStep = 0.25f;     // m, for measuring the length inside each cell
        const float WaypointStep = 4f;      // m, spacing of the points an agent follows
        const float MinSegmentMetres = 1f;  // a segment counts towards the signature above this length

        readonly Dictionary<Cubicle, List<Route>> inbound = new Dictionary<Cubicle, List<Route>>();
        readonly Dictionary<Cubicle, List<Route>> outbound = new Dictionary<Cubicle, List<Route>>();
        int nextId;

        public IReadOnlyList<Route> RoutesTo(Cubicle c) => inbound[c];
        public IReadOnlyList<Route> RoutesFrom(Cubicle c) => outbound[c];
        public IEnumerable<Route> AllRoutes => inbound.Values.SelectMany(l => l).Concat(outbound.Values.SelectMany(l => l));

        public static NavMeshQueryFilter FilterFor(int agentTypeId)
        {
            return new NavMeshQueryFilter { agentTypeID = agentTypeId, areaMask = NavMesh.AllAreas };
        }

        /// <summary>Pedestrian NavMesh (agent type 0); also used for the radar line of sight.</summary>
        public static readonly NavMeshQueryFilter Walking = FilterFor(0);

        public static RouteBook Build(Vector3 wingEntry, IList<Cubicle> cubicles, IList<CorridorNode> nodes,
            IList<CorridorCell> cells, float maxDetourRatio, int maxRoutes, NavMeshQueryFilter filter)
        {
            var book = new RouteBook();
            List<List<CorridorNode>> sequences = EnumerateSequences(nodes, 4);
            sequences.Insert(0, new List<CorridorNode>());   // plain shortest path

            foreach (Cubicle cubicle in cubicles)
            {
                var bySignature = new Dictionary<string, Route>();
                foreach (List<CorridorNode> seq in sequences)
                {
                    var points = new List<Vector3> { Snap(wingEntry, filter) };
                    points.AddRange(seq.Select(n => Snap(n.transform.position, filter)));
                    points.Add(Snap(cubicle.Position, filter));
                    if (!TryPath(points, filter, out List<Vector3> polyline))
                        continue;
                    Route r = book.Measure(polyline, cells, cubicle, true);
                    if (!bySignature.TryGetValue(r.Signature, out Route existing) || r.Length < existing.Length)
                        bySignature[r.Signature] = r;
                }
                List<Route> sorted = bySignature.Values.OrderBy(r => r.Length).ToList();
                if (sorted.Count == 0)
                {
                    Debug.LogWarning("RouteBook: no route to cubicle " + cubicle.Name);
                    book.inbound[cubicle] = new List<Route>();
                    book.outbound[cubicle] = new List<Route>();
                    continue;
                }
                float shortest = sorted[0].Length;
                List<Route> kept = sorted.Where(r => r.Length <= maxDetourRatio * shortest).Take(maxRoutes).ToList();
                book.inbound[cubicle] = kept;
                book.outbound[cubicle] = kept.Select(r => book.Reverse(r)).ToList();
            }
            return book;
        }

        Route Measure(List<Vector3> polyline, IList<CorridorCell> cells, Cubicle cubicle, bool isInbound)
        {
            var cellLength = new float[cells.Count];
            var order = new List<string>();
            float total = 0f;
            float outside = 0f;
            for (int i = 0; i + 1 < polyline.Count; i++)
            {
                float len = Vector3.Distance(polyline[i], polyline[i + 1]);
                total += len;
                int n = Mathf.Max(1, Mathf.CeilToInt(len / SampleStep));
                float piece = len / n;
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = Vector3.Lerp(polyline[i], polyline[i + 1], (k + 0.5f) / n);
                    int hit = -1;
                    for (int c = 0; c < cells.Count; c++)
                        if (cells[c].Contains(p)) { hit = c; break; }
                    if (hit < 0) { outside += piece; continue; }
                    cellLength[hit] += piece;
                }
            }
            // signature: segments in order of first traversal, counted once they exceed MinSegmentMetres
            var segmentLength = new Dictionary<string, float>();
            for (int i = 0; i + 1 < polyline.Count; i++)
            {
                float len = Vector3.Distance(polyline[i], polyline[i + 1]);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / SampleStep));
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = Vector3.Lerp(polyline[i], polyline[i + 1], (k + 0.5f) / n);
                    foreach (CorridorCell cell in cells)
                    {
                        if (!cell.Contains(p)) continue;
                        segmentLength.TryGetValue(cell.SegmentId, out float sl);
                        sl += len / n;
                        segmentLength[cell.SegmentId] = sl;
                        if (sl >= MinSegmentMetres && !order.Contains(cell.SegmentId))
                            order.Add(cell.SegmentId);
                        break;
                    }
                }
            }
            return new Route
            {
                Id = nextId++,
                Cubicle = cubicle,
                Inbound = isInbound,
                Signature = order.Count > 0 ? string.Join(">", order) : "-",
                Waypoints = Resample(polyline, WaypointStep),
                Length = total,
                CellLength = cellLength,
                FreeLength = outside,
            };
        }

        Route Reverse(Route r)
        {
            var reversedPoints = r.Waypoints.Reverse().ToList();
            return new Route
            {
                Id = nextId++,
                Cubicle = r.Cubicle,
                Inbound = false,
                Signature = string.Join(">", r.Signature.Split('>').Reverse()),
                Waypoints = Resample(reversedPoints, WaypointStep),
                Length = r.Length,
                CellLength = (float[])r.CellLength.Clone(),
                FreeLength = r.FreeLength,
            };
        }

        /// <summary>All simple paths in the corridor graph that start at an entry node, up to maxNodes nodes.</summary>
        static List<List<CorridorNode>> EnumerateSequences(IList<CorridorNode> nodes, int maxNodes)
        {
            var result = new List<List<CorridorNode>>();
            foreach (CorridorNode start in nodes.Where(n => n.EntryNode))
                Extend(new List<CorridorNode> { start }, maxNodes, result);
            return result;
        }

        static void Extend(List<CorridorNode> path, int maxNodes, List<List<CorridorNode>> result)
        {
            result.Add(new List<CorridorNode>(path));
            if (path.Count >= maxNodes)
                return;
            foreach (CorridorNode next in path[path.Count - 1].Neighbours)
            {
                if (next == null || path.Contains(next))
                    continue;
                path.Add(next);
                Extend(path, maxNodes, result);
                path.RemoveAt(path.Count - 1);
            }
        }

        public static bool TryPath(List<Vector3> points, NavMeshQueryFilter filter, out List<Vector3> polyline)
        {
            polyline = new List<Vector3>();
            var path = new NavMeshPath();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (!NavMesh.CalculatePath(points[i], points[i + 1], filter, path)
                    || path.status != NavMeshPathStatus.PathComplete)
                    return false;
                Vector3[] corners = path.corners;
                for (int k = i == 0 ? 0 : 1; k < corners.Length; k++)
                    polyline.Add(corners[k]);
            }
            return polyline.Count >= 2;
        }

        public static Vector3[] Resample(IList<Vector3> polyline, float step)
        {
            var result = new List<Vector3> { polyline[0] };
            float carried = 0f;
            for (int i = 0; i + 1 < polyline.Count; i++)
            {
                Vector3 a = polyline[i];
                Vector3 b = polyline[i + 1];
                float len = Vector3.Distance(a, b);
                float s = step - carried;
                while (s < len)
                {
                    result.Add(Vector3.Lerp(a, b, s / len));
                    s += step;
                }
                carried = len - (s - step);
            }
            if (Vector3.Distance(result[result.Count - 1], polyline[polyline.Count - 1]) > 0.05f)
                result.Add(polyline[polyline.Count - 1]);
            return result.ToArray();
        }

        public static Vector3 Snap(Vector3 p)
        {
            return Snap(p, Walking);
        }

        public static Vector3 Snap(Vector3 p, NavMeshQueryFilter filter)
        {
            return NavMesh.SamplePosition(p, out NavMeshHit hit, 3f, filter) ? hit.position : p;
        }
    }
}
