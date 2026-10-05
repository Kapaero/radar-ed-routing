using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RadarCrowd
{
    /// <summary>
    /// "Corridor care": trolleys with patients parked along corridor walls when the department is crowded, some with
    /// a staff member or relative standing beside them. Positions are drawn from their own random stream (identical in
    /// every condition) and only along solid wall: a position is rejected if the corridor opens to a door or a side
    /// corridor anywhere along the trolley (NavMesh ray through the wall line is not blocked).
    /// </summary>
    public static class CorridorCare
    {
        const float WallGap = 0.05f;        // m between trolley and wall
        const float EndMargin = 0.5f;       // m kept free at both ends of a cell (junctions)
        const float SpacingGap = 0.6f;      // m between consecutive trolleys on the same wall
        const float DoorClearance = 0.5f;   // m beyond the trolley ends that must also be solid wall
        const float AttendantGap = 0.35f;   // m between trolley edge and the standing person's centre

        public struct Placement
        {
            public string CellId;
            public int Side;
            public float Along;
            public Vector3 Position;
            public bool Attendant;
        }

        public static List<Placement> Place(HospitalLayout layout, RunConfig cfg, SeededRandom rng, Transform parent)
        {
            var placed = new List<Placement>();
            var occupied = new Dictionary<(int, int), List<float>>();   // (cell, side) -> along positions
            float totalLength = 0f;
            foreach (CorridorCell c in layout.Cells) totalLength += c.Length;

            for (int attempt = 0; placed.Count < cfg.corridorTrolleys && attempt < 2000; attempt++)
            {
                CorridorCell cell = PickCell(layout, totalLength, rng);
                int side = rng.Bernoulli(0.5) ? 1 : -1;
                float half = cell.Length * 0.5f - cfg.trolleyLength * 0.5f - EndMargin;
                if (half <= 0f || cell.Width < 2f * cfg.trolleyWidth)
                    continue;
                float along = rng.Range(-half, half);
                var key = (cell.Index, side);
                if (occupied.TryGetValue(key, out List<float> others) && others.Exists(o => Mathf.Abs(o - along) < cfg.trolleyLength + SpacingGap))
                    continue;
                if (!SolidWall(cell, side, along, cfg.trolleyLength))
                    continue;

                float across = side * (cell.Width * 0.5f - cfg.trolleyWidth * 0.5f - WallGap);
                Vector3 position = cell.ToWorld(across, along);
                var go = new GameObject("Trolley " + cell.CellId);
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(new Vector3(position.x, cell.transform.position.y, position.z), cell.transform.rotation);
                go.AddComponent<NavMeshObstacle>();
                go.AddComponent<CorridorObstacle>().Configure(new Vector2(cfg.trolleyWidth, cfg.trolleyLength));
                GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(visual.GetComponent<Collider>());
                visual.transform.SetParent(go.transform, false);
                visual.transform.localPosition = new Vector3(0f, 0.45f, 0f);
                visual.transform.localScale = new Vector3(cfg.trolleyWidth, 0.9f, cfg.trolleyLength);

                bool attendant = rng.Bernoulli(cfg.attendantShare);
                if (attendant)
                {
                    float personAcross = side * (cell.Width * 0.5f - cfg.trolleyWidth - WallGap - AttendantGap);
                    StandingAgent.Create(RouteBook.Snap(cell.ToWorld(personAcross, along)), parent);
                }

                if (others == null) occupied[key] = others = new List<float>();
                others.Add(along);
                placed.Add(new Placement { CellId = cell.CellId, Side = side, Along = along, Position = position, Attendant = attendant });
            }
            if (placed.Count < cfg.corridorTrolleys)
                Debug.LogWarning($"CorridorCare: placed {placed.Count} of {cfg.corridorTrolleys} trolleys");
            return placed;
        }

        static CorridorCell PickCell(HospitalLayout layout, float totalLength, SeededRandom rng)
        {
            float u = rng.Range(0f, totalLength);
            foreach (CorridorCell c in layout.Cells)
            {
                if (u < c.Length) return c;
                u -= c.Length;
            }
            return layout.Cells[layout.Cells.Count - 1];
        }

        /// <summary>True if the wall on the given side is closed along the trolley span (no door or side opening).</summary>
        public static bool SolidWall(CorridorCell cell, int side, float along, float length)
        {
            float from = along - length * 0.5f - DoorClearance;
            float to = along + length * 0.5f + DoorClearance;
            for (float z = from; z <= to + 1e-3f; z += 0.3f)
            {
                Vector3 inside = RouteBook.Snap(cell.ToWorld(side * (cell.Width * 0.5f - 0.6f), z));
                Vector3 outside = cell.ToWorld(side * (cell.PhysicalWidth * 0.5f + 0.8f), z);
                if (!NavMesh.Raycast(inside, outside, out _, RouteBook.Walking))
                    return false;   // the ray left the corridor without hitting a wall: opening
            }
            return true;
        }
    }
}
