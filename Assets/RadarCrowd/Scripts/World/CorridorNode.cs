using UnityEngine;

namespace RadarCrowd
{
    /// <summary>Junction of the corridor graph (e.g. where corridor A meets the top corridor). Routes are built as via-node sequences.</summary>
    public class CorridorNode : MonoBehaviour
    {
        [SerializeField] string nodeId = "JA";
        [SerializeField] CorridorNode[] neighbours = new CorridorNode[0];
        [SerializeField] bool entryNode;   // first node a route may start from (reached from the wing entry)
        [SerializeField] bool walkerEndpoint = true;

        public string NodeId => nodeId;
        public CorridorNode[] Neighbours => neighbours;
        public bool EntryNode => entryNode;
        public bool WalkerEndpoint => walkerEndpoint;

        public void Configure(string id, bool isEntry, bool isWalkerEndpoint)
        {
            nodeId = id;
            entryNode = isEntry;
            walkerEndpoint = isWalkerEndpoint;
        }

        public void SetNeighbours(CorridorNode[] nodes)
        {
            neighbours = nodes;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.6f);
            if (neighbours == null)
                return;
            foreach (CorridorNode n in neighbours)
                if (n != null)
                    Gizmos.DrawLine(transform.position, n.transform.position);
        }
    }
}
