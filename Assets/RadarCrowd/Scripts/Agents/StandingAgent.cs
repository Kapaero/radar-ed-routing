using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Staff member or relative standing next to a corridor trolley for the whole run. A standing person is invisible
    /// to the MTI channel and appears only in the static occupancy channel.
    /// </summary>
    public class StandingAgent : CrowdAgent
    {
        public override AgentKind Kind => AgentKind.Staff;

        public static StandingAgent Create(Vector3 position, Transform parent)
        {
            var go = new GameObject("Attendant");
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.AddComponent<NavMeshAgent>();
            var agent = go.AddComponent<StandingAgent>();
            agent.ConfigureNavAgent(0f);
            if (agent.Nav.isOnNavMesh)
                agent.Nav.isStopped = true;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.875f, 0f);
            body.transform.localScale = new Vector3(2f * BodyRadius, 0.875f, 2f * BodyRadius);
            return agent;
        }
    }
}
