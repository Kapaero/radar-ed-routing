using System.Collections.Generic;

namespace RadarCrowd
{
    /// <summary>
    /// Workplace-violence incidents as a Poisson process with a rate per 1,000 presentations. At each incident a
    /// waiting companion turns aggressive where it stands; people keep their distance until security removes it.
    /// </summary>
    public class IncidentManager
    {
        readonly SimContext ctx;
        readonly SeededRandom rng;
        readonly double ratePerSecond;
        double next = double.PositiveInfinity;

        public int Count { get; private set; }
        public int Skipped { get; private set; }

        public IncidentManager(SimContext context, float meanArrivalsPerHour)
        {
            ctx = context;
            rng = ctx.StreamFor("incidents");
            ratePerSecond = ctx.Cfg.incidentsPer1000 / 1000.0 * meanArrivalsPerHour / 3600.0;
            if (ratePerSecond > 0.0)
                next = rng.Exponential(1.0 / ratePerSecond);
        }

        public void Tick(float now, IReadOnlyList<CrowdAgent> agents)
        {
            if (now < next)
                return;
            next = now + rng.Exponential(1.0 / ratePerSecond);
            var candidates = new List<CompanionAgent>();
            foreach (CrowdAgent a in agents)
                if (a is CompanionAgent c && c.CanTurnDisruptive)
                    candidates.Add(c);
            if (candidates.Count == 0)
            {
                Skipped++;
                return;
            }
            candidates[rng.Index(candidates.Count)].BecomeDisruptive(now, ctx.Cfg.incidentDurationMin * 60f, ctx.Cfg.incidentRadius);
            Count++;
        }
    }
}
