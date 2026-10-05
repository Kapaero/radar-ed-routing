using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Pedestrian of the corridor validation: a NavMesh agent with exactly the settings of the hospital agents
    /// (CrowdAgent.ConfigureNavAgent), walking from its entry point to a goal beyond the corridor exit.
    /// </summary>
    public class ValidationAgent : CrowdAgent
    {
        public override AgentKind Kind => AgentKind.Walker;

        public int SourceId { get; private set; }      // participant id in the experiment
        public float DesiredSpeed { get; private set; }
        public float TSpawn { get; private set; }
        public float TExit { get; set; } = -1f;         // time of passing the end of the corridor

        public void Init(int sourceId, float speed, Vector3 goal, float now)
        {
            SourceId = sourceId;
            DesiredSpeed = speed;
            TSpawn = now;
            ConfigureNavAgent(speed);
            Nav.SetDestination(goal);
        }

        public void Tick()
        {
            TrackDistance();
        }
    }
}
