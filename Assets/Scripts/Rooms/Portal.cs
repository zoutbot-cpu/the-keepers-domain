using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// The player's home tile — visually a staircase leading up out of the
    /// dungeon ("to the overworld"), not just another floor tile. Its room
    /// is carved pre-claimed by GameBootstrap (DungeonGrid.CarveRoom) —
    /// claiming everywhere else is an impling task (see BuilderJobBoard's
    /// claim jobs), not something the portal grants by proximity.
    public class Portal : MonoBehaviour
    {
        private const int StepCount = 3;

        [SerializeField] private Color _stepColor = new Color(0.62f, 0.56f, 0.42f);

        public Vector2Int Coord { get; private set; }

        public void Initialize(Vector2Int coord, DungeonGrid grid)
        {
            Coord = coord;

            transform.position = grid.GridToWorld(coord);
            BuildStaircaseVisual(grid.CellSize, grid.FloorSurfaceY);
        }

        /// Fakes a staircase with a row of ascending step cubes, all within
        /// this one tile — cheap with primitives, still reads clearly as
        /// "the way out" rather than just another floor tile. Grounded at
        /// floorSurfaceY, not y=0 — floor tiles don't actually sit at y=0
        /// (see DungeonGrid.FloorSurfaceY), so stairs anchored to world-zero
        /// would float visibly above the ground they're supposed to start on.
        private void BuildStaircaseVisual(float cellSize, float floorSurfaceY)
        {
            var stepDepth = cellSize / StepCount;

            for (int i = 0; i < StepCount; i++)
            {
                var stepHeight = cellSize * 0.15f * (i + 1);
                var offsetAlongTile = (i - (StepCount - 1) / 2f) * stepDepth;

                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"PortalStep_{i}";
                step.transform.SetParent(transform, false);
                step.transform.localPosition = new Vector3(offsetAlongTile, floorSurfaceY + stepHeight * 0.5f, 0f);
                step.transform.localScale = new Vector3(stepDepth * 0.95f, stepHeight, cellSize * 0.9f);
                step.GetComponent<Renderer>().material.color = _stepColor;
                Destroy(step.GetComponent<Collider>());
            }
        }
    }
}
