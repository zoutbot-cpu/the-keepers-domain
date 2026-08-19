using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Simple 4-directional A* over DungeonGrid's walkable (Floor) tiles.
    /// Grids here are small (tens of tiles across), so a linear-scan open set
    /// is plenty fast — no need for a binary heap at this scale. Uniform step
    /// cost for now; if tiles ever get variable movement cost this is the
    /// place to weight them.
    public static class AStarPathfinder
    {
        /// Fills path with the route from start to goal (exclusive of start,
        /// inclusive of goal) and returns true if one exists. path is cleared
        /// first either way.
        public static bool TryFindPath(DungeonGrid grid, Vector2Int start, Vector2Int goal, List<Vector2Int> path)
        {
            path.Clear();

            if (start == goal)
            {
                return true;
            }

            var openSet = new List<Vector2Int> { start };
            var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            var gScore = new Dictionary<Vector2Int, int> { [start] = 0 };
            var fScore = new Dictionary<Vector2Int, int> { [start] = Heuristic(start, goal) };
            var closed = new HashSet<Vector2Int>();

            while (openSet.Count > 0)
            {
                var current = PopLowestFScore(openSet, fScore);

                if (current == goal)
                {
                    BuildPath(cameFrom, current, path);
                    return true;
                }

                closed.Add(current);

                foreach (var offset in GridDirections.Cardinal)
                {
                    var neighbor = current + offset;
                    if (closed.Contains(neighbor) || !grid.IsWalkable(neighbor))
                    {
                        continue;
                    }

                    var tentativeGScore = gScore[current] + 1;
                    if (gScore.TryGetValue(neighbor, out var existingGScore) && tentativeGScore >= existingGScore)
                    {
                        continue;
                    }

                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeGScore;
                    fScore[neighbor] = tentativeGScore + Heuristic(neighbor, goal);

                    if (!openSet.Contains(neighbor))
                    {
                        openSet.Add(neighbor);
                    }
                }
            }

            return false;
        }

        private static Vector2Int PopLowestFScore(List<Vector2Int> openSet, Dictionary<Vector2Int, int> fScore)
        {
            var bestIndex = 0;
            var bestScore = fScore[openSet[0]];
            for (int i = 1; i < openSet.Count; i++)
            {
                var score = fScore[openSet[i]];
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            var result = openSet[bestIndex];
            openSet.RemoveAt(bestIndex);
            return result;
        }

        private static int Heuristic(Vector2Int a, Vector2Int b)
        {
            return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
        }

        private static void BuildPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int current, List<Vector2Int> path)
        {
            while (cameFrom.TryGetValue(current, out var previous))
            {
                path.Add(current);
                current = previous;
            }

            path.Reverse();
        }
    }
}
