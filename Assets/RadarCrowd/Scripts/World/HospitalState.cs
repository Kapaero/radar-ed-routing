using System.Collections.Generic;

namespace RadarCrowd
{
    /// <summary>
    /// Waiting patients, cubicle occupancy and corridor waiting spots. Waiting patients get a cubicle as soon as one is
    /// free: critical first, then in order of arrival; the router picks cubicle (by the patient's preference order) and
    /// route. Ambulance patients who cannot be placed wait on a trolley at the corridor spot nearest to the wing entry.
    /// </summary>
    public class HospitalState
    {
        readonly List<PatientAgent> queue = new List<PatientAgent>();
        readonly List<PatientAgent> triageQueue = new List<PatientAgent>();
        readonly List<(PatientAgent patient, float start, float end)> inTriage = new List<(PatientAgent, float, float)>();
        int triageDesks = 1;
        readonly Router router;
        readonly HospitalLayout layout;
        StaffDispatcher staff;

        public HospitalState(HospitalLayout hospitalLayout, Router patientRouter)
        {
            layout = hospitalLayout;
            router = patientRouter;
        }

        public void BindStaff(StaffDispatcher dispatcher)
        {
            staff = dispatcher;
        }

        public void SetTriageDesks(int desks)
        {
            triageDesks = System.Math.Max(1, desks);
        }

        public int TriageQueueLength => triageQueue.Count;

        /// <summary>Walk-in patient at the registration desk: triage in order of arrival.</summary>
        public void EnqueueTriage(PatientAgent patient)
        {
            triageQueue.Add(patient);
        }

        void TickTriage(float now)
        {
            for (int i = inTriage.Count - 1; i >= 0; i--)
            {
                if (inTriage[i].end > now) continue;
                var (patient, start, end) = inTriage[i];
                inTriage.RemoveAt(i);
                if (patient != null)
                    patient.OnTriaged(start, end);
            }
            triageQueue.RemoveAll(p => p == null);
            while (inTriage.Count < triageDesks && triageQueue.Count > 0)
            {
                PatientAgent next = triageQueue[0];
                triageQueue.RemoveAt(0);
                inTriage.Add((next, now, now + next.Spec.TriageMin * 60f));
            }
        }

        public int QueueLength => queue.Count;

        public void Enqueue(PatientAgent patient)
        {
            queue.Add(patient);
        }

        public int OccupiedCubicles()
        {
            int n = 0;
            foreach (Cubicle c in layout.Cubicles)
                if (!c.IsFree) n++;
            return n;
        }

        public int OccupiedSpots()
        {
            int n = 0;
            foreach (CorridorSpot s in layout.Spots)
                if (s.Occupant != null) n++;
            return n;
        }

        public void Tick(float now)
        {
            TickTriage(now);
            while (queue.Count > 0)
            {
                PatientAgent next = Next();
                if (!router.ChooseAssignment(next, now, out Cubicle cubicle, out Route route))
                    break;   // no free cubicle
                queue.Remove(next);
                cubicle.Occupant = next;
                next.BeginTransfer(cubicle, route, now);
            }
            foreach (PatientAgent p in queue)
            {
                if (p.Phase != PatientPhase.AtAmbulanceBay)
                    continue;
                CorridorSpot spot = FreeSpot();
                if (spot == null)
                    break;
                spot.Occupant = p;
                p.GoToCorridorSpot(spot, now);
            }
        }

        PatientAgent Next()
        {
            PatientAgent best = null;
            foreach (PatientAgent p in queue)
            {
                if (best == null
                    || (p.Critical && !best.Critical)
                    || (p.Critical == best.Critical && p.QueuedSince < best.QueuedSince))
                    best = p;
            }
            return best;
        }

        /// <summary>Reserves the free corridor spot nearest to the wing entry, or returns null.</summary>
        public CorridorSpot ClaimSpot(PatientAgent patient)
        {
            CorridorSpot spot = FreeSpot();
            if (spot != null)
                spot.Occupant = patient;
            return spot;
        }

        CorridorSpot FreeSpot()
        {
            foreach (CorridorSpot s in layout.Spots)
                if (s.Occupant == null) return s;
            return null;
        }

        /// <summary>The patient has left: the cubicle must be cleaned before the next patient.</summary>
        public void Release(Cubicle cubicle)
        {
            if (cubicle == null || cubicle.Occupant == null)
                return;
            cubicle.Occupant = null;
            if (staff != null && staff.HasCleaners)
            {
                cubicle.Dirty = true;
                staff.RequestCleaning(cubicle, SimClock.Now);
            }
        }

        public void ReleaseSpot(CorridorSpot spot)
        {
            if (spot != null)
                spot.Occupant = null;
        }
    }
}
