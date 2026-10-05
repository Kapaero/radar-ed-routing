using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Planar radar observing one corridor cell.
    /// MTI channel: a person yields a detection only if the radial speed exceeds vMin; detections closer than the
    /// range/azimuth resolution merge; false alarms are Poisson. Static channel: occupancy of a 0.5 m grid against the
    /// empty corridor, confirmed by an M-of-N rule; blobs of cells that have been occupied for about obstacleMinSeconds
    /// (exponential average of occupancy) are reported as an obstacle, the remaining static cells as standing people.
    /// </summary>
    public class RadarSensor : MonoBehaviour
    {
        const float LongTermLevel = 0.6f;   // occupancy average above which a cell counts as long-term static

        struct Target
        {
            public CrowdAgent Agent;
            public float Range;
            public float Azimuth;
            public Vector3 Direction;
        }

        public int RadarIndex { get; private set; }
        public CorridorCell Cell { get; private set; }

        RadarParams p;
        SeededRandom rng;
        SensorHub hub;
        Vector3 losOrigin;
        Vector3 boresight;
        float halfFov;
        float nextFrame;
        float nextReport;
        int frames;
        float movingSum;

        // static occupancy grid over the cell
        int nx;
        int nz;
        float cellDx;
        float cellDz;
        Vector3[] centers;
        bool[] visible;
        int[] history;
        bool[] confirmed;
        float[] occupancyAverage;
        bool[] marked;
        int historyMask;
        float averagingRate;
        float visibleArea;

        readonly List<Target> inSector = new List<Target>();
        readonly List<Vector2> detections = new List<Vector2>();   // (range, azimuth deg)
        readonly List<Vector3> clusters = new List<Vector3>();     // (range, azimuth, count)

        public float VisibleArea => visibleArea;

        public void Init(int index, CorridorCell cell, Vector3 mountPosition, Vector3 lookDirection, RadarParams parameters,
            int runSeed, SensorHub sensorHub, float now)
        {
            RadarIndex = index;
            Cell = cell;
            p = parameters;
            hub = sensorHub;
            rng = SeededRandom.For(runSeed, "radar-" + index);
            boresight = new Vector3(lookDirection.x, 0f, lookDirection.z).normalized;
            transform.SetPositionAndRotation(mountPosition, Quaternion.LookRotation(boresight, Vector3.up));
            losOrigin = RouteBook.Snap(mountPosition);
            halfFov = p.fovDeg * 0.5f;
            historyMask = (1 << Mathf.Clamp(p.staticWindow, 1, 30)) - 1;
            averagingRate = Mathf.Clamp01(p.frameDt / Mathf.Max(p.obstacleMinSeconds, p.frameDt));
            BuildGrid();
            nextFrame = now;
            nextReport = now + p.reportDt;
        }

        void BuildGrid()
        {
            nx = Mathf.Max(1, Mathf.RoundToInt(Cell.Width / p.staticCellSize));
            nz = Mathf.Max(1, Mathf.RoundToInt(Cell.Length / p.staticCellSize));
            cellDx = Cell.Width / nx;
            cellDz = Cell.Length / nz;
            int n = nx * nz;
            centers = new Vector3[n];
            visible = new bool[n];
            history = new int[n];
            confirmed = new bool[n];
            occupancyAverage = new float[n];
            marked = new bool[n];
            int count = 0;
            for (int iz = 0; iz < nz; iz++)
            {
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iz * nx + ix;
                    centers[i] = Cell.ToWorld(-Cell.Width * 0.5f + (ix + 0.5f) * cellDx, -Cell.Length * 0.5f + (iz + 0.5f) * cellDz);
                    visible[i] = InSector(centers[i], out _, out _) && LineOfSight(centers[i]);
                    if (visible[i]) count++;
                }
            }
            visibleArea = count * cellDx * cellDz;
        }

        /// <summary>Called by the sensor hub once per simulation step.</summary>
        public void Tick(float now)
        {
            while (now >= nextFrame)
            {
                Frame();
                nextFrame += p.frameDt;
            }
            if (now >= nextReport)
            {
                Report(now);
                nextReport += p.reportDt;
            }
        }

        bool InSector(Vector3 point, out float range, out float azimuth)
        {
            Vector3 d = point - transform.position;
            d.y = 0f;
            range = d.magnitude;
            azimuth = range > 1e-4f ? Vector3.SignedAngle(boresight, d, Vector3.up) : 0f;
            return range >= 0.2f && range <= p.rangeMax && Mathf.Abs(azimuth) <= halfFov;
        }

        bool LineOfSight(Vector3 target)
        {
            if (!NavMesh.SamplePosition(target, out NavMeshHit onMesh, 0.5f, RouteBook.Walking))
                return false;
            return !NavMesh.Raycast(losOrigin, onMesh.position, out _, RouteBook.Walking);
        }

        /// <summary>People between the radar and the target whose body crosses the line of sight (all are in the sector).</summary>
        int Occluders(int targetIndex)
        {
            Target t = inSector[targetIndex];
            int k = 0;
            for (int j = 0; j < inSector.Count; j++)
            {
                if (j == targetIndex)
                    continue;
                Vector3 w = inSector[j].Agent.Position - transform.position;
                w.y = 0f;
                float along = Vector3.Dot(w, t.Direction);
                if (along <= 0.3f || along >= t.Range - 0.3f)
                    continue;
                float r = inSector[j].Agent.Radius;
                if ((w - t.Direction * along).sqrMagnitude < r * r)
                    k++;
            }
            return k;
        }

        void Frame()
        {
            detections.Clear();
            inSector.Clear();
            System.Array.Clear(marked, 0, marked.Length);

            IReadOnlyList<CrowdAgent> agents = AgentRegistry.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                if (!InSector(agents[i].Position, out float range, out float azimuth))
                    continue;
                inSector.Add(new Target
                {
                    Agent = agents[i],
                    Range = range,
                    Azimuth = azimuth,
                    Direction = (Quaternion.AngleAxis(azimuth, Vector3.up) * boresight).normalized,
                });
            }

            for (int i = 0; i < inSector.Count; i++)
            {
                Target t = inSector[i];
                if (!LineOfSight(t.Agent.Position))
                    continue;
                float occlusion = Mathf.Pow(p.eta, Occluders(i));
                float radialSpeed = Vector3.Dot(t.Agent.Velocity, t.Direction);
                if (Mathf.Abs(radialSpeed) >= p.vMin)
                {
                    if (rng.Bernoulli(p.pd0 * occlusion))
                        detections.Add(new Vector2(t.Range + (float)rng.Normal(0, p.sigmaRange), t.Azimuth + (float)rng.Normal(0, p.sigmaAzDeg)));
                }
                else if (p.staticChannel)
                {
                    int g = GridIndex(t.Agent.Position);
                    if (g >= 0 && rng.Bernoulli(p.staticPd * occlusion))
                        marked[g] = true;
                }
            }

            int falseAlarms = rng.Poisson(p.falseAlarmsPerFrame);
            for (int k = 0; k < falseAlarms; k++)
                detections.Add(new Vector2(p.rangeMax * Mathf.Sqrt((float)rng.Uniform()), rng.Range(-halfFov, halfFov)));

            if (p.staticChannel)
                MarkObstacles();

            movingSum += CountClustersInCell();
            frames++;

            if (!p.staticChannel)
                return;
            for (int i = 0; i < history.Length; i++)
            {
                history[i] = ((history[i] << 1) | (marked[i] ? 1 : 0)) & historyMask;
                confirmed[i] = visible[i] && PopCount(history[i]) >= p.staticHits;
                occupancyAverage[i] += ((marked[i] ? 1f : 0f) - occupancyAverage[i]) * averagingRate;
            }
        }

        void MarkObstacles()
        {
            IReadOnlyList<CorridorObstacle> obstacles = AgentRegistry.Obstacles;
            for (int o = 0; o < obstacles.Count; o++)
            {
                CorridorObstacle obstacle = obstacles[o];
                if (!obstacle.isActiveAndEnabled)
                    continue;
                for (int i = 0; i < centers.Length; i++)
                {
                    if (!visible[i] || !obstacle.Contains(centers[i], 0.1f))
                        continue;
                    if (ObstacleSeen(obstacle, centers[i]) && rng.Bernoulli(p.staticPd))
                        marked[i] = true;
                }
            }
        }

        bool ObstacleSeen(CorridorObstacle obstacle, Vector3 point)
        {
            if (!NavMesh.SamplePosition(point, out NavMeshHit onMesh, 1.5f, RouteBook.Walking))
                return false;
            if (!NavMesh.Raycast(losOrigin, onMesh.position, out NavMeshHit hit, RouteBook.Walking))
                return true;
            return obstacle.Contains(hit.position, 0.4f);
        }

        int GridIndex(Vector3 world)
        {
            Vector2 l = Cell.ToLocal(world);
            int ix = Mathf.FloorToInt((l.x + Cell.Width * 0.5f) / cellDx);
            int iz = Mathf.FloorToInt((l.y + Cell.Length * 0.5f) / cellDz);
            if (ix < 0 || ix >= nx || iz < 0 || iz >= nz)
                return -1;
            return iz * nx + ix;
        }

        float CountClustersInCell()
        {
            detections.Sort((a, b) => a.x.CompareTo(b.x));
            clusters.Clear();
            foreach (Vector2 d in detections)
            {
                bool merged = false;
                for (int c = 0; c < clusters.Count; c++)
                {
                    Vector3 cl = clusters[c];
                    if (Mathf.Abs(cl.x - d.x) < p.mergeRange && Mathf.Abs(cl.y - d.y) < p.mergeAzDeg)
                    {
                        float n = cl.z + 1f;
                        clusters[c] = new Vector3((cl.x * cl.z + d.x) / n, (cl.y * cl.z + d.y) / n, n);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    clusters.Add(new Vector3(d.x, d.y, 1f));
            }
            int inside = 0;
            foreach (Vector3 cl in clusters)
            {
                Vector3 world = transform.position + Quaternion.AngleAxis(cl.y, Vector3.up) * boresight * cl.x;
                if (Cell.Contains(world))
                    inside++;
            }
            return inside;
        }

        void Report(float now)
        {
            var report = new RadarReport
            {
                CellIndex = Cell.Index,
                RadarIndex = RadarIndex,
                Time = now,
                MovingCount = frames > 0 ? movingSum / frames : 0f,
                VisibleArea = visibleArea,
                FreeWidth = Cell.Width,
            };
            movingSum = 0f;
            frames = 0;

            if (p.staticChannel)
            {
                // long-term occupied cells stay in the map through single missed frames, so a thin object (a barrier
                // one grid row deep) is not broken into blobs below the obstacle size by one unconfirmed cell
                var occupied = new bool[centers.Length];
                for (int i = 0; i < occupied.Length; i++)
                    occupied[i] = confirmed[i] || occupancyAverage[i] >= LongTermLevel || (visible[i] && rng.Bernoulli(p.staticFalseCellProb));
                bool[] obstacleCells = ClassifyStatic(occupied, out int personCells, out bool obstacle);
                report.StaticPersons = personCells * p.cellsPerPerson;
                report.ObstacleSeen = obstacle;
                // passage width is narrowed only by long-term static objects; standing people step aside
                report.FreeWidth = EstimateFreeWidth(obstacleCells);
            }
            hub.Submit(report);
        }

        /// <summary>
        /// 4-connected components of occupied cells; components with enough long-term static cells are obstacles.
        /// Returns the mask of cells that belong to obstacle components.
        /// </summary>
        bool[] ClassifyStatic(bool[] occupied, out int personCells, out bool obstacle)
        {
            personCells = 0;
            obstacle = false;
            var obstacleCells = new bool[occupied.Length];
            var seen = new bool[occupied.Length];
            var stack = new Stack<int>();
            var component = new List<int>();
            for (int s = 0; s < occupied.Length; s++)
            {
                if (!occupied[s] || seen[s])
                    continue;
                component.Clear();
                stack.Push(s);
                seen[s] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    component.Add(i);
                    int ix = i % nx, iz = i / nx;
                    TryPush(ix - 1, iz, occupied, seen, stack);
                    TryPush(ix + 1, iz, occupied, seen, stack);
                    TryPush(ix, iz - 1, occupied, seen, stack);
                    TryPush(ix, iz + 1, occupied, seen, stack);
                }
                int longTerm = 0;
                foreach (int i in component)
                    if (occupancyAverage[i] >= LongTermLevel) longTerm++;
                if (longTerm >= p.obstacleMinCells)
                {
                    obstacle = true;
                    foreach (int i in component)
                        obstacleCells[i] = true;
                }
                else
                {
                    personCells += component.Count;
                }
            }
            return obstacleCells;
        }

        void TryPush(int ix, int iz, bool[] occupied, bool[] seen, Stack<int> stack)
        {
            if (ix < 0 || ix >= nx || iz < 0 || iz >= nz)
                return;
            int i = iz * nx + ix;
            if (!occupied[i] || seen[i])
                return;
            seen[i] = true;
            stack.Push(i);
        }

        /// <summary>Narrowest row of the cell: widest free run of unoccupied cells across the corridor.</summary>
        float EstimateFreeWidth(bool[] occupied)
        {
            float narrowest = Cell.Width;
            for (int iz = 0; iz < nz; iz++)
            {
                int seenCells = 0;
                for (int ix = 0; ix < nx; ix++)
                    if (visible[iz * nx + ix]) seenCells++;
                if (seenCells < 0.8f * nx)
                    continue;   // the radar sees too little of this row to judge it
                int run = 0, best = 0;
                for (int ix = 0; ix < nx; ix++)
                {
                    if (occupied[iz * nx + ix]) run = 0;
                    else best = Mathf.Max(best, ++run);
                }
                narrowest = Mathf.Min(narrowest, best * cellDx);
            }
            return narrowest;
        }

        static int PopCount(int v)
        {
            int c = 0;
            while (v != 0)
            {
                v &= v - 1;
                c++;
            }
            return c;
        }
    }
}
