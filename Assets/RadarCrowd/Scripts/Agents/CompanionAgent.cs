using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Relative or friend of a patient. Walks with the patient, waits next to the patient (waiting room, corridor trolley
    /// or cubicle; companions of a patient taken to radiology wait at the cubicle), now and then leaves for the toilets
    /// or the cafe, and leaves with the patient. Companions of ambulance patients arrive later at the main entrance.
    /// A companion can turn aggressive (incident): it then stands still and people keep their distance until security
    /// removes it.
    /// </summary>
    public class CompanionAgent : CrowdAgent
    {
        enum State
        {
            Accompanying,
            Staying,
            Excursion,
            ExcursionDwell,
            Disruptive,
            Leaving
        }

        public override AgentKind Kind => AgentKind.Companion;

        public PatientAgent Patient { get; private set; }
        public int PatientIndex { get; private set; }
        public float TSpawn { get; private set; }
        public int Excursions { get; private set; }
        public bool WasDisruptive { get; private set; }
        public bool CanTurnDisruptive => state == State.Staying && Patient != null;

        SimContext ctx;
        SeededRandom rng;
        State state;
        Vector3 offset;
        float nextRefresh;
        float dwellUntil;
        float disruptiveUntil;
        GameObject zone;

        public void Init(PatientAgent patient, int order, SimContext context, float speed, float now)
        {
            Patient = patient;
            PatientIndex = patient.Spec.Index;
            ctx = context;
            TSpawn = now;
            rng = ctx.StreamFor("companion-" + PatientIndex + "-" + order);
            float angle = rng.Range(0f, 360f);
            float radius = rng.Range(1.2f, 1.8f);
            offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
            ConfigureNavAgent(speed);
            state = State.Accompanying;
        }

        public void Tick(float now)
        {
            TrackDistance();
            if (Patient == null && state != State.Leaving && state != State.Disruptive)
                Leave();

            switch (state)
            {
                case State.Accompanying:
                case State.Staying:
                    Accompany(now);
                    break;

                case State.Excursion:
                    if (Near(ctx.Layout.AmenitiesPos, 3f))
                    {
                        state = State.ExcursionDwell;
                        dwellUntil = now + ctx.Cfg.companionExcursionMin * 60f;
                        Stop();
                    }
                    break;

                case State.ExcursionDwell:
                    if (now >= dwellUntil)
                    {
                        state = State.Accompanying;
                        Resume();
                    }
                    break;

                case State.Disruptive:
                    if (now >= disruptiveUntil)
                    {
                        if (zone != null) Destroy(zone);
                        zone = null;
                        Nav.enabled = true;
                        Nav.Warp(RouteBook.Snap(transform.position));
                        ctx.Metrics.LogEvent(now, "incident_end", Id, "removed by security", transform.position);
                        Leave();
                    }
                    break;

                case State.Leaving:
                    if (Near(ctx.Layout.EntrancePos, 2.5f))
                    {
                        ctx.Metrics.RecordCompanion(this, now);
                        Destroy(gameObject);
                    }
                    break;
            }
        }

        void Accompany(float now)
        {
            if (Patient.Phase == PatientPhase.Done)
            {
                Leave();
                return;
            }
            if (Patient.IsMoving && !Patient.AwayForImaging)
            {
                state = State.Accompanying;
                Resume();
                if (now >= nextRefresh)
                {
                    nextRefresh = now + 0.5f;
                    Nav.stoppingDistance = 1.2f;
                    Nav.SetDestination(Patient.Position);
                }
                return;
            }
            Vector3 anchor = Anchor();
            if (!Near(anchor, 0.6f))
            {
                state = State.Accompanying;
                Resume();
                if (now >= nextRefresh)
                {
                    nextRefresh = now + 1f;
                    Nav.stoppingDistance = 0.2f;
                    Nav.SetDestination(anchor);
                }
                return;
            }
            state = State.Staying;
            Stop();
            bool atCubicle = Patient.AtCubicle || Patient.AwayForImaging;
            if (atCubicle && rng.Bernoulli(ctx.Cfg.companionExcursionsPerHour * ctx.Cfg.dt / 3600f))
            {
                state = State.Excursion;
                Excursions++;
                Resume();
                Nav.SetDestination(ctx.Layout.AmenitiesPos);
            }
        }

        /// <summary>Where to wait: next to the patient's trolley, cubicle or seat.</summary>
        Vector3 Anchor()
        {
            if (Patient.Phase == PatientPhase.CorridorWaiting && Patient.Spot != null)
                return Patient.Spot.StandPosition;
            if (Patient.Cubicle != null && (Patient.AtCubicle || Patient.AwayForImaging))
                return RouteBook.Snap(Patient.Cubicle.Position + offset);
            return RouteBook.Snap(Patient.Position + offset);
        }

        /// <summary>Incident: stands still where it is; a carved zone of the given radius keeps other people away.</summary>
        public void BecomeDisruptive(float now, float durationSec, float radius)
        {
            WasDisruptive = true;
            state = State.Disruptive;
            disruptiveUntil = now + durationSec;
            Nav.ResetPath();
            Nav.enabled = false;
            zone = new GameObject("Incident zone");
            zone.transform.position = transform.position;
            var obstacle = zone.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Capsule;
            obstacle.radius = radius;
            obstacle.height = 2f;
            obstacle.center = new Vector3(0f, 1f, 0f);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
            ctx.Metrics.LogEvent(now, "incident", Id, "radius " + radius, transform.position);
        }

        void Leave()
        {
            state = State.Leaving;
            if (!Nav.enabled)
                return;
            Resume();
            Nav.stoppingDistance = 0.2f;
            Nav.SetDestination(ctx.Layout.EntrancePos);
        }

        void Stop()
        {
            if (Nav.enabled && Nav.isOnNavMesh) Nav.isStopped = true;
        }

        void Resume()
        {
            if (Nav.enabled && Nav.isOnNavMesh) Nav.isStopped = false;
        }

        bool Near(Vector3 target, float radius)
        {
            float dx = transform.position.x - target.x, dz = transform.position.z - target.z;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
