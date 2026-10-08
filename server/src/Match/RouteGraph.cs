using System;
using System.Collections.Generic;
using System.Linq;

namespace EpidemicServer.Match
{
    /// <summary>
    /// A waypoint network for bots (ours). The game's pathfinder (role PathFinder.Find) only finds routes a few hundred
    /// units long on Scavenger maps (probe: ~300 route, ~900 don't, whatever the budget), so long trips go over
    /// a graph: nodes are the map's own zombie spawn spots and points of interest thinned to one per NodeSpacing,
    /// edges join nodes up to EdgeLength apart that the navmesh routes nearly straight (short searches work). A trip
    /// is the shortest graph route (Dijkstra) from the nearest node the bot can reach to the nearest node that reaches
    /// the destination; each hop is short and nearly straight.
    /// </summary>
    public sealed class RouteGraph
    {
        public const float NodeSpacing = 45f, EdgeLength = 160f;
        public readonly List<float[]> Nodes = new List<float[]>();
        private readonly List<List<int>> _edges = new List<List<int>>();
        public int EdgeCount;

        public static RouteGraph Build(IEnumerable<float[]> points)
        {
            var g = new RouteGraph();
            foreach (float[] p in points)
                if (!g.Nodes.Any(n => BotDriver.Dist(n, p) < NodeSpacing)) g.Nodes.Add(new[] { p[0], p[1] });
            for (int i = 0; i < g.Nodes.Count; i++) g._edges.Add(new List<int>());
            for (int i = 0; i < g.Nodes.Count; i++)
                for (int j = i + 1; j < g.Nodes.Count; j++)
                {
                    if (BotDriver.Dist(g.Nodes[i], g.Nodes[j]) > EdgeLength) continue;
                    // An edge needs a short navmesh route (the collision line check alone missed cliffs and
                    // walls); a clear line is accepted only if the navmesh agrees within RouteSlack.
                    if (!Reachable(g.Nodes[i], g.Nodes[j])) continue;
                    g._edges[i].Add(j);
                    g._edges[j].Add(i);
                    g.EdgeCount++;
                }
            return g;
        }

        public const float RouteSlack = 2.5f;
        public const int EdgeIterations = 20000;

        /// <summary>True when the navmesh routes a to b in at most RouteSlack times the straight distance.</summary>
        public static bool Reachable(float[] a, float[] b)
        {
            List<float[]> path;
            try { path = ServerHooks.Path(a, b, EdgeIterations, true); } catch (Exception) { return false; }
            if (path == null) return false;
            float len = 0f;
            float[] at = a;
            foreach (float[] p in path) { len += BotDriver.Dist(at, p); at = p; }
            len += BotDriver.Dist(at, b);
            return len <= RouteSlack * BotDriver.Dist(a, b) + 20f;
        }

        /// <summary>The nearest node with a clear line to the point (within EdgeLength), or -1.</summary>
        private int Visible(float[] p)
        {
            var order = Enumerable.Range(0, Nodes.Count).Where(i => BotDriver.Dist(Nodes[i], p) <= EdgeLength).OrderBy(i => BotDriver.Dist(Nodes[i], p)).Take(12);
            foreach (int i in order)
            {
                if (Reachable(p, Nodes[i])) return i;
            }
            return -1;
        }

        /// <summary>Connected-component id per node (for diagnostics).</summary>
        public int[] Components()
        {
            var comp = Enumerable.Repeat(-1, Nodes.Count).ToArray();
            int c = 0;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (comp[i] >= 0) continue;
                var stack = new Stack<int>(); stack.Push(i); comp[i] = c;
                while (stack.Count > 0) { int u = stack.Pop(); foreach (int v in _edges[u]) if (comp[v] < 0) { comp[v] = c; stack.Push(v); } }
                c++;
            }
            return comp;
        }

        public int NearestNode(float[] p) { return Enumerable.Range(0, Nodes.Count).OrderBy(i => BotDriver.Dist(Nodes[i], p)).FirstOrDefault(); }

        /// <summary>Waypoints from a point to a destination over the graph (the destination last), or null.</summary>
        public List<float[]> Route(float[] from, float[] to)
        {
            int a = Visible(from), b = Visible(to);
            if (a < 0 || b < 0) return null;
            var dist = new float[Nodes.Count];
            var prev = new int[Nodes.Count];
            for (int i = 0; i < dist.Length; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            dist[a] = 0f;
            var open = new HashSet<int> { a };
            while (open.Count > 0)
            {
                int u = open.OrderBy(x => dist[x]).First();
                open.Remove(u);
                if (u == b) break;
                foreach (int v in _edges[u])
                {
                    float d = dist[u] + BotDriver.Dist(Nodes[u], Nodes[v]);
                    if (d < dist[v]) { dist[v] = d; prev[v] = u; open.Add(v); }
                }
            }
            if (dist[b] == float.MaxValue) return null;
            var route = new List<float[]>();
            for (int n = b; n >= 0; n = prev[n]) route.Add(Nodes[n]);
            route.Reverse();
            route.Add(to);
            return route;
        }
    }
}
