using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// Deterministic obstacle scenarios: O0 none; O1 a hospital bed parked along the wall (narrows the corridor);
    /// O2 the corridor closed across its full width (e.g. cleaning or repair).
    /// </summary>
    public static class ObstacleScenario
    {
        public static readonly Vector2 BedFootprint = new Vector2(0.9f, 2.1f);   // m, width x length of a hospital bed
        public const float ClosureDepth = 0.4f;                                  // m, depth of a barrier across the corridor

        public static GameObject Spawn(string scenario, CorridorCell cell, float along = 0f)
        {
            Vector2 footprint;
            Vector3 position;
            switch (scenario)
            {
                case "O1":
                    footprint = BedFootprint;
                    position = cell.ToWorld(cell.Width * 0.5f - BedFootprint.x * 0.5f - 0.05f, along);
                    break;
                case "O2":
                    footprint = new Vector2(cell.Width + 0.2f, ClosureDepth);
                    position = cell.ToWorld(0f, along);
                    break;
                default:
                    return null;
            }
            var go = new GameObject("Obstacle " + scenario + " " + cell.CellId);
            go.transform.SetPositionAndRotation(new Vector3(position.x, cell.transform.position.y, position.z), cell.transform.rotation);
            go.AddComponent<NavMeshObstacle>();
            go.AddComponent<CorridorObstacle>().Configure(footprint);

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            visual.transform.localScale = new Vector3(footprint.x, 0.9f, footprint.y);
            return go;
        }
    }
}
