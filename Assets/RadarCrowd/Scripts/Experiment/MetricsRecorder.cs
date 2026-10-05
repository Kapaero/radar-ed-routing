using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Collects run outputs and writes them as CSV: patients, companions, staff, walkers, decisions, events,
    /// census (where people are, every minute), corridor-cell time series (truth and sensor estimates), schedules and
    /// routes, plus a JSON summary. Times are simulation seconds; the analysis drops the warm-up.
    /// </summary>
    public class MetricsRecorder
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly RunConfig cfg;
        readonly string outDir;
        readonly float warmup;
        HospitalLayout layout;
        SensorHub hub;
        SimContext ctx;

        readonly StringBuilder patients = new StringBuilder(
            "id,index,initial,critical,ambulance,admitted,imaging,lov_min,board_min,companions," +
            "t_arrival,t_triage_start,t_triage_end,t_queued,t_dispatched,t_cubicle,t_first_physician,t_imaging_out,t_imaging_back,t_stay_end,t_departure,t_done," +
            "corridor_s,cubicle,wing,route_in,route_out,reroutes,blocked,walk_to_cubicle_m,walk_imaging_m,walk_out_m,done,boarded_in_corridor\n");
        readonly StringBuilder companions = new StringBuilder("id,patient_index,t_spawn,t_done,excursions,disruptive,walked_m,done\n");
        readonly StringBuilder walkers = new StringBuilder("id,schedule_index,t_spawn,t_done,origin,destination,stops,dwell_s,path_m,walked_m,speed,done\n");
        readonly StringBuilder decisions = new StringBuilder("t,patient,critical,stretcher,point,chosen,chosen_len_m,chosen_eta_s,runner_up,runner_up_eta_s\n");
        readonly StringBuilder events = new StringBuilder("t,event,agent,info,x,z\n");
        readonly StringBuilder trips = new StringBuilder("t_start,t_end,patient,index,critical,stretcher,kind,route,walked_m,blocked,reroutes,make_way\n");
        readonly StringBuilder census = new StringBuilder(
            "t,clock_h,waiting_room,ambulance_bay,corridor,to_cubicle,in_cubicle,boarding,imaging,awaiting_porter,departing," +
            "companions,staff_away_from_base,physician_queue,transport_queue,occupied_cubicles,agents\n");
        readonly StringBuilder cells = new StringBuilder(
            "t,cell,segment,true_n,true_standing,true_density,true_free_w,sensed,doppler_density,fused_density,sensed_free_w,obstacle_seen,report_age\n");

        float nextCellLog;
        float nextCensus;
        float nextExposure;
        double exposureAll;
        readonly Dictionary<AgentKind, double> exposureByKind = new Dictionary<AgentKind, double>();
        int patientsDone;
        int companionsDone;
        int walkersDone;

        public MetricsRecorder(RunConfig config, string outputDir)
        {
            cfg = config;
            outDir = outputDir;
            warmup = config.warmupMin * 60f;
            Directory.CreateDirectory(outDir);
        }

        public void Bind(HospitalLayout hospitalLayout, SensorHub sensorHub)
        {
            layout = hospitalLayout;
            hub = sensorHub;
        }

        public void BindContext(SimContext context)
        {
            ctx = context;
        }

        static string F(float v) => float.IsInfinity(v) || float.IsNaN(v) ? "" : v.ToString("0.###", Inv);

        // ------------------------------------------------------------------ records

        public void RecordPatient(PatientAgent p)
        {
            AppendPatient(p, true);
            patientsDone++;
        }

        void AppendPatient(PatientAgent p, bool done)
        {
            PatientSpec s = p.Spec;
            patients.Append(string.Join(",", p.Id, s.Index, s.Initial ? 1 : 0, s.Critical ? 1 : 0, s.Ambulance ? 1 : 0, s.Admitted ? 1 : 0,
                s.Imaging ? 1 : 0, F(s.LovMin), F(s.BoardMin), s.Companions,
                F(p.TArrival), F(p.TTriageStart), F(p.TTriageEnd), F(p.TQueued), F(p.TDispatched), F(p.TCubicle), F(p.TFirstPhysician), F(p.TImagingOut), F(p.TImagingBack),
                F(p.TStayEnd), F(p.TDeparture), F(p.TDone), F(p.CorridorSecondsAt(SimClock.Now)),
                p.Cubicle != null ? p.Cubicle.Name.Replace(",", " ") : "", p.Cubicle != null ? p.Cubicle.Wing : "",
                p.RouteIn, p.RouteOut, p.Reroutes, p.BlockedEncounters, F(p.WalkToCubicle), F(p.WalkImaging), F(p.WalkOut), done ? 1 : 0,
                p.BoardingInCorridor ? 1 : 0)).Append('\n');
        }

        public void RecordCompanion(CompanionAgent c, float now)
        {
            AppendCompanion(c, now, true);
            companionsDone++;
        }

        void AppendCompanion(CompanionAgent c, float now, bool done)
        {
            companions.Append(string.Join(",", c.Id, c.PatientIndex, F(c.TSpawn), done ? F(now) : "", c.Excursions, c.WasDisruptive ? 1 : 0,
                F(c.WalkedDistance), done ? 1 : 0)).Append('\n');
        }

        public void RecordWalker(WalkerAgent w, float now)
        {
            AppendWalker(w, now, true);
            walkersDone++;
        }

        void AppendWalker(WalkerAgent w, float now, bool done)
        {
            WalkerSpec s = w.Spec;
            walkers.Append(string.Join(",", w.Id, s.Index, F(w.TSpawn), done ? F(now) : "", s.Origin, s.Destination, s.Stops ? 1 : 0,
                F(s.DwellSec), F(w.PathLength), F(w.WalkedDistance), F(s.Speed), done ? 1 : 0)).Append('\n');
        }

        public void LogDecision(float now, PatientAgent p, string point, Route chosen, float chosenEta, Route runnerUp, float runnerUpEta)
        {
            decisions.Append(string.Join(",", F(now), p.Id, p.Critical ? 1 : 0, p.Stretcher ? 1 : 0, point, chosen.Signature, F(chosen.Length),
                F(chosenEta), runnerUp != null ? runnerUp.Signature : "", F(runnerUpEta))).Append('\n');
        }

        /// <summary>One patient trip (inbound, imaging_out, imaging_back, outbound, ward_from_corridor), from start to arrival.</summary>
        public void LogTrip(PatientAgent p, string kind, float start, float end, string route, float walked, int blocked, int reroutes, int makeWay)
        {
            trips.Append(string.Join(",", F(start), F(end), p.Id, p.Spec.Index, p.Critical ? 1 : 0, p.Stretcher ? 1 : 0, kind, route,
                F(walked), blocked, reroutes, makeWay)).Append('\n');
        }

        public void LogEvent(float now, string kind, int agent, string info, Vector3 position)
        {
            events.Append(string.Join(",", F(now), kind, agent, info.Replace(",", " "), F(position.x), F(position.z))).Append('\n');
        }

        // ------------------------------------------------------------------ periodic logging

        public void Tick(float now)
        {
            if (now >= nextCellLog)
            {
                nextCellLog += cfg.cellLogInterval;
                LogCells(now);
            }
            if (now >= nextCensus)
            {
                nextCensus += cfg.censusInterval;
                LogCensus(now);
            }
            if (now >= nextExposure)
            {
                nextExposure += cfg.exposureInterval;
                if (now >= warmup)
                    AccumulateExposure();
            }
        }

        void LogCells(float now)
        {
            foreach (CorridorCell c in layout.Cells)
            {
                int n = c.CountAgents(out int standing);
                bool sensed = hub.TryGetSensed(c.Index, now, out float doppler, out float fused, out float freeWidth, out bool obstacle, out float age);
                cells.Append(string.Join(",", F(now), c.CellId, c.SegmentId, n, standing, F(n / c.Area), F(c.TrueFreeWidth()),
                    sensed ? 1 : 0, F(doppler), F(fused), F(freeWidth), obstacle ? 1 : 0, F(age))).Append('\n');
            }
        }

        void LogCensus(float now)
        {
            int waiting = 0, bay = 0, corridor = 0, toCubicle = 0, inCubicle = 0, boarding = 0, imaging = 0, awaiting = 0, departing = 0;
            int companionCount = 0, staffAway = 0;
            foreach (CrowdAgent a in AgentRegistry.Agents)
            {
                switch (a)
                {
                    case PatientAgent p:
                        if (p.OnCorridorTrolley || p.Phase == PatientPhase.ToCorridorSpot)
                        {
                            corridor++;   // includes boarders on a corridor trolley waiting for the porter
                            break;
                        }
                        switch (p.Phase)
                        {
                            case PatientPhase.ToRegistration:
                            case PatientPhase.WaitingRoom: waiting++; break;
                            case PatientPhase.AtAmbulanceBay: bay++; break;
                            case PatientPhase.ToCorridorSpot:
                            case PatientPhase.CorridorWaiting: corridor++; break;
                            case PatientPhase.ToWing:
                            case PatientPhase.ToCubicle: toCubicle++; break;
                            case PatientPhase.InCubicle: inCubicle++; break;
                            case PatientPhase.Boarding: boarding++; break;
                            case PatientPhase.ToImaging:
                            case PatientPhase.AtImaging:
                            case PatientPhase.FromImaging: imaging++; break;
                            case PatientPhase.AwaitPorter: awaiting++; break;
                            case PatientPhase.Departing: departing++; break;
                        }
                        break;
                    case CompanionAgent _:
                        companionCount++;
                        break;
                    case StaffAgent s:
                        if (!s.Available) staffAway++;
                        break;
                }
            }
            float clock = Mathf.Repeat(cfg.startHour + now / 3600f, 24f);
            census.Append(string.Join(",", F(now), F(clock), waiting, bay, corridor, toCubicle, inCubicle, boarding, imaging, awaiting, departing,
                companionCount, staffAway, ctx.Staff.PhysicianQueue, ctx.Staff.TransportQueue, ctx.Hospital.OccupiedCubicles(),
                AgentRegistry.Agents.Count)).Append('\n');
        }

        /// <summary>Person-seconds spent at local density above the threshold (neighbours within the exposure radius).</summary>
        void AccumulateExposure()
        {
            IReadOnlyList<CrowdAgent> agents = AgentRegistry.Agents;
            float r2 = cfg.exposureRadius * cfg.exposureRadius;
            float disc = Mathf.PI * r2;
            for (int i = 0; i < agents.Count; i++)
            {
                Vector3 p = agents[i].Position;
                if (!InCorridor(p))
                    continue;   // crowding is measured in the corridors only
                int neighbours = 0;
                for (int j = 0; j < agents.Count; j++)
                {
                    Vector3 d = agents[j].Position - p;
                    if (d.x * d.x + d.z * d.z <= r2)
                        neighbours++;
                }
                if (neighbours / disc <= cfg.exposureThreshold)
                    continue;
                exposureAll += cfg.exposureInterval;
                exposureByKind.TryGetValue(agents[i].Kind, out double e);
                exposureByKind[agents[i].Kind] = e + cfg.exposureInterval;
            }
        }

        bool InCorridor(Vector3 p)
        {
            foreach (CorridorCell c in layout.Cells)
                if (c.Contains(p)) return true;
            return false;
        }

        // ------------------------------------------------------------------ files

        public void WriteSchedules(List<PatientSpec> patientSchedule, List<PatientSpec> initial, List<WalkerSpec> walkerSchedule)
        {
            var sb = new StringBuilder("index,t,initial,elapsed_min,critical,ambulance,admitted,imaging,imaging_fraction,lov_min,board_min,companions,companion_delay_min,speed\n");
            foreach (List<PatientSpec> list in new[] { initial, patientSchedule })
                foreach (PatientSpec s in list)
                    sb.Append(string.Join(",", s.Index, F(s.Time), s.Initial ? 1 : 0, F(s.ElapsedMin), s.Critical ? 1 : 0, s.Ambulance ? 1 : 0,
                        s.Admitted ? 1 : 0, s.Imaging ? 1 : 0, F(s.ImagingFraction), F(s.LovMin), F(s.BoardMin), s.Companions,
                        F(s.CompanionDelayMin), F(s.Speed))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "schedule_patients.csv"), sb.ToString());
            sb = new StringBuilder("index,t,origin,destination,stops,stop_fraction,dwell_s,speed\n");
            foreach (WalkerSpec s in walkerSchedule)
                sb.Append(string.Join(",", s.Index, F(s.Time), s.Origin, s.Destination, s.Stops ? 1 : 0, F(s.StopFraction), F(s.DwellSec), F(s.Speed))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "schedule_walkers.csv"), sb.ToString());
        }

        public void WriteCorridorCare(List<CorridorCare.Placement> placements)
        {
            var sb = new StringBuilder("cell,side,along_m,x,z,attendant\n");
            foreach (CorridorCare.Placement t in placements)
                sb.Append(string.Join(",", t.CellId, t.Side, F(t.Along), F(t.Position.x), F(t.Position.z), t.Attendant ? 1 : 0)).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "corridor_care.csv"), sb.ToString());
        }

        public void WriteClosures(IReadOnlyList<ClosureSchedule.Closure> closures)
        {
            var sb = new StringBuilder("start_s,end_s,cell,along_m\n");
            foreach (ClosureSchedule.Closure c in closures)
                sb.Append(string.Join(",", F(c.Start), F(c.End), c.CellId, F(c.Along))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "schedule_closures.csv"), sb.ToString());
        }

        public void WriteRoutes()
        {
            var sb = new StringBuilder("mode,route_id,cubicle,wing,inbound,signature,length_m,free_m");
            foreach (CorridorCell c in layout.Cells)
                sb.Append(',').Append(c.CellId);
            sb.Append('\n');
            var books = new List<(string, RouteBook)> { ("walk", layout.Routes) };
            if (layout.StretcherRoutes != null) books.Add(("stretcher", layout.StretcherRoutes));
            foreach (var (mode, book) in books)
                foreach (Route r in book.AllRoutes)
                {
                    sb.Append(string.Join(",", mode, r.Id, r.Cubicle.Name, r.Cubicle.Wing, r.Inbound ? 1 : 0, r.Signature, F(r.Length), F(r.FreeLength)));
                    foreach (float l in r.CellLength)
                        sb.Append(',').Append(F(l));
                    sb.Append('\n');
                }
            File.WriteAllText(Path.Combine(outDir, "routes.csv"), sb.ToString());
            sb = new StringBuilder("spot,cell,side,x,z\n");
            foreach (CorridorSpot s in layout.Spots)
                sb.Append(string.Join(",", s.Index, s.Cell.CellId, s.Side, F(s.Position.x), F(s.Position.z))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "corridor_spots.csv"), sb.ToString());
        }

        public void Finish(float now, int incidents, int incidentsSkipped)
        {
            int unfinishedPatients = 0;
            var staff = new StringBuilder("id,role,number,tasks,walked_m,km_per_h\n");
            float hours = Mathf.Max(1e-6f, now / 3600f);
            foreach (CrowdAgent a in AgentRegistry.Agents)
            {
                switch (a)
                {
                    case PatientAgent p: AppendPatient(p, false); unfinishedPatients++; break;
                    case CompanionAgent c: AppendCompanion(c, now, false); break;
                    case WalkerAgent w: AppendWalker(w, now, false); break;
                    case StaffAgent s:
                        staff.Append(string.Join(",", s.Id, s.Role, s.Number, s.Tasks, F(s.WalkedDistance), F(s.WalkedDistance / 1000f / hours))).Append('\n');
                        break;
                }
            }
            File.WriteAllText(Path.Combine(outDir, "patients.csv"), patients.ToString());
            File.WriteAllText(Path.Combine(outDir, "companions.csv"), companions.ToString());
            File.WriteAllText(Path.Combine(outDir, "staff.csv"), staff.ToString());
            File.WriteAllText(Path.Combine(outDir, "walkers.csv"), walkers.ToString());
            File.WriteAllText(Path.Combine(outDir, "decisions.csv"), decisions.ToString());
            File.WriteAllText(Path.Combine(outDir, "events.csv"), events.ToString());
            File.WriteAllText(Path.Combine(outDir, "trips.csv"), trips.ToString());
            File.WriteAllText(Path.Combine(outDir, "census.csv"), census.ToString());
            File.WriteAllText(Path.Combine(outDir, "cells.csv"), cells.ToString());
            File.WriteAllText(Path.Combine(outDir, "config.json"), JsonUtility.ToJson(cfg, true));

            float measured = Mathf.Max(1e-6f, (now - warmup) / 3600f);
            var s2 = new StringBuilder("{\n");
            s2.Append("  \"run_id\": \"").Append(cfg.runId).Append("\",\n");
            s2.Append("  \"sim_seconds\": ").Append(F(now)).Append(",\n");
            s2.Append("  \"patients_done\": ").Append(patientsDone).Append(",\n");
            s2.Append("  \"patients_unfinished\": ").Append(unfinishedPatients).Append(",\n");
            s2.Append("  \"companions_done\": ").Append(companionsDone).Append(",\n");
            s2.Append("  \"walkers_done\": ").Append(walkersDone).Append(",\n");
            s2.Append("  \"incidents\": ").Append(incidents).Append(",\n");
            s2.Append("  \"incidents_without_candidate\": ").Append(incidentsSkipped).Append(",\n");
            s2.Append("  \"exposure_person_s_per_h\": ").Append(F((float)(exposureAll / measured))).Append(",\n");
            foreach (var kv in exposureByKind)
                s2.Append("  \"exposure_").Append(kv.Key.ToString().ToLowerInvariant()).Append("_person_s_per_h\": ").Append(F((float)(kv.Value / measured))).Append(",\n");
            s2.Append("  \"radar_reports_sent\": ").Append(hub.ReportsSent).Append(",\n");
            s2.Append("  \"radar_reports_lost\": ").Append(hub.ReportsLost).Append(",\n");
            s2.Append("  \"realtime_seconds\": ").Append(F(Time.realtimeSinceStartup)).Append("\n}\n");
            File.WriteAllText(Path.Combine(outDir, "summary.json"), s2.ToString());
        }
    }
}
