using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    public struct PatientSpec
    {
        public int Index;
        public float Time;              // s after the run start (initial patients: 0)
        public bool Critical;           // NHAMCS immediate or emergent
        public bool Ambulance;          // arrives by ambulance on a stretcher
        public bool Admitted;           // admitted or observation: leaves on a bed after boarding
        public bool Imaging;            // has an imaging trip during the stay
        public float ImagingFraction;   // when the trip starts, as a share of the stay
        public float LovMin;            // length of visit drawn from NHAMCS (minutes)
        public float TriageMin;         // triage assessment at the registration desk (walk-in patients)
        public float BoardMin;          // boarding time after the visit ends (admitted only)
        public int Companions;
        public float CompanionDelayMin; // ambulance patients: relatives arrive later
        public float Speed;             // m/s, desired walking speed
        public int[] CubicleOrder;      // random preference order over cubicles; the first free one is assigned
        public bool Initial;            // already in a cubicle at the start of the run (warm start)
        public bool InitialBoarder;     // warm start: admitted patient already waiting for a ward bed
        public float ElapsedMin;        // initial patients: time already spent in the department
    }

    public struct WalkerSpec
    {
        public int Index;
        public float Time;
        public int Origin;
        public int Destination;
        public bool Stops;
        public float StopFraction;
        public float DwellSec;
        public float Speed;
    }

    /// <summary>Arrival schedules drawn before the run from their own random streams (identical across conditions).</summary>
    public static class ScheduleGenerator
    {
        const double MinSpeed = 0.6, MaxSpeed = 2.0;   // m/s, truncation of the walking-speed distribution

        /// <summary>
        /// Non-homogeneous Poisson arrivals (thinning) with the NHAMCS hourly profile, scaled to the mean rate, plus the
        /// surge factor; patient attributes are drawn from the NHAMCS shares and quantile tables.
        /// </summary>
        public static List<PatientSpec> Patients(RunConfig c, EdInputs ed, SeededRandom rng, int cubicleCount)
        {
            float meanRate = c.MeanArrivalsPerHour(cubicleCount) / 3600f;
            float maxRate = meanRate * ed.MaxRelative() * Mathf.Max(1f, c.surgeMultiplier);
            float horizon = c.durationMin * 60f;
            var result = new List<PatientSpec>();
            double t = 0.0;
            while (true)
            {
                t += rng.Exponential(1.0 / maxRate);
                if (t > horizon)
                    break;
                float clock = c.startHour + (float)t / 3600f;
                float rate = meanRate * ed.ArrivalRelative(clock) * (InSurge(c, (float)t) ? c.surgeMultiplier : 1f);
                if (rng.Uniform() * maxRate > rate)
                    continue;
                result.Add(Draw(c, ed, rng, cubicleCount, result.Count, (float)t, false, 0f));
            }
            return result;
        }

        /// <summary>
        /// Warm start: patients already in the department at t = 0. Their number follows Little's law (arrival rate of
        /// the preceding hours times the mean length of visit); visit lengths are length-biased and the elapsed time is a
        /// uniform share of it, as for a stationary process.
        /// </summary>
        public static List<PatientSpec> InitialPatients(RunConfig c, EdInputs ed, SeededRandom rng, int cubicleCount, int firstIndex)
        {
            float meanRate = c.MeanArrivalsPerHour(cubicleCount);
            float recentRate = meanRate * (ed.ArrivalRelative(c.startHour - 1f) + ed.ArrivalRelative(c.startHour - 3f)) * 0.5f;
            float meanLovHours = (ed.Mean(ed.lovOtherDischarged) * 0.8f + ed.Mean(ed.lovOtherAdmitted) * 0.2f) / 60f;
            int n = Mathf.Min(cubicleCount, Mathf.RoundToInt(recentRate * meanLovHours));
            var result = new List<PatientSpec>();
            while (result.Count < n)
            {
                PatientSpec s = Draw(c, ed, rng, cubicleCount, firstIndex + result.Count, 0f, true, 0f);
                // length-biased acceptance: keep a visit with probability proportional to its length
                if (rng.Uniform() * 1440.0 > s.LovMin)
                    continue;
                s.ElapsedMin = (float)rng.Uniform() * s.LovMin;
                result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Warm start for exit block: admitted patients already waiting for a ward bed at t = 0 (Little law on the
        /// scaled boarding time); the time already spent boarding is a uniform share of a length-biased draw.
        /// </summary>
        public static List<PatientSpec> InitialBoarders(RunConfig c, EdInputs ed, SeededRandom rng, int cubicleCount, int firstIndex)
        {
            float meanRate = c.MeanArrivalsPerHour(cubicleCount);
            float admitShare = ed.criticalShare * ed.admitShareCritical + (1f - ed.criticalShare) * ed.admitShareOther;
            float meanBoardHours = ed.Mean(ed.boarded) * c.boardingScale / 60f;
            int n = Mathf.RoundToInt(meanRate * admitShare * meanBoardHours);
            float maxBoard = ed.boarded[ed.boarded.Length - 1];
            var result = new List<PatientSpec>();
            int guard = 0;
            while (result.Count < n && guard++ < 100000)
            {
                PatientSpec s = Draw(c, ed, rng, cubicleCount, firstIndex + result.Count, 0f, true, 0f);
                if (!s.Admitted || s.BoardMin <= 0f || rng.Uniform() * maxBoard > s.BoardMin)
                    continue;
                s.InitialBoarder = true;
                s.ElapsedMin = (float)rng.Uniform() * s.BoardMin * c.boardingScale;
                result.Add(s);
            }
            return result;
        }

        static PatientSpec Draw(RunConfig c, EdInputs ed, SeededRandom rng, int cubicleCount, int index, float time, bool initial, float elapsed)
        {
            bool critical = rng.Bernoulli(ed.criticalShare);
            bool ambulance = rng.Bernoulli(critical ? ed.ambulanceShareCritical : ed.ambulanceShareOther);
            bool admitted = rng.Bernoulli(critical ? ed.admitShareCritical : ed.admitShareOther);
            bool imaging = rng.Bernoulli(critical ? ed.imagingShareCritical : ed.imagingShareOther);
            float lov = ed.Sample(ed.LovTable(critical, admitted), rng);
            float board = admitted ? ed.Sample(ed.boarded, rng) : 0f;
            int companions = 0;
            if (rng.Bernoulli(c.companionShare))
                companions = 1 + rng.Poisson(Mathf.Max(0f, c.companionsMeanIfAny - 1f));
            return new PatientSpec
            {
                Index = index,
                Time = time,
                Critical = critical,
                Ambulance = ambulance,
                Admitted = admitted,
                Imaging = imaging,
                ImagingFraction = rng.Range(0.15f, 0.5f),
                LovMin = lov,
                TriageMin = rng.Range(c.triageMinMin, c.triageMaxMin),
                BoardMin = board,
                Companions = companions,
                CompanionDelayMin = ambulance ? c.ambulanceCompanionDelayMin * rng.Range(0.5f, 1.5f) : 0f,
                Speed = (float)rng.TruncatedNormal(Weidmann.FreeSpeed, Weidmann.FreeSpeedSd, MinSpeed, MaxSpeed),
                CubicleOrder = Permutation(cubicleCount, rng),
                Initial = initial,
                ElapsedMin = elapsed,
            };
        }

        static bool InSurge(RunConfig c, float t)
        {
            float s0 = c.surgeStartMin * 60f;
            return c.surgeMultiplier > 1f && t >= s0 && t <= s0 + c.surgeDurationMin * 60f;
        }

        public static List<WalkerSpec> Walkers(RunConfig c, int endpointCount, SeededRandom rng)
        {
            var result = new List<WalkerSpec>();
            if (c.walkersPerHour <= 0f || endpointCount < 2)
                return result;
            double t = 0.0;
            double rate = c.walkersPerHour / 3600.0;
            while (true)
            {
                t += rng.Exponential(1.0 / rate);
                if (t > c.durationMin * 60f)
                    break;
                int origin = rng.Index(endpointCount);
                int destination = rng.Index(endpointCount - 1);
                if (destination >= origin)
                    destination++;
                result.Add(new WalkerSpec
                {
                    Index = result.Count,
                    Time = (float)t,
                    Origin = origin,
                    Destination = destination,
                    Stops = rng.Bernoulli(c.stationaryShare),
                    StopFraction = rng.Range(0.2f, 0.8f),
                    DwellSec = c.dwellSec,
                    Speed = (float)rng.TruncatedNormal(Weidmann.FreeSpeed, Weidmann.FreeSpeedSd, MinSpeed, MaxSpeed),
                });
            }
            return result;
        }

        static int[] Permutation(int n, SeededRandom rng)
        {
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Index(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order;
        }
    }
}
