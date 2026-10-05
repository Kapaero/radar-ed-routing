using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Random corridor closures (cleaning, spill, repair) as an alternating renewal process: an exponential gap with mean
    /// closureGapMin, then a barrier across the full width of a corridor cell drawn uniformly, for a duration drawn
    /// uniformly between closureMinMin and closureMaxMin; then the next gap. One closure at a time, so the wing stays
    /// connected (every cell lies on a loop of corridors). The barrier stands in the gap between two corridor waiting spots
    /// nearest to the cell centre, so it never cuts through a parked trolley. The schedule comes from its own random
    /// stream: every condition of a seed meets the same closures.
    /// </summary>
    public sealed class ClosureSchedule
    {
        public struct Closure
        {
            public float Start;
            public float End;
            public string CellId;
            public float Along;   // m from the cell centre along the corridor
        }

        readonly List<Closure> closures = new List<Closure>();
        int next;
        GameObject barrier;
        Closure current;

        public IReadOnlyList<Closure> Closures => closures;

        public ClosureSchedule(RunConfig cfg, IReadOnlyList<CorridorCell> cells, SeededRandom rng)
        {
            if (cfg.closureGapMin <= 0f || cells.Count == 0)
                return;
            float end = cfg.durationMin * 60f;
            float t = (float)rng.Exponential(cfg.closureGapMin * 60f);
            while (t < end)
            {
                float duration = rng.Range(cfg.closureMinMin, cfg.closureMaxMin) * 60f;
                CorridorCell cell = cells[rng.Index(cells.Count)];
                closures.Add(new Closure { Start = t, End = t + duration, CellId = cell.CellId, Along = BarrierAlong(cell, cfg.trolleyLength) });
                t += duration + (float)rng.Exponential(cfg.closureGapMin * 60f);
            }
        }

        /// <summary>Gap between waiting spots (same pitch as HospitalLayout.BuildSpots) nearest to the cell centre.</summary>
        static float BarrierAlong(CorridorCell cell, float trolleyLength)
        {
            float pitch = trolleyLength + HospitalLayout.SpotGap;
            float half = cell.Length * 0.5f - trolleyLength * 0.5f - 0.5f;
            float limit = cell.Length * 0.5f - ObstacleScenario.ClosureDepth;
            if (half < 0f)
                return 0f;
            float best = 0f, bestDistance = float.MaxValue;
            for (float spot = -half; spot <= half + 1e-3f; spot += pitch)
            {
                foreach (float candidate in new[] { spot - 0.5f * pitch, spot + 0.5f * pitch })
                {
                    if (Mathf.Abs(candidate) > limit || Mathf.Abs(candidate) >= bestDistance)
                        continue;
                    best = candidate;
                    bestDistance = Mathf.Abs(candidate);
                }
            }
            return best;
        }

        public void Tick(float now, System.Func<string, CorridorCell> findCell, MetricsRecorder metrics)
        {
            if (barrier != null && now >= current.End)
            {
                metrics.LogEvent(now, "closure_off", -1, current.CellId, barrier.transform.position);
                Object.Destroy(barrier);
                barrier = null;
            }
            if (barrier == null && next < closures.Count && now >= closures[next].Start)
            {
                current = closures[next++];
                CorridorCell cell = findCell(current.CellId);
                barrier = cell != null ? ObstacleScenario.Spawn("O2", cell, current.Along) : null;
                if (barrier != null)
                    metrics.LogEvent(now, "closure_on", -1, current.CellId, barrier.transform.position);
            }
        }
    }
}
