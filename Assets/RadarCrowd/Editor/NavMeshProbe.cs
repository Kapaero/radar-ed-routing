using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd.EditorTools
{
    /// <summary>
    /// Diagnostic: NavMesh paths between given points in the hospital scene (pedestrian NavMesh), written to
    /// Docs/navmesh_probe.txt. Batch mode: -executeMethod RadarCrowd.EditorTools.NavMeshProbe.Run
    /// </summary>
    public static class NavMeshProbe
    {
        static readonly Vector2[,] Pairs =
        {
            { new Vector2(87.2f, -76.9f), new Vector2(85.5f, -74.7f) },
            { new Vector2(78.99f, -77.18f), new Vector2(80.5f, -76.2f) },
            { new Vector2(88.6f, -65.9f), new Vector2(85.5f, -65.7f) },
            { new Vector2(66.49f, -61.3f), new Vector2(64.4f, -59.8f) },
            { new Vector2(83.97f, -72.35f), new Vector2(85.5f, -74.9f) },
            { new Vector2(79.17f, -32.82f), new Vector2(79.2f, -30.7f) },
        };

        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/RadarCrowd/Scenes/Hospital.unity");
            var filter = new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
            var sb = new StringBuilder();
            var path = new NavMeshPath();
            for (int i = 0; i < Pairs.GetLength(0); i++)
            {
                Vector3 a = new Vector3(Pairs[i, 0].x, 0f, Pairs[i, 0].y), b = new Vector3(Pairs[i, 1].x, 0f, Pairs[i, 1].y);
                bool sa = NavMesh.SamplePosition(a, out NavMeshHit ha, 2f, filter);
                bool sb2 = NavMesh.SamplePosition(b, out NavMeshHit hb, 2f, filter);
                bool ok = sa && sb2 && NavMesh.CalculatePath(ha.position, hb.position, filter, path);
                float length = 0f;
                for (int k = 1; k < path.corners.Length; k++) length += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} -> {1}: snapped a {2} ({3:F2} m off, y {4:F2}), b {5} ({6:F2} m off, y {7:F2}); path {8} {9}, {10} corners, {11:F1} m; ray hit {12}",
                    a, b, ha.position, Vector3.Distance(a, ha.position), ha.position.y, hb.position, Vector3.Distance(b, hb.position), hb.position.y,
                    ok, path.status, path.corners.Length, length, NavMesh.Raycast(ha.position, hb.position, out NavMeshHit hit, filter) ? hit.position.ToString() : "none"));
            }
            // the cubicle nearest to each start point, and the corners of its path to the wing entry
            var cubicles = new System.Collections.Generic.List<GameObject>();
            cubicles.AddRange(GameObject.FindGameObjectsWithTag("Cubicle"));
            cubicles.AddRange(GameObject.FindGameObjectsWithTag("CubicleLeft"));
            HospitalLayout layout = Object.FindObjectsByType<HospitalLayout>(FindObjectsInactive.Include, FindObjectsSortMode.None)[0];
            var entryField = typeof(HospitalLayout).GetField("wingEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Vector3 entry = ((Transform)entryField.GetValue(layout)).position;
            NavMesh.SamplePosition(entry, out NavMeshHit he, 3f, filter);
            for (int i = 0; i < Pairs.GetLength(0); i++)
            {
                Vector3 a = new Vector3(Pairs[i, 0].x, 0f, Pairs[i, 0].y);
                GameObject nearest = null;
                foreach (GameObject c in cubicles)
                    if (nearest == null || Vector3.Distance(c.transform.position, a) < Vector3.Distance(nearest.transform.position, a)) nearest = c;
                NavMesh.SamplePosition(nearest.transform.position, out NavMeshHit hc, 3f, filter);
                bool ok = NavMesh.CalculatePath(hc.position, he.position, filter, path);
                sb.Append(string.Format(CultureInfo.InvariantCulture, "patient at {0}: nearest cubicle '{1}' marker {2} snapped {3}; to entry {4} {5}: ",
                    a, nearest.name, nearest.transform.position, hc.position, ok, path.status));
                for (int k = 0; k < Mathf.Min(5, path.corners.Length); k++) sb.Append(path.corners[k].ToString("F2")).Append(' ');
                NavMesh.SamplePosition(a, out NavMeshHit hp, 2f, filter);
                bool ok2 = NavMesh.CalculatePath(hp.position, hc.position, filter, path);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| patient->cubicle {0} {1}", ok2, path.status));
            }
            System.IO.File.WriteAllText("Docs/navmesh_probe.txt", sb.ToString());
            Debug.Log("NavMeshProbe:\n" + sb);
        }
    }
}
