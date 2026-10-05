using System;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>Simulation clock: seconds since the start of the run (advances by exactly dt per frame).</summary>
    public static class SimClock
    {
        static float start;

        public static float Now { get; private set; }

        public static void Reset()
        {
            start = Time.time;
            Now = 0f;
        }

        public static void Update()
        {
            Now = Time.time - start;
        }
    }

    /// <summary>
    /// All factors of one simulation run, loaded from JSON (JsonUtility) passed with -config. Empirical process inputs
    /// (arrival profile, acuity, ambulance, imaging, admission shares, length of visit, boarding) come from the NHAMCS
    /// file at edInputsPath; the values below are either sourced (see comments) or experimental factors.
    /// </summary>
    [Serializable]
    public class RunConfig
    {
        public string runId = "editor";
        public int seed = 1;
        public string condition = "DopplerStatic";     // NoSensing | Oracle | DopplerOnly | DopplerStatic | Calibrated
        public float durationMin = 600f;               // includes the warm-up
        public float warmupMin = 120f;                 // excluded from the metrics
        public float startHour = 6f;                   // clock time at the start of the run
        public float dt = 0.04f;                       // s, fixed simulation step
        public string edInputsPath = "Data/derived/ed_inputs.json";

        // demand: EDBA benchmark 1,350-1,750 annual visits per patient-care space (mean about 1,550)
        public float visitsPerSpacePerYear = 1550f;
        public float surgeMultiplier = 1f;             // arrivals are multiplied by k during the surge window
        public float surgeStartMin = 240f;
        public float surgeDurationMin = 30f;
        public float minStayMin = 15f;                 // floor on the time spent in the cubicle
        public float boardingScale = 1f;               // factor on NHAMCS boarding times (exit block in the crowded mode)
        public bool boardInCorridor = false;           // UK practice: admitted patients wait for a bed on a corridor trolley
        public float triageMinMin = 2f;                // ACEM: the initial triage assessment takes 2-5 min
        public float triageMaxMin = 5f;
        public int triageNurses = 1;
        public float stretcherRadius = 0.6f;           // m; a stretcher or bed needs a free width of 2 x radius

        // companions: 73.1% of ED patients accompanied, mean 1.5 companions per accompanied patient
        public float companionShare = 0.731f;
        public float companionsMeanIfAny = 1.5f;
        public float ambulanceCompanionDelayMin = 30f; // factor: relatives of ambulance patients arrive later
        public float companionExcursionsPerHour = 0.5f;// factor: trips to toilets, cafe, smoking
        public float companionExcursionMin = 10f;      // factor

        // staff: NSW safe staffing 1 nurse per 3 treatment spaces; 2.5 patients per physician-hour (peak arrivals);
        // walking distances checked against pedometer data (nurses 6.5 km, physicians 6.2 km, technicians 9.6 km per 12 h)
        public float patientsPerNurse = 3f;
        public float patientsPerPhysicianHour = 2.5f;
        public int porters = 3;                        // factor
        public float nurseVisitIntervalMin = 30f;      // calibrated to the nurses' walking distance
        public float nurseBedsideMin = 4f;             // factor
        public float physicianInitialMin = 10f;        // factor
        public float physicianReviewMin = 5f;          // factor
        public float imagingMin = 20f;                 // factor: time the patient spends in radiology
        // errands to service rooms (supplies, medication, lab, sluice), calibrated to the pedometer distances
        public float nurseErrandsPerHour = 2f;
        public float physicianErrandsPerHour = 2f;
        public float porterErrandsPerHour = 2f;
        public float errandDwellMin = 1f;

        // workplace violence: 5.5 per 1,000 presentations formally reported; 13 per 1,000 in an audit
        public float incidentsPer1000 = 0f;
        public float incidentRadius = 2.5f;            // factor: distance people keep from an aggressive person
        public float incidentDurationMin = 10f;        // factor: until security has removed the person

        // optional background trips (stress tests only)
        public float walkersPerHour = 0f;
        public float stationaryShare = 0.2f;
        public float dwellSec = 60f;

        // trolleys placed at random along corridor walls (legacy test); waiting in corridors is endogenous
        public int corridorTrolleys = 0;
        public float attendantShare = 0.5f;
        public float trolleyWidth = 0.7f;              // m; ED stretcher, siderails down 0.66-0.78 m (Stryker Prime spec sheet)
        public float trolleyLength = 2.1f;             // m

        // corridor geometry: clear width left by fixed lining along the walls; 0 = as built (3.4-4.8 m)
        public float corridorClearWidth = 0f;          // scenario: 2.44 m = minimum clear width for bed movement (NFPA 101, IBC 1020.2)
        public float makeWaySec = 120f;                // factor: staff push a parked trolley aside so that a stretcher can pass
        public float makeWayPassSec = 60f;             // the trolley stays aside at least this long, longer while a stretcher is near

        // obstacle scenario: O0 none, O1 bed along the wall, O2 corridor closure
        public string obstacleScenario = "O0";
        public string obstacleCell = "A2-2";
        public float obstacleStartMin = 240f;
        public float obstacleEndMin = 300f;

        // random corridor closures (cleaning, spill, repair): one at a time, at a random cell, across the full width
        public float closureGapMin = 0f;               // factor: mean gap between closures (exponential); 0 = none
        public float closureMinMin = 10f;              // factor: closure duration, uniform between min and max (minutes)
        public float closureMaxMin = 30f;

        // sensing
        public string radarMount = "End";              // End | Side | BothEnds
        public float latencySec = 0.5f;
        public float lossProb = 0f;
        public RadarParams radar = new RadarParams();
        public float[] calibrationX = new float[0];    // C4: sensed (fused) density -> true density
        public float[] calibrationY = new float[0];

        // routing
        public float hysteresis = 0.15f;
        public float keepClearDensity = 1f;            // persons/m^2
        public float maxDetourRatio = 1.4f;
        public int maxRoutes = 4;

        // logging
        public float cellLogInterval = 1f;             // s
        public float censusInterval = 60f;             // s
        public float exposureInterval = 0.5f;          // s
        public float exposureRadius = 1f;              // m, neighbourhood for local density
        public float exposureThreshold = 2f;           // persons/m^2

        public InformationCondition Condition => (InformationCondition)Enum.Parse(typeof(InformationCondition), condition);
        public RadarMount Mount => (RadarMount)Enum.Parse(typeof(RadarMount), radarMount);

        /// <summary>Mean arrivals per hour over the day for the given number of treatment spaces.</summary>
        public float MeanArrivalsPerHour(int spaces) => spaces * visitsPerSpacePerYear / 8760f;
    }
}
