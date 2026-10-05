using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Routing rule shared by all conditions; only the information differs.
    /// The expected travel time of a route is the sum over its corridor cells of length / v(rho_eff), with v from
    /// Weidmann's fundamental diagram and rho_eff = rho * W / W_free; a cell narrower than the passable width closes
    /// the route. Critical patients take the fastest route; normal patients leave the default (shortest) route only if an
    /// alternative is faster by the hysteresis margin or the default exceeds the keep-clear density.
    /// </summary>
    public class Router
    {
        readonly HospitalLayout layout;
        readonly SensorHub hub;
        readonly InformationCondition condition;
        readonly float hysteresis;
        readonly float keepClearDensity;
        readonly MetricsRecorder metrics;
        readonly float stretcherWidth;
        readonly List<(Route route, float time)> scratch = new List<(Route, float)>();

        public Router(HospitalLayout hospitalLayout, SensorHub sensorHub, InformationCondition informationCondition,
            float hysteresisMargin, float keepClear, float stretcherRequiredWidth, MetricsRecorder recorder)
        {
            stretcherWidth = stretcherRequiredWidth;
            layout = hospitalLayout;
            hub = sensorHub;
            condition = informationCondition;
            hysteresis = hysteresisMargin;
            keepClearDensity = keepClear;
            metrics = recorder;
        }

        bool Informed => condition != InformationCondition.NoSensing;

        /// <summary>Free width a patient needs: a walking person or a stretcher transport.</summary>
        public float RequiredWidth(PatientAgent patient) => patient.Stretcher ? stretcherWidth : SensorHub.MinPassableWidth;

        public float EstimateTime(Route route, float now, float requiredWidth)
        {
            if (!Informed)
                return route.Length / Weidmann.FreeSpeed;
            float t = route.FreeLength / Weidmann.FreeSpeed;
            for (int c = 0; c < route.CellLength.Length; c++)
            {
                if (route.CellLength[c] <= 0f)
                    continue;
                CellEstimate e = hub.Estimate(c, now);
                if (e.FreeWidth < requiredWidth)
                    return float.PositiveInfinity;
                float width = layout.Cells[c].Width;
                float effective = e.Density * width / Mathf.Max(e.FreeWidth, SensorHub.MinPassableWidth);
                t += route.CellLength[c] / Weidmann.Speed(effective);
            }
            return t;
        }

        bool ViolatesKeepClear(Route route, float now)
        {
            if (!Informed)
                return false;
            for (int c = 0; c < route.CellLength.Length; c++)
                if (route.UsesCell(c) && hub.Estimate(c, now).Density > keepClearDensity)
                    return true;
            return false;
        }

        /// <summary>
        /// Dispatch from the waiting room. The cubicle is the first free one in the patient's preference order (the same
        /// in every condition), so the router only chooses the route.
        /// </summary>
        public bool ChooseAssignment(PatientAgent patient, float now, out Cubicle cubicle, out Route route)
        {
            cubicle = null;
            route = null;
            RouteBook book = layout.RoutesFor(patient);
            foreach (int index in patient.CubicleOrder)
            {
                Cubicle c = layout.Cubicles[index];
                if (c.IsFree && book.RoutesTo(c).Count > 0)
                {
                    cubicle = c;
                    break;
                }
            }
            if (cubicle == null)
                return false;
            route = Pick(patient, book.RoutesTo(cubicle), null, now, "dispatch");
            return true;
        }

        /// <summary>Re-evaluation when the patient reaches the wing entry, just before the corridor network.</summary>
        public Route ReconsiderInbound(PatientAgent patient, Route current, float now)
        {
            return Pick(patient, layout.RoutesFor(patient).RoutesTo(current.Cubicle), current, now, "wing");
        }

        /// <summary>Route into the cubicle from wherever the patient is (e.g. back from radiology).</summary>
        public Route ChooseInbound(PatientAgent patient, Cubicle cubicle, float now, string point)
        {
            return Pick(patient, layout.RoutesFor(patient).RoutesTo(cubicle), null, now, point);
        }

        public Route ChooseOutbound(PatientAgent patient, Cubicle cubicle, float now)
        {
            return Pick(patient, layout.RoutesFor(patient).RoutesFrom(cubicle), null, now, "exit");
        }

        Route Pick(PatientAgent patient, IReadOnlyList<Route> routes, Route incumbent, float now, string point)
        {
            patient.DetourKnownBlockage = false;
            Route fallback = incumbent ?? routes[0];
            if (!Informed)
            {
                metrics.LogDecision(now, patient, point, fallback, fallback.Length / Weidmann.FreeSpeed, null, 0f);
                return fallback;
            }
            scratch.Clear();
            float required = RequiredWidth(patient);
            foreach (Route r in routes)
                scratch.Add((r, EstimateTime(r, now, required)));

            Route chosen;
            float chosenTime;
            if (patient.Critical)
            {
                (chosen, chosenTime) = Fastest(null);
                if (chosen == null) (chosen, chosenTime) = (fallback, float.PositiveInfinity);
            }
            else
            {
                float defaultTime = TimeOf(fallback);
                bool defaultCrowded = ViolatesKeepClear(fallback, now);
                (Route alt, float altTime) = Fastest(fallback);
                bool switchForTime = alt != null && altTime < (1f - hysteresis) * defaultTime;
                bool switchForKeepClear = alt != null && defaultCrowded && !float.IsInfinity(altTime) && !ViolatesKeepClear(alt, now);
                if (switchForTime || switchForKeepClear)
                    (chosen, chosenTime) = (alt, altTime);
                else
                    (chosen, chosenTime) = (fallback, defaultTime);
            }
            Route runnerUp = null;
            float runnerUpTime = float.PositiveInfinity;
            foreach (var (r, t) in scratch)
                if (r != chosen && t < runnerUpTime) { runnerUp = r; runnerUpTime = t; }
            metrics.LogDecision(now, patient, point, chosen, chosenTime, runnerUp, runnerUpTime);
            // every route of the library is known to be blocked: the patient is sent around the blockage from the start
            patient.DetourKnownBlockage = float.IsInfinity(chosenTime);
            return chosen;
        }

        (Route, float) Fastest(Route exclude)
        {
            Route best = null;
            float bestTime = float.PositiveInfinity;
            foreach (var (r, t) in scratch)
            {
                if (r == exclude || float.IsInfinity(t)) continue;
                if (t < bestTime) { best = r; bestTime = t; }
            }
            return (best, bestTime);
        }

        float TimeOf(Route route)
        {
            foreach (var (r, t) in scratch)
                if (r == route) return t;
            return float.PositiveInfinity;
        }
    }
}
