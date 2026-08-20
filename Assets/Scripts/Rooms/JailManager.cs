using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Storage for defeated creatures the player wishes to keep, per the
    /// design doc — a sunken pit inset one tile from every edge of the
    /// room's own footprint, so a placed Jail is a walkable ground-level
    /// walkway ring around a lower pit, not pit wall-to-wall. Hard 5x5
    /// minimum footprint (see MinFootprintSize) — same
    /// SlimeHatcheryManager-style "reject anything smaller than
    /// MinFootprintSize in either dimension" check every hard-minimum
    /// room uses — which guarantees at least a 3x3 pit inside the 1-tile
    /// ring. Ringed by a low fence around the pit's own boundary (not the
    /// room's outer edge) with a single staircase-and-gate entrance.
    /// Attracts the Maze Rattler, but that creature (and any actual
    /// capture/prisoner mechanic) doesn't exist yet — this is
    /// placement/visuals only, same "player-placed, subscribes to
    /// RoomSold" shape TrainingRoomManager/LibraryManager use, including
    /// their adjacent-placement merge rule (see TryFindMergeableRoom): a
    /// footprint that exactly completes a rectangle together with an
    /// existing Jail extends that Jail instead of starting a separate
    /// one. A merge can only ever grow the room, which can only ever grow
    /// or hold steady each tile's margin to the room's own edges — so a
    /// merge can turn a former ring tile into a pit tile (if the seam
    /// between the two merged rectangles was previously an outer edge and
    /// is now interior) but never the reverse; see IsPitTile and its call
    /// site in TryPlaceJailInternal, which re-evaluates every tile in the
    /// room (not just the newly added footprint) on every placement for
    /// exactly this reason.
    ///
    /// Two things set a Jail apart from every other room type:
    /// - It can be placed directly on undug Rock — CanPlaceFootprint
    ///   accepts Rock tiles (dug and claimed on the spot, see
    ///   TryPlaceJailInternal) alongside the usual already-dug
    ///   DungeonGrid.CanBuildRoomOn tiles every other room requires. Per
    ///   the brief: "not needed to be dug out, placing the room tiles
    ///   will do that."
    /// - Its floor renders one level lower (DungeonGrid.SetPitDepth) than
    ///   ordinary floor. That's a render-time Y offset only —
    ///   DungeonGrid.IsWalkable checks Type/IsBlocked, never Y — so a Jail
    ///   tile is exactly as walkable as any other Floor tile despite
    ///   sitting visually in a pit; prisoners/implings can path across it
    ///   like any room. The "infinite blocks below" look asked for is a
    ///   stack of black panel-with-a-gray-cross "blocks" this class
    ///   builds along the pit's outer rim (see BuildRimWallVisual/
    ///   BuildRimWallBlock) reaching down far past anything the camera
    ///   can ever see the bottom of — not a second grid layer, and
    ///   deliberately NOT a slab under the whole tile: a full-footprint
    ///   column reaching up to ground level would sit physically in front
    ///   of (above) the sunk floor from any downward-looking angle and
    ///   bury it, which is exactly the "solid black box" bug an earlier
    ///   version of this had. Only the thin rim, inset like the fence
    ///   rail rather than covering the tile, is needed — interior pit
    ///   tiles have no elevation seam against their neighbors and don't
    ///   need anything under them, same as ordinary DungeonGrid floor
    ///   never does.
    public class JailManager : MonoBehaviour
    {
        /// Gold cost per tile of a placed Jail — no design-brief value
        /// exists yet, so this matches Training Room/Library's own
        /// unbalanced 20g/tile placeholder rather than inventing a new
        /// number.
        public const int CostPerTile = 20;

        // Hard minimum, same enforcement shape as
        // SlimeHatcheryManager.MinFootprintSize — checked against the
        // dragged footprint itself in TryPlaceJailInternal, not the
        // merged result (a merge-extension can be smaller, same as every
        // other mergeable room type allows). Guarantees the 1-tile ring
        // always has a real pit (at least 3x3) inside it.
        private const int MinFootprintSize = 5;

        // One full grid cell lower than the surrounding ground — "one
        // level deeper" per the brief. DungeonGrid's cellSize is 1 world
        // unit per grid step, so this is exactly one step down.
        private const float PitDepth = 1f;

        // How far down the rim wall reaches. Not literally infinite —
        // just far enough that the iso camera (min pitch 20°, max
        // orthographic size 20 — see IsoCameraController) can never see
        // its bottom edge at any zoom/pan/rotation the player can reach,
        // so it reads as bottomless.
        private const float RimWallDepth = 40f;

        // Rock's own top face sits at 0.5 (see DungeonGrid.RefreshVisual)
        // — the rim wall reaches up to that same height so it closes
        // flush against a neighboring undug Rock tile with no gap, and
        // against a neighboring dug Floor tile it simply reads as "rock
        // behind the floor," which is exactly what it is.
        private const float RimWallTopY = 0.5f;
        private const float RimWallThickness = 0.1f;

        // Right at the tile's true edge (half a cell is 0.5) rather than
        // the fence's own inset — the wall and the low fence rail below
        // sit at slightly different depths along the same edge so their
        // faces don't coplanar-z-fight with each other.
        private const float RimWallEdgeInset = 0.49f;

        // Only the near-rim portion of the wall is ever actually visible
        // (see RimWallDepth's own comment) — that portion is built as a
        // stack of textured one-cell "blocks" (a black panel with a light
        // gray plus/cross centered on it, arms reaching to the middle of
        // each of the block's four sides — see BuildRimWallBlock).
        // Everything below that is one plain black filler panel instead
        // of tiling the same texture all the way down, since it's cheap
        // and nothing ever sees it.
        private const int RimWallTexturedBlockCount = 6;
        private const float RimWallCrossBarThickness = 0.09f;

        // How far the cross bars sit proud of the black panel behind
        // them, along the wall's own outward-facing axis — without this
        // they'd sit exactly coplanar with the panel and z-fight/flicker.
        private const float RimWallCrossReliefOffset = 0.02f;

        [SerializeField] private Color _rimWallColor = new Color(0.05f, 0.05f, 0.05f);
        [SerializeField] private Color _rimWallCrossColor = new Color(0.72f, 0.72f, 0.72f);

        // Low rim fence — short posts along every outward-facing edge of
        // the pit except the one gate tile/edge, grounded at ordinary
        // FloorSurfaceY (ground level) regardless of the pit's own sunk
        // floor, since it's a guard rail marking the rim, not something
        // that spans the pit's depth.
        [SerializeField] private Color _fenceColor = new Color(0.4f, 0.32f, 0.2f);
        private const float FenceRailHeight = 0.35f;
        private const float FenceRailThickness = 0.06f;
        private const float FenceEdgeInset = 0.42f;

        // Staircase + gate — one designated boundary tile (the south
        // edge's middle tile — always a real boundary edge regardless of
        // footprint size, since origin is the rectangle's min corner)
        // descends from ground level down to the pit floor, same
        // ascending-cube trick Portal.BuildStaircaseVisual uses for its
        // own staircase, just spanning PitDepth instead of Portal's
        // stylized rise. Flanked by two gate posts framing the opening.
        [SerializeField] private Color _stepColor = new Color(0.42f, 0.38f, 0.32f);
        [SerializeField] private Color _gatePostColor = new Color(0.3f, 0.26f, 0.2f);
        private const int StepCount = 3;
        private const float GatePostHeight = 0.6f;
        private const float GatePostRadius = 0.05f;

        // Dirt floor overlay — sits flush on top of DungeonGrid's own
        // (purple, HasRoom-colored) floor cube for every pit tile, same
        // "colored floor overlay flush on the base tile" convention
        // TrainingRoomManager/LibraryManager use for their own floor
        // colors (taller than DungeonGrid's own 0.15 floor height so its
        // top face wins the z-fight instead of flickering). Persists
        // across a merge same as the base pit sink does — only
        // fence/rim wall/gate get torn down and rebuilt for a bigger
        // shape.
        [SerializeField] private Color _dirtFloorColor = new Color(0.36f, 0.27f, 0.17f);
        private const float DirtFloorHeight = 0.17f;
        private const float DirtFloorFootprintScale = 0.95f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private BuilderJobBoard _jobBoard;
        private TreasuryManager _treasuryManager;
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();

        // Per-tile, built once when a tile first joins any Jail room and
        // never rebuilt after — the dirt floor doesn't depend on the
        // room's overall shape, unlike the rim structures below.
        private readonly Dictionary<Vector2Int, GameObject> _floorVisuals = new Dictionary<Vector2Int, GameObject>();

        // Per-room — fence rails, rim walls, and the one staircase/gate,
        // torn down and rebuilt in full every time this room's footprint
        // changes (initial placement or a merge-extension), since which
        // tiles are boundary vs. interior — and where the gate lands —
        // depends on the room's overall shape.
        private readonly Dictionary<string, List<GameObject>> _rimStructures = new Dictionary<string, List<GameObject>>();

        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, BuilderJobBoard jobBoard, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _jobBoard = jobBoard;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
        }

        /// Total tile count across every placed Jail — mirrors the
        /// TotalTileCount convention SlimeHatcheryManager/
        /// TrainingRoomManager/LibraryManager/LairManager all expose, for
        /// whatever future join requirement the Maze Rattler ends up
        /// needing.
        public int TotalTileCount
        {
            get
            {
                var total = 0;
                foreach (var tiles in _roomTiles.Values)
                {
                    total += tiles.Count;
                }
                return total;
            }
        }

        /// Places a Jail spanning the rectangle between startCoord and
        /// endCoord inclusive. Unlike every other room, footprint tiles
        /// don't need to already be dug — see CanPlaceFootprint. Fails
        /// atomically, same as every other room manager's Try* method.
        public bool TryPlaceJail(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceJailInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Jail exactly like TryPlaceJail but without charging
        /// gold — for terrain generation, not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury. Not currently called by
        /// GameBootstrap (no starting Jail exists yet), kept for parity
        /// with every other room manager's Place* pair.
        public bool PlaceStartingJail(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceJailInternal(startCoord, endCoord, chargeGold: false);
        }

        private bool TryPlaceJailInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
        {
            var footprint = GetFootprint(startCoord, endCoord, out var newWidth, out var newHeight, out var newOrigin);
            if (newWidth < MinFootprintSize || newHeight < MinFootprintSize || !CanPlaceFootprint(footprint))
            {
                return false;
            }

            if (chargeGold && _treasuryManager != null && !_treasuryManager.TrySpendGold(footprint.Count * CostPerTile))
            {
                return false;
            }

            string roomId;
            var origin = newOrigin;
            var width = newWidth;
            var height = newHeight;

            // Extends an existing Jail instead of starting a separate one
            // if footprint exactly completes a rectangle together with
            // it — see TryFindMergeableRoom. Only the fence/wall/gate need
            // rebuilding for the new shape; each tile's own pit sink,
            // once applied, is never undone by a merge (see IsPitTile).
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearRimStructures(roomId);
            }
            else
            {
                roomId = $"Jail_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
                _rimStructures[roomId] = new List<GameObject>();
            }

            foreach (var coord in footprint)
            {
                // Dig-and-claim on the spot rather than requiring a
                // pre-dug Claimed Floor — "not needed to be dug out,
                // placing the room tiles will do that." ApplyClaim (not
                // a bare DungeonGrid.ClaimTile) so the claim job
                // CompleteDig just queued (DungeonGrid.FloorNeedsClaim)
                // is cleared immediately too, instead of lingering as a
                // phantom already-satisfied job in BuilderJobBoard's
                // Tasks list.
                if (_grid.GetTile(coord).Type == TileType.Rock)
                {
                    _grid.CompleteDig(coord);
                    _jobBoard.ApplyClaim(coord);
                }

                _grid.TryAssignRoom(coord, roomId);
            }

            // Re-evaluated over every tile currently in the room, not
            // just the newly dragged footprint — a merge can turn a
            // former ring tile into a pit tile (see IsPitTile's own
            // comment), and this is the only place that needs to notice.
            // Guarded by _floorVisuals.ContainsKey so an already-pit tile
            // from an earlier placement is left untouched rather than
            // rebuilt.
            var allTiles = _roomTiles[roomId];
            var pitTiles = new HashSet<Vector2Int>();
            foreach (var coord in allTiles)
            {
                if (!IsPitTile(coord, origin, width, height))
                {
                    continue;
                }

                pitTiles.Add(coord);
                _grid.SetPitDepth(coord, PitDepth);
                if (!_floorVisuals.ContainsKey(coord))
                {
                    _floorVisuals[coord] = BuildDirtFloorVisual(coord);
                }
            }

            // The fence/rim wall/gate ring the pit's own boundary, not
            // the room's outer edge — a pit tile's neighbor is always
            // either another pit tile or one of this same room's ring
            // tiles (the pit is always inset exactly 1 tile from every
            // edge), never a tile outside the room.
            var pitOrigin = origin + Vector2Int.one;
            var pitWidth = width - 2;
            var gateCoord = pitOrigin + new Vector2Int(pitWidth / 2, 0);
            var rimStructures = _rimStructures[roomId];
            foreach (var coord in pitTiles)
            {
                BuildRimStructures(coord, pitTiles, gateCoord, rimStructures);
            }

            return true;
        }

        /// Whether coord sits strictly inside origin/width/height's
        /// rectangle — inset by exactly 1 tile from every edge, i.e. not
        /// on the room's own outer ring. A merge only ever grows the
        /// room's rectangle from wherever it already was, which can only
        /// ever grow or hold steady a given tile's margin to the
        /// rectangle's edges — so a tile already inside (pit) always
        /// stays inside, while a tile on the old ring can newly qualify
        /// as pit once the seam it sat on becomes interior to the bigger
        /// merged rectangle. Never the other direction.
        private static bool IsPitTile(Vector2Int coord, Vector2Int origin, int width, int height)
        {
            return coord.x > origin.x && coord.x < origin.x + width - 1
                && coord.y > origin.y && coord.y < origin.y + height - 1;
        }

        /// Whether footprint, combined with some single already-placed
        /// Jail, would exactly fill a rectangle — i.e. footprint extends
        /// an existing Jail rather than starting a fresh one. Same shape
        /// as TrainingRoomManager.TryFindMergeableRoom: only ever merges
        /// with one existing room at a time, and returns false (footprint
        /// becomes its own new room) if it doesn't cleanly complete a
        /// rectangle with any single one — including simply not being
        /// adjacent to one at all.
        private bool TryFindMergeableRoom(List<Vector2Int> footprint, out string roomId, out Vector2Int mergedOrigin, out int mergedWidth, out int mergedHeight)
        {
            foreach (var entry in _roomTiles)
            {
                var minX = int.MaxValue;
                var maxX = int.MinValue;
                var minY = int.MaxValue;
                var maxY = int.MinValue;

                foreach (var coord in entry.Value)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                foreach (var coord in footprint)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                var width = maxX - minX + 1;
                var height = maxY - minY + 1;

                if (width * height == entry.Value.Count + footprint.Count)
                {
                    roomId = entry.Key;
                    mergedOrigin = new Vector2Int(minX, minY);
                    mergedWidth = width;
                    mergedHeight = height;
                    return true;
                }
            }

            roomId = null;
            mergedOrigin = default;
            mergedWidth = 0;
            mergedHeight = 0;
            return false;
        }

        /// Tears down this room's current fence/wall/staircase/gate —
        /// used right before rebuilding them for a merged, bigger
        /// footprint.
        private void ClearRimStructures(string roomId)
        {
            if (!_rimStructures.TryGetValue(roomId, out var structures))
            {
                return;
            }

            foreach (var go in structures)
            {
                if (go != null)
                {
                    Destroy(go);
                }
            }
            structures.Clear();
        }

        /// Whether footprint could become a Jail right now. Unlike every
        /// other room's CanPlaceFootprint (which funnels entirely through
        /// DungeonGrid.CanBuildRoomOn), an undug Rock tile passes too — it
        /// gets dug and claimed as part of placement instead of needing
        /// to be pre-dug.
        private bool CanPlaceFootprint(List<Vector2Int> footprint)
        {
            foreach (var coord in footprint)
            {
                if (!_grid.InBounds(coord))
                {
                    return false;
                }

                var tile = _grid.GetTile(coord);
                if (tile.HasRoom)
                {
                    return false;
                }

                if (tile.Type == TileType.Rock)
                {
                    continue;
                }

                if (!_grid.CanBuildRoomOn(coord))
                {
                    return false;
                }
            }

            return true;
        }

        /// LairManager.RoomSold fires for every sold room — only react to
        /// our own (by roomId prefix, same convention every other room
        /// manager uses).
        private void OnRoomSold(string roomId)
        {
            if (!_roomTiles.TryGetValue(roomId, out var tiles))
            {
                return;
            }
            _roomTiles.Remove(roomId);

            foreach (var coord in tiles)
            {
                // TrySellRoom leaves the tile Claimed Floor, "ready to
                // build something else on" — reset the pit sink so
                // whatever gets built there next (or the bare floor
                // itself) doesn't inherit a leftover sunken tile.
                _grid.SetPitDepth(coord, 0f);

                if (_floorVisuals.TryGetValue(coord, out var floor) && floor != null)
                {
                    Destroy(floor);
                }
                _floorVisuals.Remove(coord);
            }

            ClearRimStructures(roomId);
            _rimStructures.Remove(roomId);
        }

        /// A flat dirt-colored slab sitting flush on top of DungeonGrid's
        /// own floor cube for coord (which stays the shared purple
        /// HasRoom color underneath, same as every other room) — grounded
        /// at the sunk pit level (FloorSurfaceY - PitDepth), not ordinary
        /// FloorSurfaceY, so it lands on the actual pit floor rather than
        /// floating at the surrounding ground's height.
        private GameObject BuildDirtFloorVisual(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * (0.5f + PitDepth);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = $"JailDirtFloor_{coord.x}_{coord.y}";
            floor.transform.SetParent(transform, false);
            floor.transform.position = basePosition;
            floor.transform.localScale = new Vector3(cellSize * DirtFloorFootprintScale, DirtFloorHeight, cellSize * DirtFloorFootprintScale);
            floor.GetComponent<Renderer>().material.color = _dirtFloorColor;
            Destroy(floor.GetComponent<Collider>());
            return floor;
        }

        /// Every outward-facing edge of coord (a pit tile) gets a rim wall
        /// (the "infinite blocks below" look) plus either a fence rail or
        /// — for the one designated gate edge — a staircase down into the
        /// pit flanked by gate posts instead of a fence. An edge whose
        /// neighbor is also a pit tile is an interior tile boundary, not
        /// a rim, so it gets nothing — two pit tiles sitting at the same
        /// depth have no seam to fill. Every other neighbor is one of
        /// this room's own ring tiles (the pit is always inset exactly 1
        /// tile from the room's outer edge), never a tile outside the
        /// room entirely.
        private void BuildRimStructures(Vector2Int coord, HashSet<Vector2Int> pitTiles, Vector2Int gateCoord, List<GameObject> structures)
        {
            foreach (var direction in GridDirections.Cardinal)
            {
                if (pitTiles.Contains(coord + direction))
                {
                    continue;
                }

                structures.Add(BuildRimWallVisual(coord, direction));

                if (coord == gateCoord && direction == Vector2Int.down)
                {
                    structures.Add(BuildStaircaseVisual(coord));
                    structures.Add(BuildGatePostsVisual(coord));
                    continue;
                }

                structures.Add(BuildFenceRailVisual(coord, direction));
            }
        }

        /// A stack of textured "blocks" standing right at one
        /// outward-facing edge of coord, from RimWallTopY down to
        /// RimWallDepth below — the "infinite blocks below the ground"
        /// look. Inset to the tile's true edge like the fence rail rather
        /// than covering the tile's own footprint, so it closes the
        /// vertical gap against a neighboring ground-level tile without
        /// ever sitting in front of (and hiding) the sunk floor the way a
        /// full-tile column would — see this class's own header comment
        /// for why that was a bug. Only the near-rim RimWallTexturedBlockCount
        /// blocks actually get the black-panel-with-a-gray-cross texture
        /// (see BuildRimWallBlock) — nothing below that is ever visible,
        /// so the remaining depth is one plain filler panel instead of
        /// tiling the same texture dozens of times over.
        private GameObject BuildRimWallVisual(Vector2Int coord, Vector2Int direction)
        {
            var container = new GameObject($"JailRimWall_{coord.x}_{coord.y}_{direction.x}_{direction.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var outward = new Vector3(direction.x, 0f, direction.y);
            var basePosition = worldPos + outward * (cellSize * RimWallEdgeInset);
            var isEastWestEdge = direction.x != 0;

            var texturedDepth = RimWallTexturedBlockCount * cellSize;
            for (int i = 0; i < RimWallTexturedBlockCount; i++)
            {
                var blockCenterY = RimWallTopY - cellSize * (i + 0.5f);
                BuildRimWallBlock(container.transform, basePosition, outward, blockCenterY, cellSize, isEastWestEdge);
            }

            var fillerDepth = RimWallDepth - texturedDepth;
            if (fillerDepth > 0f)
            {
                var fillerCenterY = RimWallTopY - texturedDepth - fillerDepth * 0.5f;
                var filler = GameObject.CreatePrimitive(PrimitiveType.Cube);
                filler.name = "Filler";
                filler.transform.SetParent(container.transform, false);
                filler.transform.position = new Vector3(basePosition.x, fillerCenterY, basePosition.z);
                filler.transform.localScale = isEastWestEdge
                    ? new Vector3(RimWallThickness, fillerDepth, cellSize * 0.98f)
                    : new Vector3(cellSize * 0.98f, fillerDepth, RimWallThickness);
                filler.GetComponent<Renderer>().material.color = _rimWallColor;
                Destroy(filler.GetComponent<Collider>());
            }

            return container;
        }

        /// One rim-wall "block": a black square panel one cell tall, with
        /// a light gray plus/cross centered on it — arms reaching to the
        /// middle of each of the block's four sides, i.e. each bar spans
        /// the block's full width (or height) rather than stopping short
        /// of the edges. The cross sits proud of the panel (offset
        /// further outward by RimWallCrossReliefOffset) so it never
        /// coplanar-z-fights with the panel behind it.
        private void BuildRimWallBlock(Transform parent, Vector3 basePosition, Vector3 outward, float centerY, float cellSize, bool isEastWestEdge)
        {
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(parent, false);
            panel.transform.position = new Vector3(basePosition.x, centerY, basePosition.z);
            panel.transform.localScale = isEastWestEdge
                ? new Vector3(RimWallThickness, cellSize * 0.96f, cellSize * 0.98f)
                : new Vector3(cellSize * 0.98f, cellSize * 0.96f, RimWallThickness);
            panel.GetComponent<Renderer>().material.color = _rimWallColor;
            Destroy(panel.GetComponent<Collider>());

            var crossPosition = new Vector3(basePosition.x, centerY, basePosition.z) + outward * RimWallCrossReliefOffset;

            var horizontalBar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            horizontalBar.name = "CrossHorizontal";
            horizontalBar.transform.SetParent(parent, false);
            horizontalBar.transform.position = crossPosition;
            horizontalBar.transform.localScale = isEastWestEdge
                ? new Vector3(RimWallCrossBarThickness, RimWallCrossBarThickness, cellSize)
                : new Vector3(cellSize, RimWallCrossBarThickness, RimWallCrossBarThickness);
            horizontalBar.GetComponent<Renderer>().material.color = _rimWallCrossColor;
            Destroy(horizontalBar.GetComponent<Collider>());

            var verticalBar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            verticalBar.name = "CrossVertical";
            verticalBar.transform.SetParent(parent, false);
            verticalBar.transform.position = crossPosition;
            verticalBar.transform.localScale = new Vector3(RimWallCrossBarThickness, cellSize, RimWallCrossBarThickness);
            verticalBar.GetComponent<Renderer>().material.color = _rimWallCrossColor;
            Destroy(verticalBar.GetComponent<Collider>());
        }

        /// One low fence rail along a single outward-facing edge of coord
        /// — grounded at ordinary FloorSurfaceY (ground level) regardless
        /// of how deep the pit itself sits, since it's a rim guard rail,
        /// not a wall spanning the drop.
        private GameObject BuildFenceRailVisual(Vector2Int coord, Vector2Int direction)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var edgeOffset = new Vector3(direction.x, 0f, direction.y) * (cellSize * FenceEdgeInset);
            var basePosition = worldPos + edgeOffset;
            var groundY = _grid.FloorSurfaceY;

            var rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rail.name = $"JailFence_{coord.x}_{coord.y}_{direction.x}_{direction.y}";
            rail.transform.SetParent(transform, false);
            rail.transform.position = new Vector3(basePosition.x, groundY + FenceRailHeight * 0.5f, basePosition.z);

            // A rail runs along the edge, perpendicular to `direction` —
            // an east/west-facing edge (direction along X) is long in Z,
            // a north/south-facing edge (direction along Z) is long in X.
            var isEastWestEdge = direction.x != 0;
            rail.transform.localScale = isEastWestEdge
                ? new Vector3(FenceRailThickness, FenceRailHeight, cellSize * 0.9f)
                : new Vector3(cellSize * 0.9f, FenceRailHeight, FenceRailThickness);
            rail.GetComponent<Renderer>().material.color = _fenceColor;
            Destroy(rail.GetComponent<Collider>());
            return rail;
        }

        /// Three ascending step cubes from the pit floor up to ground
        /// level, running along the tile's south (Vector2Int.down) edge —
        /// the one fixed direction BuildRimStructures ever calls this
        /// for.
        private GameObject BuildStaircaseVisual(Vector2Int coord)
        {
            var container = new GameObject($"JailStair_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            container.transform.position = _grid.GridToWorld(coord);

            var cellSize = _grid.CellSize;
            var pitFloorY = _grid.FloorSurfaceY - PitDepth;
            var stepDepth = cellSize / StepCount;

            for (int i = 0; i < StepCount; i++)
            {
                // i=0 is innermost/deepest (pit floor level, toward the
                // room's interior), i=StepCount-1 is outermost/highest
                // (ground level, at the south/gate edge).
                var stepTopY = pitFloorY + PitDepth * (i + 1) / (float)StepCount;
                var stepHeight = stepTopY - pitFloorY;
                var offsetZ = (i - (StepCount - 1) / 2f) * -stepDepth;

                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"JailStep_{i}";
                step.transform.SetParent(container.transform, false);
                step.transform.localPosition = new Vector3(0f, pitFloorY + stepHeight * 0.5f, offsetZ);
                step.transform.localScale = new Vector3(cellSize * 0.9f, stepHeight, stepDepth * 0.95f);
                step.GetComponent<Renderer>().material.color = _stepColor;
                Destroy(step.GetComponent<Collider>());
            }

            return container;
        }

        /// Two posts framing the staircase opening at coord's south edge
        /// — the "gate" half of "one staircase with a gate." Cosmetic
        /// only, same as the staircase itself: the tile stays fully
        /// walkable underneath.
        private GameObject BuildGatePostsVisual(Vector2Int coord)
        {
            var container = new GameObject($"JailGate_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var edgeZ = worldPos.z - cellSize * FenceEdgeInset;
            var groundY = _grid.FloorSurfaceY;

            foreach (var side in new[] { -1f, 1f })
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = $"JailGatePost_{side}";
                post.transform.SetParent(container.transform, false);
                post.transform.position = new Vector3(worldPos.x + side * cellSize * 0.4f, groundY + GatePostHeight * 0.5f, edgeZ);
                // Cylinder primitive is 2 units tall at scale 1, so the
                // height scale needed for a world-space length is half
                // that length (same trick TrainingRoomManager.
                // BuildDummyVisual's post uses).
                post.transform.localScale = new Vector3(GatePostRadius * 2f, GatePostHeight * 0.5f, GatePostRadius * 2f);
                post.GetComponent<Renderer>().material.color = _gatePostColor;
                Destroy(post.GetComponent<Collider>());
            }

            return container;
        }

        public void UpdatePlacementPreview(Vector2Int startCoord, Vector2Int endCoord)
        {
            ClearPlacementPreview();

            var footprint = GetFootprint(startCoord, endCoord, out var width, out var height, out _);
            var isValidSize = width >= MinFootprintSize && height >= MinFootprintSize;
            var color = isValidSize && CanPlaceFootprint(footprint) ? _previewValidColor : _previewInvalidColor;

            foreach (var coord in footprint)
            {
                if (!_grid.InBounds(coord))
                {
                    continue;
                }

                _previewMarkers.Add(CreatePreviewMarker(coord, color));
            }
        }

        public void ClearPlacementPreview()
        {
            foreach (var marker in _previewMarkers)
            {
                if (marker != null)
                {
                    Destroy(marker);
                }
            }
            _previewMarkers.Clear();
        }

        private GameObject CreatePreviewMarker(Vector2Int coord, Color color)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var centerY = GetGroundTopY(coord) + PreviewClearance + PreviewHeight * 0.5f;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"JailPreview_{coord.x}_{coord.y}";
            marker.transform.SetParent(transform, false);
            marker.transform.localPosition = new Vector3(worldPos.x, centerY, worldPos.z);
            marker.transform.localScale = new Vector3(cellSize * PreviewFootprintScale, PreviewHeight, cellSize * PreviewFootprintScale);
            marker.GetComponent<Renderer>().material.color = color;
            Destroy(marker.GetComponent<Collider>());
            return marker;
        }

        private float GetGroundTopY(Vector2Int coord)
        {
            return _grid.GetTile(coord).Type == TileType.Rock ? RockTopY : _grid.FloorSurfaceY;
        }

        private List<Vector2Int> GetFootprint(Vector2Int startCoord, Vector2Int endCoord, out int width, out int height, out Vector2Int origin)
        {
            var minX = Mathf.Min(startCoord.x, endCoord.x);
            var maxX = Mathf.Max(startCoord.x, endCoord.x);
            var minY = Mathf.Min(startCoord.y, endCoord.y);
            var maxY = Mathf.Max(startCoord.y, endCoord.y);

            width = maxX - minX + 1;
            height = maxY - minY + 1;
            origin = new Vector2Int(minX, minY);

            var footprint = new List<Vector2Int>(width * height);
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    footprint.Add(new Vector2Int(x, y));
                }
            }

            return footprint;
        }
    }
}
