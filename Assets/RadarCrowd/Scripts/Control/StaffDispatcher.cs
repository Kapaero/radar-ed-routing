using System.Collections.Generic;
using UnityEngine;

namespace RadarCrowd
{
    public enum StaffTaskKind
    {
        InitialAssessment,
        Review,
        NurseVisit,
        Transport,
        Errand
    }

    public sealed class StaffTask
    {
        public StaffTaskKind Kind;
        public PatientAgent Patient;
        public TransportKind Transport;
        public float Requested;
        public float DurationSec;
    }

    /// <summary>
    /// Assigns staff to work. Physicians: initial assessment of each newly placed patient (critical first, then in
    /// order of request) and one review at the middle of the remaining stay. Nurses: each nurse looks after a fixed group
    /// of neighbouring cubicles (NSW ratio 1:3) and visits each patient again once the visit interval has passed.
    /// Porters: transports in order of request.
    /// </summary>
    public class StaffDispatcher
    {
        readonly SimContext ctx;
        readonly List<StaffAgent> physicians = new List<StaffAgent>();
        readonly List<StaffAgent> nurses = new List<StaffAgent>();
        readonly List<StaffAgent> porters = new List<StaffAgent>();
        readonly List<StaffTask> physicianQueue = new List<StaffTask>();
        readonly List<StaffTask> transportQueue = new List<StaffTask>();
        readonly List<StaffTask> scheduledReviews = new List<StaffTask>();
        readonly Dictionary<StaffAgent, List<Cubicle>> nurseCubicles = new Dictionary<StaffAgent, List<Cubicle>>();
        readonly Dictionary<PatientAgent, float> lastNurseVisit = new Dictionary<PatientAgent, float>();
        readonly HashSet<PatientAgent> nurseBusyWith = new HashSet<PatientAgent>();
        readonly Dictionary<StaffAgent, SeededRandom> errandStreams = new Dictionary<StaffAgent, SeededRandom>();

        public IReadOnlyList<StaffAgent> Physicians => physicians;
        public IReadOnlyList<StaffAgent> Nurses => nurses;
        public IReadOnlyList<StaffAgent> Porters => porters;
        public int PhysicianQueue => physicianQueue.Count;
        public int TransportQueue => transportQueue.Count;

        public StaffDispatcher(SimContext context)
        {
            ctx = context;
        }

        public void Add(StaffAgent staff)
        {
            errandStreams[staff] = ctx.StreamFor("errands-" + staff.Role + "-" + staff.Number);
            switch (staff.Role)
            {
                case StaffRole.Physician: physicians.Add(staff); break;
                case StaffRole.Nurse: nurses.Add(staff); break;
                case StaffRole.Porter: porters.Add(staff); break;
            }
        }

        /// <summary>Splits the cubicles into contiguous groups (ordered along the wing) and gives one group to each nurse.</summary>
        public void AssignNurseCubicles(IReadOnlyList<Cubicle> cubicles)
        {
            var ordered = new List<Cubicle>(cubicles);
            ordered.Sort((a, b) =>
            {
                int w = string.CompareOrdinal(a.Wing, b.Wing);
                return w != 0 ? w : b.Position.z.CompareTo(a.Position.z);
            });
            for (int i = 0; i < ordered.Count; i++)
            {
                StaffAgent nurse = nurses[Mathf.Min(nurses.Count - 1, i * nurses.Count / ordered.Count)];
                if (!nurseCubicles.TryGetValue(nurse, out List<Cubicle> list))
                    nurseCubicles[nurse] = list = new List<Cubicle>();
                list.Add(ordered[i]);
            }
        }

        public void RequestInitial(PatientAgent patient, float now)
        {
            physicianQueue.Add(new StaffTask { Kind = StaffTaskKind.InitialAssessment, Patient = patient, Requested = now,
                DurationSec = ctx.Cfg.physicianInitialMin * 60f });
            lastNurseVisit[patient] = float.NegativeInfinity;
        }

        public void RequestTransport(PatientAgent patient, TransportKind kind, float now)
        {
            transportQueue.Add(new StaffTask { Kind = StaffTaskKind.Transport, Patient = patient, Transport = kind, Requested = now });
        }

        public void TransportDone(StaffAgent porter, float now)
        {
            porter.EndEscort();
        }

        public void BedsideDone(StaffAgent staff, StaffTask task, float now)
        {
            if (task.Patient == null)
                return;
            if (task.Kind == StaffTaskKind.InitialAssessment)
            {
                float reviewAt = now + 0.5f * Mathf.Max(0f, task.Patient.StayEnd - now);
                scheduledReviews.Add(new StaffTask { Kind = StaffTaskKind.Review, Patient = task.Patient, Requested = reviewAt,
                    DurationSec = ctx.Cfg.physicianReviewMin * 60f });
            }
            else if (task.Kind == StaffTaskKind.NurseVisit)
            {
                lastNurseVisit[task.Patient] = now;
                nurseBusyWith.Remove(task.Patient);
            }
        }

