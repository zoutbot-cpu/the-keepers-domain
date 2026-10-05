using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Grid
{
    /// 4-directional A* over DungeonGrid's walkable tiles. Uniform step cost
    /// for now; if tiles ever get variable movement cost this is the place to
    /// weight them.
    ///
    /// Generated maps run up to 96×96+ and there are dozens of call sites
    /// (every agent's PlanPathTo, Combatant reachability checks), so this is
    /// built to allocate nothing per call: per-cell state lives in flat
    /// arrays indexed by y * width + x, reused across searches and
    /// invalidated by bumping a search stamp instead of being cleared; the
    /// open set is a binary min-heap (lazy deletion — a cell whose g-score
    /// improves is simply pushed again, and stale entries are skipped when
    /// popped). Main-thread only — the buffers are shared statics.
    public static class AStarPathfinder
    {
        private struct OpenNode
        {
            public int F;
            public int H;
            public int Index;
        }

        private static int _bufferWidth;
        private static int _bufferHeight;
        private static int[] _seenStamp = new int[0];   // == _stamp: G/CameFrom valid this search
        private static int[] _closedStamp = new int[0]; // == _stamp: expanded this search
        private static int[] _gScore = new int[0];
        private static int[] _cameFrom = new int[0];
        private static readonly List<OpenNode> _heap = new List<OpenNode>(256);
        private static int _stamp;

        /// Fills path with the route from start to goal (exclusive of start,
        /// inclusive of goal) and returns true if one exists. path is cleared
        /// first either way.
        public static bool TryFindPath(DungeonGrid grid, Vector2Int start, Vector2Int goal, List<Vector2Int> path, bool isImp = false)
        {
            path.Clear();

            if (start == goal)
            {
                return true;
            }

            // The goal can only ever be entered through the neighbour
            // walkability check below, so an unwalkable goal is unreachable —
            // bail before flooding every tile reachable from start.
            if (!grid.IsWalkable(goal, isImp) || !grid.InBounds(start))
            {
                return false;
            }

            var width = grid.Width;
            EnsureBuffers(width, grid.Height);
            NextStamp();

            var startIndex = start.y * width + start.x;
            var goalIndex = goal.y * width + goal.x;

            _heap.Clear();
            _seenStamp[startIndex] = _stamp;
            _gScore[startIndex] = 0;
            _cameFrom[startIndex] = -1;
            var startH = Heuristic(start, goal);
            Push(new OpenNode { F = startH, H = startH, Index = startIndex });

            while (_heap.Count > 0)
            {
                var node = Pop();
                var current = node.Index;
                if (_closedStamp[current] == _stamp)
                {
                    continue; // stale duplicate from an earlier, worse push
                }

                if (current == goalIndex)
                {
                    BuildPath(current, width, path);
                    return true;
                }

                _closedStamp[current] = _stamp;
                var currentCoord = new Vector2Int(current % width, current / width);
                var tentativeGScore = _gScore[current] + 1;

                foreach (var offset in GridDirections.Cardinal)
                {
                    var neighbor = currentCoord + offset;
                    if (!grid.IsWalkable(neighbor, isImp))
                    {
                        continue;
                    }

                    var neighborIndex = neighbor.y * width + neighbor.x;
                    if (_closedStamp[neighborIndex] == _stamp)
                    {
                        continue;
                    }

                    if (_seenStamp[neighborIndex] == _stamp && tentativeGScore >= _gScore[neighborIndex])
                    {
                        continue;
                    }

                    _seenStamp[neighborIndex] = _stamp;
                    _gScore[neighborIndex] = tentativeGScore;
                    _cameFrom[neighborIndex] = current;
                    var h = Heuristic(neighbor, goal);
                    Push(new OpenNode { F = tentativeGScore + h, H = h, Index = neighborIndex });
                }
            }

            return false;
        }

        private static void EnsureBuffers(int width, int height)
        {
            if (width == _bufferWidth && height == _bufferHeight)
            {
                return;
            }

            var size = width * height;
            _bufferWidth = width;
            _bufferHeight = height;
            _seenStamp = new int[size];
            _closedStamp = new int[size];
            _gScore = new int[size];
            _cameFrom = new int[size];
            _stamp = 0;
        }

        private static void NextStamp()
        {
            _stamp++;
            if (_stamp == int.MaxValue)
            {
                // Wrapped after ~2 billion searches — wipe so old stamps
                // can't collide with the restarted counter.
                System.Array.Clear(_seenStamp, 0, _seenStamp.Length);
                System.Array.Clear(_closedStamp, 0, _closedStamp.Length);
                _stamp = 1;
            }
        }

        /// Lower F first; on a tie prefer the node nearer the goal (lower H),
        /// which keeps A* from fanning out sideways across open floor.
        private static bool Less(OpenNode a, OpenNode b)
        {
            return a.F < b.F || (a.F == b.F && a.H < b.H);
        }

        private static void Push(OpenNode node)
        {
            _heap.Add(node);
            var i = _heap.Count - 1;
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (!Less(node, _heap[parent]))
                {
                    break;
                }

                _heap[i] = _heap[parent];
                i = parent;
            }

            _heap[i] = node;
        }

        private static OpenNode Pop()
        {
            var top = _heap[0];
            var lastIndex = _heap.Count - 1;
            var last = _heap[lastIndex];
            _heap.RemoveAt(lastIndex);
            if (lastIndex == 0)
            {
                return top;
            }

            var i = 0;
            var count = _heap.Count;
            while (true)
            {
                var child = 2 * i + 1;
                if (child >= count)
                {
                    break;
                }

                if (child + 1 < count && Less(_heap[child + 1], _heap[child]))
                {
                    child++;
                }

                if (!Less(_heap[child], last))
                {
                    break;
                }

                _heap[i] = _heap[child];
                i = child;
            }

            _heap[i] = last;
            return top;
        }

        private static int Heuristic(Vector2Int a, Vector2Int b)
        {
            return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
        }

        private static void BuildPath(int goalIndex, int width, List<Vector2Int> path)
        {
            for (var index = goalIndex; _cameFrom[index] != -1; index = _cameFrom[index])
            {
                path.Add(new Vector2Int(index % width, index / width));
            }

            path.Reverse();
        }
    }
}
