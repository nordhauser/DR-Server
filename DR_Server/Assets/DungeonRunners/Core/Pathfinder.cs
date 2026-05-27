using System;
using System.Collections.Generic;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Server-side A* pathfinder, ported from client's <c>Pathfinder</c> class
    /// (constructor 0x005D2620, RequestPath 0x005D2950, UpdateRequest 0x005D2AF0, GetPath 0x005D2F20).
    /// Phase 4 of Option 1-full.
    ///
    /// Algorithm matches the client's per-tick A* expander semantically: same direction table,
    /// same costs, same Manhattan heuristic, same corner-only waypoint reconstruction.
    /// Tie-breaking uses grid coords (stable, scope-relaxed per D2 derisk — client uses
    /// pointer address order which isn't portable).
    ///
    /// Usage:
    /// <code>
    ///   var pf = new Pathfinder(pathMap);
    ///   pf.RequestPath(start, goal);
    ///   while (!pf.IsDone) pf.UpdateRequest(64);  // expand N nodes per call
    ///   if (pf.GetPath(out var waypoints)) { ... }
    /// </code>
    /// </summary>
    public sealed class Pathfinder
    {
        // ─── Constants ──────────────────────────────────────────────────────

        /// <summary>Goal-direction dot-product gate: skip neighbors whose direction
        /// deviates more than ~60° from initial direction. Mirrors client check at
        /// UpdateRequest +offset ("if dot &lt; 0x80 skip"). Disabled here for v1 since
        /// it requires Fixed32 vector math; preliminary tests show acceptable paths
        /// without it.</summary>
        private const bool ApplyDirectionGate = false;

        // ─── State ──────────────────────────────────────────────────────────

        private readonly PathMap _pathMap;

        private PathNode _startPathMapNode;
        private PathNode _goalPathMapNode;

        private readonly SortedSet<Node> _openSet = new SortedSet<Node>(NodeComparer.Instance);
        private readonly Dictionary<PathNode, Node> _openByPathMapNode = new Dictionary<PathNode, Node>();
        private readonly Dictionary<PathNode, Node> _closedByPathMapNode = new Dictionary<PathNode, Node>();

        private Node _bestNode;
        private bool _done;
        private bool _directReach;
        private int _nodesExpanded;

        public bool IsDone => _done || _openSet.Count == 0;
        public bool DirectReach => _directReach;
        public int NodesExpanded => _nodesExpanded;

        public Pathfinder(PathMap pathMap)
        {
            _pathMap = pathMap ?? throw new ArgumentNullException(nameof(pathMap));
        }

        // ─── RequestPath: seed the open set with the start node ────────────

        /// <summary>
        /// Initialize a path request. Mirrors client <c>Pathfinder::RequestPath</c> @ 0x005D2950.
        /// Detects direct reach via <see cref="PathMap.TryCanReachPoint"/> and short-circuits.
        /// </summary>
        public void RequestPath(PathNode startPathMapNode, PathNode goalPathMapNode)
        {
            if (startPathMapNode == null) throw new ArgumentNullException(nameof(startPathMapNode));
            if (goalPathMapNode == null) throw new ArgumentNullException(nameof(goalPathMapNode));

            Reset();

            _startPathMapNode = startPathMapNode;
            _goalPathMapNode = goalPathMapNode;

            // Direct-reach early-out: if start and goal share a straight-line walkable corridor,
            // emit a 2-waypoint path without running A*.
            if (startPathMapNode != goalPathMapNode)
            {
                bool tryOK = _pathMap.TryCanReachPoint(
                    startPathMapNode.WorldX, startPathMapNode.WorldY,
                    goalPathMapNode.WorldX, goalPathMapNode.WorldY,
                    out bool reach);
                if (tryOK && reach)
                {
                    _directReach = true;
                    _done = true;
                    return;
                }
            }
            else
            {
                _directReach = true;
                _done = true;
                return;
            }

            // Seed open set with start node. g=0, h=Manhattan to goal, f=g+h.
            var startNode = new Node
            {
                PathMapNode = startPathMapNode,
                G = 0,
                F = ManhattanHeuristic(startPathMapNode, goalPathMapNode),
                Parent = null,
            };
            _openSet.Add(startNode);
            _openByPathMapNode[startPathMapNode] = startNode;
            _bestNode = startNode;
        }

        // ─── UpdateRequest: expand up to N nodes ───────────────────────────

        /// <summary>
        /// Step the A* search up to <paramref name="maxSteps"/> nodes. Returns count expanded.
        /// Mirrors client <c>Pathfinder::UpdateRequest</c> @ 0x005D2AF0.
        /// </summary>
        public int UpdateRequest(int maxSteps)
        {
            if (_done) return 0;

            int expanded = 0;
            while (expanded < maxSteps)
            {
                if (_openSet.Count == 0)
                {
                    _done = true;
                    break;
                }

                Node current = _openSet.Min;
                _openSet.Remove(current);
                _openByPathMapNode.Remove(current.PathMapNode);
                _closedByPathMapNode[current.PathMapNode] = current;

                // Track best-so-far by lowest f (used by GetPath if open set drains without
                // hitting goal).
                if (_bestNode == null || current.F < _bestNode.F)
                    _bestNode = current;

                if (current.PathMapNode == _goalPathMapNode)
                {
                    _bestNode = current;
                    _done = true;
                    break;
                }

                ExpandNeighbors(current);
                expanded++;
            }

            _nodesExpanded += expanded;
            return expanded;
        }

        private void ExpandNeighbors(Node current)
        {
            byte connectionFlags = current.PathMapNode.ConnectionFlags;
            int cx = current.PathMapNode.GridX;
            int cy = current.PathMapNode.GridY;

            for (int dirIndex = 0; dirIndex < 8; dirIndex++)
            {
                // ConnectionFlags gate (mirrors client: only step direction i if bit i set).
                if ((connectionFlags & (1 << dirIndex)) == 0) continue;

                var (dx, dy) = PathMap.Directions[dirIndex];
                PathNode neighbor = _pathMap.GetNodeAt(cx + dx, cy + dy);
                if (neighbor == null) continue;
                if (!neighbor.IsWalkable) continue;

                int stepCost = PathMap.DirectionCosts[dirIndex];
                int newG = current.G + stepCost;

                // Skip if neighbor is in closed set with equal-or-better g.
                if (_closedByPathMapNode.TryGetValue(neighbor, out var closedNode) && closedNode.G <= newG)
                    continue;

                // Skip if neighbor is in open set with equal-or-better g.
                if (_openByPathMapNode.TryGetValue(neighbor, out var openNode) && openNode.G <= newG)
                    continue;

                // Otherwise create/replace.
                if (openNode != null)
                {
                    _openSet.Remove(openNode);
                    _openByPathMapNode.Remove(neighbor);
                }
                if (closedNode != null)
                    _closedByPathMapNode.Remove(neighbor);

                int h = ManhattanHeuristic(neighbor, _goalPathMapNode);
                var newNode = new Node
                {
                    PathMapNode = neighbor,
                    G = newG,
                    F = newG + h,
                    Parent = current,
                };
                _openSet.Add(newNode);
                _openByPathMapNode[neighbor] = newNode;
            }
        }

        // ─── GetPath: reconstruct corner-only waypoints ─────────────────────

        /// <summary>
        /// Reconstruct the path from goal back to start via parent chain. Emits waypoints
        /// only on direction change (corner-only). Mirrors client <c>Pathfinder::GetPath</c>
        /// @ 0x005D2F20. Returns false if no path was found.
        /// </summary>
        public bool GetPath(out List<PathNode> waypoints)
        {
            waypoints = new List<PathNode>();

            if (_directReach)
            {
                if (_startPathMapNode == null || _goalPathMapNode == null) return false;
                waypoints.Add(_startPathMapNode);
                waypoints.Add(_goalPathMapNode);
                return true;
            }

            // Use the closed node matching the goal if one exists; else best-so-far.
            Node tail = null;
            if (_goalPathMapNode != null && _closedByPathMapNode.TryGetValue(_goalPathMapNode, out var goalNode))
                tail = goalNode;
            else if (_bestNode != null && _bestNode.PathMapNode == _goalPathMapNode)
                tail = _bestNode;

            if (tail == null) return false;

            // Walk parent chain goal→start, accumulating corner waypoints.
            // Reverse to start→goal order at the end.
            var reversed = new List<PathNode>();
            int prevDir = -1;
            Node node = tail;
            while (node != null)
            {
                if (node.Parent != null)
                {
                    int dir = PathMap.GetDirFromAToB(node.Parent.PathMapNode, node.PathMapNode);
                    if (prevDir == -1 || dir != prevDir)
                    {
                        reversed.Add(node.PathMapNode);
                    }
                    prevDir = dir;
                }
                else
                {
                    // start node — always include as final entry (which becomes the first
                    // waypoint after reversal).
                    reversed.Add(node.PathMapNode);
                }
                node = node.Parent;
            }

            for (int i = reversed.Count - 1; i >= 0; i--)
                waypoints.Add(reversed[i]);

            return waypoints.Count >= 2;
        }

        // ─── Internals ──────────────────────────────────────────────────────

        private static int ManhattanHeuristic(PathNode a, PathNode b)
        {
            int dx = a.GridX - b.GridX;
            int dy = a.GridY - b.GridY;
            if (dx < 0) dx = -dx;
            if (dy < 0) dy = -dy;
            return (dx + dy) * 10; // each grid step is 10 cost units (cardinal); matches mCostTable
        }

        private void Reset()
        {
            _openSet.Clear();
            _openByPathMapNode.Clear();
            _closedByPathMapNode.Clear();
            _bestNode = null;
            _done = false;
            _directReach = false;
            _nodesExpanded = 0;
            _startPathMapNode = null;
            _goalPathMapNode = null;
        }

        private sealed class Node
        {
            public PathNode PathMapNode;
            public int G;
            public int F;
            public Node Parent;
        }

        private sealed class NodeComparer : IComparer<Node>
        {
            public static readonly NodeComparer Instance = new NodeComparer();
            public int Compare(Node x, Node y)
            {
                if (ReferenceEquals(x, y)) return 0;
                int c = x.F.CompareTo(y.F);
                if (c != 0) return c;
                // Stable tie-break by grid coords (D2 derisk: scope-relaxed from client's
                // pointer-order tiebreak — ≤1-tile drift acceptable per DoD).
                c = x.PathMapNode.GridX.CompareTo(y.PathMapNode.GridX);
                if (c != 0) return c;
                c = x.PathMapNode.GridY.CompareTo(y.PathMapNode.GridY);
                if (c != 0) return c;
                // Distinct nodes must sort to non-zero; fall back to hash.
                return x.GetHashCode().CompareTo(y.GetHashCode());
            }
        }
    }
}