        // Cleaners are not modelled yet: cubicles become free immediately (to be replaced by cleaning tasks).
        public bool HasCleaners => false;

        public void RequestCleaning(Cubicle cubicle, float now)
        {
            cubicle.Dirty = false;
        }

        public void Requeue(StaffAgent staff, StaffTask task)
        {
            if (task.Kind == StaffTaskKind.NurseVisit)
                nurseBusyWith.Remove(task.Patient);   // the nurse comes back at the next round
            else
                physicianQueue.Add(task);
        }

        public void Abandon(StaffAgent staff, StaffTask task)
        {
            if (task != null && task.Kind == StaffTaskKind.NurseVisit && task.Patient != null)
                nurseBusyWith.Remove(task.Patient);
        }

        public void Tick(float now)
        {
            for (int i = scheduledReviews.Count - 1; i >= 0; i--)
            {
                if (scheduledReviews[i].Requested > now) continue;
                physicianQueue.Add(scheduledReviews[i]);
                scheduledReviews.RemoveAt(i);
            }
            physicianQueue.RemoveAll(t => t.Patient == null);
            transportQueue.RemoveAll(t => t.Patient == null);

            foreach (StaffAgent p in physicians)
            {
                if (!p.Available || physicianQueue.Count == 0) continue;
                StaffTask next = NextPhysicianTask();
                if (next == null) break;
                physicianQueue.Remove(next);
                p.Assign(next, Bedside(next.Patient, p.Id));
            }

            foreach (StaffAgent n in nurses)
            {
                if (!n.Available || !nurseCubicles.TryGetValue(n, out List<Cubicle> cubicles)) continue;
                PatientAgent due = null;
                float oldest = float.PositiveInfinity;
                foreach (Cubicle c in cubicles)
                {
                    PatientAgent p = c.Occupant;
                    if (p == null || !p.AtCubicle || nurseBusyWith.Contains(p)) continue;
                    if (!lastNurseVisit.TryGetValue(p, out float last)) last = float.NegativeInfinity;
                    if (now - last < ctx.Cfg.nurseVisitIntervalMin * 60f) continue;
                    if (last < oldest) { oldest = last; due = p; }
                }
                if (due == null) continue;
                nurseBusyWith.Add(due);
                n.Assign(new StaffTask { Kind = StaffTaskKind.NurseVisit, Patient = due, Requested = now,
                    DurationSec = ctx.Cfg.nurseBedsideMin * 60f }, Bedside(due, n.Id));
            }

            foreach (StaffAgent porter in porters)
            {
                if (!porter.Available || transportQueue.Count == 0) continue;
                StaffTask next = transportQueue[0];
                transportQueue.RemoveAt(0);
                porter.Assign(next, next.Patient.Position);
            }

            AssignErrands(nurses, ctx.Cfg.nurseErrandsPerHour, now);
            AssignErrands(physicians, ctx.Cfg.physicianErrandsPerHour, now);
            AssignErrands(porters, ctx.Cfg.porterErrandsPerHour, now);
        }

        /// <summary>
        /// Work away from the patients (supplies, medication, lab, sluice) as trips to service points along the
        /// corridors, started by available staff at a per-role rate calibrated to the pedometer distances.
        /// </summary>
        void AssignErrands(List<StaffAgent> staff, float perHour, float now)
        {
            if (perHour <= 0f || ctx.Layout.WalkerEndpoints.Count == 0)
                return;
            double p = perHour * ctx.Cfg.dt / 3600.0;
            foreach (StaffAgent s in staff)
            {
                SeededRandom rng = errandStreams[s];
                if (!s.Available || !rng.Bernoulli(p))
                    continue;
                Vector3 target = ctx.Layout.WalkerEndpoints[rng.Index(ctx.Layout.WalkerEndpoints.Count)];
                s.Assign(new StaffTask { Kind = StaffTaskKind.Errand, Requested = now, DurationSec = ctx.Cfg.errandDwellMin * 60f }, target);
            }
        }

        StaffTask NextPhysicianTask()
        {
            StaffTask best = null;
            foreach (StaffTask t in physicianQueue)
            {
                if (t.Patient == null || !t.Patient.AtCubicle) continue;   // seen when back in the cubicle
                if (best == null || Rank(t) < Rank(best) || (Rank(t) == Rank(best) && t.Requested < best.Requested))
                    best = t;
            }
            return best;
        }

        static int Rank(StaffTask t)
        {
            if (t.Kind == StaffTaskKind.InitialAssessment) return t.Patient.Critical ? 0 : 1;
            return 2;
        }

        /// <summary>A bedside position next to the patient's cubicle, slightly different for each staff member.</summary>
        static Vector3 Bedside(PatientAgent patient, int salt)
        {
            Vector3 c = patient.Cubicle != null ? patient.Cubicle.Position : patient.Position;
            float angle = (salt * 137.5f) % 360f;
            return RouteBook.Snap(c + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * 0.9f);
        }
    }
}
