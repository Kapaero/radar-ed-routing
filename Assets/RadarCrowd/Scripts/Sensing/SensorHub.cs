using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Creates the radars, delivers their reports to the controller with latency and loss, and answers cell-state
    /// queries according to the information condition. Radars always run, so every run also logs what the sensors
    /// would have reported.
    /// </summary>
    public class SensorHub : MonoBehaviour
    {
        public const float MinPassableWidth = 0.6f;   // m; narrower gaps are treated as closed

        InformationCondition condition;
        float latency;
        float lossProbability;
        SeededRandom rng;
        HospitalLayout layout;

        readonly List<RadarSensor> radars = new List<RadarSensor>();
        readonly List<RadarReport> inFlight = new List<RadarReport>();
        readonly Dictionary<int, RadarReport> latestByRadar = new Dictionary<int, RadarReport>();
        List<int>[] radarsOfCell;

        public int ReportsSent { get; private set; }
        public int ReportsLost { get; private set; }
        public IReadOnlyList<RadarSensor> Radars => radars;
        public InformationCondition Condition => condition;

        /// <summary>Piecewise-linear calibration (C4); beyond the last knot the last segment's slope is extended.</summary>
        public float Calibrate(float sensed)
        {
            if (calibrationX == null || calibrationX.Length < 2)
                return sensed;
            int n = calibrationX.Length;
            int i = 1;
            while (i < n - 1 && sensed > calibrationX[i])
                i++;
            float x0 = calibrationX[i - 1], x1 = calibrationX[i];
            float y0 = calibrationY[i - 1], y1 = calibrationY[i];
            float t = x1 > x0 ? (sensed - x0) / (x1 - x0) : 0f;
            return Mathf.Max(0f, y0 + t * (y1 - y0));
        }

        float[] calibrationX;
        float[] calibrationY;

        public void SetCalibration(float[] x, float[] y)
        {
            if (x != null && y != null && x.Length == y.Length && x.Length >= 2)
            {
                calibrationX = x;
                calibrationY = y;
            }
            else if (condition == InformationCondition.Calibrated)
            {
                Debug.LogWarning("SensorHub: Calibrated condition without a valid calibration curve; using raw estimates.");
            }
        }

        public void Init(HospitalLayout hospitalLayout, InformationCondition informationCondition, RadarParams radarParams,
            RadarMount mount, float latencySeconds, float lossProb, int runSeed, float now)
        {
            layout = hospitalLayout;
            condition = informationCondition;
            latency = latencySeconds;
            lossProbability = lossProb;
            rng = SeededRandom.For(runSeed, "sensor-network");
            radarsOfCell = new List<int>[layout.Cells.Count];
            for (int c = 0; c < layout.Cells.Count; c++)
                radarsOfCell[c] = new List<int>();

            foreach (CorridorCell cell in layout.Cells)
            {
                switch (mount)
                {
                    case RadarMount.End:
                        AddRadar(cell, cell.ToWorld(0f, -cell.Length * 0.5f), cell.transform.forward, radarParams, runSeed, now);
                        break;
                    case RadarMount.Side:
                        AddRadar(cell, cell.ToWorld(-cell.Width * 0.5f + 0.1f, 0f), cell.transform.right, radarParams, runSeed, now);
                        break;
                    case RadarMount.BothEnds:
                        AddRadar(cell, cell.ToWorld(0f, -cell.Length * 0.5f), cell.transform.forward, radarParams, runSeed, now);
                        AddRadar(cell, cell.ToWorld(0f, cell.Length * 0.5f), -cell.transform.forward, radarParams, runSeed, now);
                        break;
                }
            }
        }

        void AddRadar(CorridorCell cell, Vector3 position, Vector3 look, RadarParams radarParams, int runSeed, float now)
        {
            var go = new GameObject("Radar " + cell.CellId + " #" + radars.Count);
            go.transform.SetParent(transform, false);
            var radar = go.AddComponent<RadarSensor>();
            radar.Init(radars.Count, cell, position, look, radarParams, runSeed, this, now);
            radarsOfCell[cell.Index].Add(radar.RadarIndex);
            radars.Add(radar);
        }

        public void Submit(RadarReport report)
        {
            ReportsSent++;
            if (rng.Bernoulli(lossProbability))
            {
                ReportsLost++;
                return;
            }
            inFlight.Add(report);
        }

        /// <summary>Called by the runner once per step: radar frames, then delivery of reports whose latency has elapsed.</summary>
        public void Tick(float now)
        {
            for (int i = 0; i < radars.Count; i++)
                radars[i].Tick(now);
            for (int i = 0; i < inFlight.Count;)
            {
                if (inFlight[i].Time + latency <= now)
                {
                    latestByRadar[inFlight[i].RadarIndex] = inFlight[i];
                    inFlight.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        /// <summary>Latest delivered sensor picture of a cell (radar averages; free width = most pessimistic radar).</summary>
        public bool TryGetSensed(int cellIndex, float now, out float doppler, out float fused, out float freeWidth, out bool obstacle, out float age)
        {
            doppler = fused = 0f;
            freeWidth = layout.Cells[cellIndex].Width;
            obstacle = false;
            age = float.PositiveInfinity;
            int n = 0;
            foreach (int r in radarsOfCell[cellIndex])
            {
                if (!latestByRadar.TryGetValue(r, out RadarReport rep))
                    continue;
                doppler += rep.DopplerDensity;
                fused += rep.FusedDensity;
                freeWidth = Mathf.Min(freeWidth, rep.FreeWidth);
                obstacle |= rep.ObstacleSeen;
                age = Mathf.Min(age, now - rep.Time);
                n++;
            }
            if (n == 0)
                return false;
            doppler /= n;
            fused /= n;
            return true;
        }

        /// <summary>Cell state as the router sees it under the current information condition.</summary>
        public CellEstimate Estimate(int cellIndex, float now)
        {
            CorridorCell cell = layout.Cells[cellIndex];
            switch (condition)
            {
                case InformationCondition.Oracle:
                {
                    float free = cell.TrueFreeWidth();
                    return new CellEstimate { Density = cell.TrueDensity(), FreeWidth = free, Blocked = free < MinPassableWidth, Age = 0f };
                }
                case InformationCondition.OracleWidthOnly:
                {
                    float free = cell.TrueFreeWidth();
                    return new CellEstimate { Density = 0f, FreeWidth = free, Blocked = free < MinPassableWidth, Age = 0f };
                }
                case InformationCondition.DopplerOnly:
                {
                    if (!TryGetSensed(cellIndex, now, out float doppler, out _, out _, out _, out float age))
                        return new CellEstimate { Density = 0f, FreeWidth = cell.Width, Age = float.PositiveInfinity };
                    return new CellEstimate { Density = doppler, FreeWidth = cell.Width, Blocked = false, Age = age };
                }
                case InformationCondition.DopplerStatic:
                case InformationCondition.Calibrated:
                {
                    if (!TryGetSensed(cellIndex, now, out _, out float fused, out float free, out _, out float age))
                        return new CellEstimate { Density = 0f, FreeWidth = cell.Width, Age = float.PositiveInfinity };
                    float density = condition == InformationCondition.Calibrated ? Calibrate(fused) : fused;
                    return new CellEstimate { Density = density, FreeWidth = free, Blocked = free < MinPassableWidth, Age = age };
                }
                default:
                    return new CellEstimate { Density = 0f, FreeWidth = cell.Width, Blocked = false, Age = 0f };
            }
        }
    }
}
