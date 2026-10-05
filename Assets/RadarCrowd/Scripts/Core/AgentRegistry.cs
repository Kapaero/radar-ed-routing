using System.Collections.Generic;

namespace RadarCrowd
{
    /// <summary>Insertion-ordered lists of live agents and obstacles (deterministic iteration order).</summary>
    public static class AgentRegistry
    {
        static readonly List<CrowdAgent> agents = new List<CrowdAgent>();
        static readonly List<CorridorObstacle> obstacles = new List<CorridorObstacle>();

        public static IReadOnlyList<CrowdAgent> Agents => agents;
        public static IReadOnlyList<CorridorObstacle> Obstacles => obstacles;

        public static void Add(CrowdAgent agent)
        {
            if (!agents.Contains(agent))
                agents.Add(agent);
        }

        public static void Remove(CrowdAgent agent)
        {
            agents.Remove(agent);
        }

        public static void Add(CorridorObstacle obstacle)
        {
            if (!obstacles.Contains(obstacle))
                obstacles.Add(obstacle);
        }

        public static void Remove(CorridorObstacle obstacle)
        {
            obstacles.Remove(obstacle);
        }

        public static void Clear()
        {
            agents.Clear();
            obstacles.Clear();
        }
    }
}
