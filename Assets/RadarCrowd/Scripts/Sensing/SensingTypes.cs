using System;

namespace RadarCrowd
{
    /// <summary>What information the router receives (the experimental condition).</summary>
    public enum InformationCondition
    {
        NoSensing,      // C0: static shortest routes
        Oracle,         // C1: true densities and obstacles, no delay
        DopplerOnly,    // C2: radar, moving-target (MTI) channel only
        DopplerStatic,  // C3: radar, MTI channel plus static occupancy channel
        Calibrated,     // C4: as C3, with the density mapped through a calibration curve fitted on separate runs
        OracleWidthOnly, // ablation: true free width, no density information (density taken as zero)
    }

    public enum RadarMount
    {
        End,        // one radar at the upstream end of each cell, looking along the corridor
        Side,       // one radar on a side wall at the middle of each cell, looking across
        BothEnds,   // two radars, one at each end of the cell, looking inwards
    }

    /// <summary>
    /// Radar model parameters. Defaults describe a 60-GHz MIMO people-counting sensor; each value is varied in the
    /// sensitivity analysis.
    /// </summary>
    [Serializable]
    public class RadarParams
    {
        public float fovDeg = 120f;              // azimuth field of view
        public float rangeMax = 10f;             // m
        public float frameDt = 0.1f;             // s between radar frames
        public float reportDt = 1f;              // s between reports to the controller
        public float pd0 = 0.9f;                 // detection probability of an unoccluded target
        public float eta = 0.5f;                 // residual visibility per occluding person
        public float vMin = 0.2f;                // m/s, minimum radial speed passed by MTI (Doppler resolution)
        public float sigmaRange = 0.1f;          // m
        public float sigmaAzDeg = 3f;            // deg
        public float mergeRange = 0.3f;          // m, range resolution of the clustering
        public float mergeAzDeg = 14.3f;         // deg, ~2/N_v rad for N_v = 8 virtual antennas
        public float falseAlarmsPerFrame = 0.1f; // Poisson mean per frame within the sector
        public bool staticChannel = true;        // occupancy against an empty-corridor reference
        public float staticCellSize = 0.5f;      // m
        public float staticPd = 0.9f;            // per-frame detection probability of a static object
        public int staticWindow = 10;            // frames (M of the M-of-N rule)
        public int staticHits = 8;               // frames (N of the M-of-N rule)
        public float staticFalseCellProb = 0.002f; // per cell and report
        public float cellsPerPerson = 1f;        // calibrated in the radar unit scenes (V2)
        public int obstacleMinCells = 4;         // connected static cells (1 m^2) ...
        public float obstacleMinSeconds = 10f;   // ... persisting this long are reported as an obstacle
    }

    /// <summary>One radar report as delivered to the controller.</summary>
    public struct RadarReport
    {
        public int CellIndex;
        public int RadarIndex;
        public float Time;               // s, simulation time of the measurement
        public float MovingCount;        // mean MTI detections per frame inside the cell
        public float StaticPersons;      // people-equivalents from confirmed static cells (outside obstacles)
        public float VisibleArea;        // m^2 of the cell the radar can see
        public float FreeWidth;          // m, estimated widest free gap across the cell
        public bool ObstacleSeen;
        public float DopplerDensity => VisibleArea > 0f ? MovingCount / VisibleArea : 0f;
        public float FusedDensity => VisibleArea > 0f ? (MovingCount + StaticPersons) / VisibleArea : 0f;
    }

    /// <summary>Corridor-cell state as seen by the router under a given condition.</summary>
    public struct CellEstimate
    {
        public float Density;     // persons / m^2
        public float FreeWidth;   // m
        public bool Blocked;
        public float Age;         // s since the underlying measurement
    }
}
