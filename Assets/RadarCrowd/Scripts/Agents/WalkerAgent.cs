using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Background pedestrian (staff or visitor) between two corridor end points, not controlled by the router.
    /// A share of walkers stops once on the way for a fixed dwell time, which creates standing people without jams.
    /// </summary>
    public class WalkerAgent : CrowdAgent
    {
        public override AgentKind Kind => AgentKind.Walker;

        public WalkerSpec Spec { get; private set; }
        public float TSpawn { get; private set; }
        public float PathLength { get; private set; }
        public bool StopDone { get; private set; }

        MetricsRecorder metrics;
        Vector3 destination;
        bool stopped;
        float resumeAt;

        public void Init(WalkerSpec spec, Vector3 origin, Vector3 target, MetricsRecorder recorder, float now)
        {
            Spec = spec;
            metrics = recorder;
            destination = target;
            TSpawn = now;
            ConfigureNavAgent(spec.Speed);
            var path = new NavMeshPath();
            PathLength = NavMesh.CalculatePath(origin, target, RouteBook.Walking, path) ? Length(path) : Vector3.Distance(origin, target);
            Nav.SetDestination(destination);
        }

        public void Tick(float now)
        {
            TrackDistance();
            if (Spec.Stops && !StopDone && !stopped && WalkedDistance >= Spec.StopFraction * PathLength)
            {
                stopped = true;
                Nav.isStopped = true;
                Nav.velocity = Vector3.zero;
                resumeAt = now + Spec.DwellSec;
            }
            if (stopped && now >= resumeAt)
            {
                stopped = false;
                StopDone = true;
                Nav.isStopped = false;
            }
            if (!stopped && FlatDistance(transform.position, destination) <= 1f)
            {
                metrics.RecordWalker(this, now);
                Destroy(gameObject);
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float Length(NavMeshPath path)
        {
            float length = 0f;
            Vector3[] c = path.corners;
            for (int i = 0; i + 1 < c.Length; i++)
                length += Vector3.Distance(c[i], c[i + 1]);
            return length;
        }
    }
}
