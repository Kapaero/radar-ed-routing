using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>Static object parked in a corridor (bed, cart). It carves the NavMesh and is seen by the static radar channel.</summary>
    [RequireComponent(typeof(NavMeshObstacle))]
    public class CorridorObstacle : MonoBehaviour
    {
        [SerializeField] Vector2 footprint = new Vector2(0.9f, 2.1f);   // m: local x (width), local z (length)

        public Vector2 Footprint => footprint;

        public void Configure(Vector2 size)
        {
            footprint = size;
            var obstacle = GetComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = new Vector3(0f, 0.5f, 0f);
            obstacle.size = new Vector3(size.x, 1f, size.y);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
        }

        public Vector3[] Corners()
        {
            Vector3 r = transform.right * (footprint.x * 0.5f);
            Vector3 f = transform.forward * (footprint.y * 0.5f);
            Vector3 c = transform.position;
            return new[] { c - r - f, c + r - f, c + r + f, c - r + f };
        }

        public bool Contains(Vector3 p, float margin = 0f)
        {
            Vector3 d = p - transform.position;
            return Mathf.Abs(Vector3.Dot(d, transform.right)) <= footprint.x * 0.5f + margin
                && Mathf.Abs(Vector3.Dot(d, transform.forward)) <= footprint.y * 0.5f + margin;
        }

        void OnEnable()
        {
            AgentRegistry.Add(this);
        }

        void OnDisable()
        {
            AgentRegistry.Remove(this);
        }
    }
}
