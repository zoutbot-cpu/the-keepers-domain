using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// The player's home tile — visually a staircase leading up out of the
    /// dungeon ("to the overworld"), not just another floor tile. Its room
    /// is carved pre-claimed by GameBootstrap (DungeonGrid.CarveRoom) —
    /// claiming everywhere else is an impling task (see BuilderJobBoard's
    /// claim jobs), not something the portal grants by proximity. Also owns
    /// this map's recruitable-creature pool: every non-Imp creature has to
    /// "join" the domain by coming down this stairway, so recruiting one
    /// (see TryTakeFromPool) is gated on this pool rather than a creature
    /// type's own spawner being able to conjure one out of nothing.
    public class Portal : MonoBehaviour
    {
        private const int StepCount = 3;

        [SerializeField] private Color _stepColor = new Color(0.62f, 0.56f, 0.42f);

        public Vector2Int Coord { get; private set; }

        // Keyed by creature kind (e.g. GremlinAgent.CreatureKind) — what's
        // actually available depends on the map being played; for now
        // GameBootstrap just seeds this directly (10 Gremlins) rather than
        // reading it from real per-map data, which doesn't exist yet.
        private readonly Dictionary<string, int> _creaturePool = new Dictionary<string, int>();

        public void Initialize(Vector2Int coord, DungeonGrid grid)
        {
            Coord = coord;

            transform.position = grid.GridToWorld(coord);
            BuildStairway(grid.CellSize, grid.FloorSurfaceY);
        }

        /// Adds count to this portal's pool of a recruitable creature kind.
        public void SeedPool(string creatureKind, int count)
        {
            _creaturePool.TryGetValue(creatureKind, out var current);
            _creaturePool[creatureKind] = current + count;
        }

        /// How many of creatureKind are still available to recruit — read
        /// by UI (e.g. BottomMenuBar's Creatures tab) to show/disable the
        /// recruit button.
        public int GetPoolCount(string creatureKind)
        {
            return _creaturePool.TryGetValue(creatureKind, out var count) ? count : 0;
        }

        /// Takes one creature of creatureKind out of the pool if available.
        /// This is the actual gate behind "creatures join by coming down
        /// the portal stairway" — a creature's own spawner (e.g.
        /// GremlinSpawner.TryRecruitGremlin) calls this before creating
        /// anything, so recruiting can never exceed what the map's pool
        /// allows.
        public bool TryTakeFromPool(string creatureKind)
        {
            if (!_creaturePool.TryGetValue(creatureKind, out var count) || count <= 0)
            {
                return false;
            }

            _creaturePool[creatureKind] = count - 1;
            return true;
        }

        // The room itself is still being reworked — this is just the
        // default facing/size for the new dungeon_pack Portal prop until
        // then. -20 (first pass) + another -70 = -90.
        private const float PortalYRotationDegrees = -90f;
        private const float PortalScaleMultiplier = 2f;

        /// dungeon_pack's Portal prop (see Tools > DungeonPack > Setup
        /// Props) replaces the primitive step cubes below — scaled
        /// uniformly to fit this room's single tile (then doubled, see
        /// PortalScaleMultiplier — deliberately overflows past the tile's
        /// own edges now), pivot-at-base like every other dungeon_pack mesh
        /// so it's grounded at floorSurfaceY (re-exported once already to
        /// fix a version that floated — see the source .obj's own Y bounds
        /// if this needs rechecking). Falls back to the original primitive
        /// staircase if the prop hasn't been set up yet.
        private void BuildStairway(float cellSize, float floorSurfaceY)
        {
            var prefab = Resources.Load<GameObject>("Dungeon/Prop_Portal");
            if (prefab == null)
            {
                BuildStaircaseVisual(cellSize, floorSurfaceY);
                return;
            }

            var portal = Instantiate(prefab, transform, false);
            portal.name = "PortalGateway";
            portal.transform.localPosition = Vector3.zero;
            portal.transform.localRotation = Quaternion.Euler(0f, PortalYRotationDegrees, 0f);
            portal.transform.localScale = Vector3.one;

            var renderers = portal.GetComponentsInChildren<Renderer>();
            var scale = 1f;
            var xzCenterOffset = Vector2.zero;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                var footprint = Mathf.Max(bounds.size.x, bounds.size.z);
                if (footprint > 0.01f)
                {
                    scale = (cellSize * 0.95f * PortalScaleMultiplier) / footprint;
                }

                // The mesh's footprint isn't centered on its own local
                // origin (its bounds are lopsided, not symmetric around
                // 0) — measured here at scale 1/before the final position
                // is set, so the correction can be scaled up consistently
                // with whatever final scale gets applied below, keeping
                // the prop's visual center (not just its pivot) on the
                // tile's actual center rather than off to one side.
                var worldCenterOffset = bounds.center - transform.position;
                xzCenterOffset = new Vector2(worldCenterOffset.x, worldCenterOffset.z);
            }

            portal.transform.localScale = Vector3.one * scale;
            portal.transform.localPosition = new Vector3(-xzCenterOffset.x * scale, floorSurfaceY, -xzCenterOffset.y * scale);
        }

        /// Fakes a staircase with a row of ascending step cubes, all within
        /// this one tile — cheap with primitives, still reads clearly as
        /// "the way out" rather than just another floor tile. Grounded at
        /// floorSurfaceY, not y=0 — floor tiles don't actually sit at y=0
        /// (see DungeonGrid.FloorSurfaceY), so stairs anchored to world-zero
        /// would float visibly above the ground they're supposed to start on.
        /// Fallback only now — see BuildStairway.
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
