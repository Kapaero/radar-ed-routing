using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    public enum AgentKind
    {
        Patient,
        Walker,
        Staff,
        Companion
    }

    /// <summary>
    /// Base class of every simulated pedestrian. Agents register themselves so that sensors and metrics enumerate
    /// them in a deterministic order without physics queries.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public abstract class CrowdAgent : MonoBehaviour
    {
        public const float BodyRadius = 0.25f;     // m, half shoulder width; NavMeshAgent radius of a pedestrian
        public const float StandingSpeed = 0.1f;   // m/s, below this an agent counts as standing (ground truth)

        static int nextId;

        protected NavMeshAgent Nav { get; private set; }
        public int Id { get; private set; }
        public abstract AgentKind Kind { get; }
        public Vector3 Position => transform.position;
        public Vector3 Velocity => Nav != null && Nav.enabled && Nav.isOnNavMesh ? Nav.velocity : Vector3.zero;
        public bool IsStanding => Velocity.sqrMagnitude < StandingSpeed * StandingSpeed;

        /// <summary>Radius of the body as seen by the radar and the occupancy grid (larger for a stretcher).</summary>
        public float Radius { get; private set; } = BodyRadius;

        /// <summary>Distance actually walked, accumulated in Tick.</summary>
        public float WalkedDistance { get; private set; }

        Vector3 lastPosition;

        public static void ResetIds()
        {
            nextId = 0;
        }

        protected virtual void Awake()
        {
            Nav = GetComponent<NavMeshAgent>();
            Id = ++nextId;
            lastPosition = transform.position;
        }

        protected virtual void OnEnable()
        {
            AgentRegistry.Add(this);
        }

        protected virtual void OnDisable()
        {
            AgentRegistry.Remove(this);
        }

        /// <summary>Accumulates the walked distance; called from the agent's Tick (driven by the runner).</summary>
        protected void TrackDistance()
        {
            Vector3 p = transform.position;
            WalkedDistance += Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(lastPosition.x, 0f, lastPosition.z));
            lastPosition = p;
        }

        /// <summary>Pedestrian-like agent settings: relaxation time of about 0.5 s gives an acceleration near 3 m/s^2.</summary>
        protected void ConfigureNavAgent(float desiredSpeed, int agentTypeId = 0, float radius = BodyRadius)
        {
            Radius = radius;
            if (Nav.agentTypeID != agentTypeId)
            {
                Nav.agentTypeID = agentTypeId;
                // re-attach to the NavMesh of the new agent type at the nearest valid point
                Nav.Warp(RouteBook.Snap(transform.position, RouteBook.FilterFor(agentTypeId)));
            }
            Nav.radius = radius;
            Nav.height = 1.75f;
            Nav.speed = desiredSpeed;
            Nav.acceleration = 3f;
            Nav.angularSpeed = 540f;
            Nav.stoppingDistance = 0.2f;
            Nav.autoBraking = true;
            Nav.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            Nav.avoidancePriority = 50;
        }
    }
}
