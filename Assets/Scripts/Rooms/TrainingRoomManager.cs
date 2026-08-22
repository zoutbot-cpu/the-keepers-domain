using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Where non-Imp units train to gain exp, per the design doc — Imps get
    /// their exp from mining instead (see ImplingAgent), so this room has no
    /// effect on them. Placed the same drag-a-footprint way a Lair/Treasury
    /// is (see TryPlaceTrainingRoom), with no stated minimum size (like
    /// Lair/Treasury, unlike the Hatchery/Beacon's hard minimums). Training
    /// dummies are blocked to pathfinding, same as Library's bookcases — a
    /// training creature (see GremlinAgent/WarlockAgent) stands on one of
    /// the tiles cardinally adjacent to a dummy, walking between different
    /// dummies' adjacent tiles rather than standing in one spot the whole
    /// time (see TryFindNearestDummyTile/TryFindRandomDummyTile).
    public class TrainingRoomManager : MonoBehaviour
    {
        /// Gold cost per tile of a placed Training Room — charged out of
        /// TreasuryManager's reserves, same as Hatchery/Beacon/Lair.
        public const int CostPerTile = 20;

        /// Training now actually runs (see GremlinAgent/WarlockAgent) — a
        /// creature standing on any Training Room tile gains this much exp
        /// every TrainingTickSeconds, per the numbers this class originally
        /// documented before the behavior existed.
        public const int TrainingExpPerTick = 20;
        public const float TrainingTickSeconds = 2f;

        // Dummy structures — a brick pattern, see GetStructureCoords for
        // the full placement rule (e.g. 1 at 3x3, 4 at 5x5, 6 at 7x5).

        // Ground overlay on every footprint tile (including the structure's
        // own tile): a thin dark-green border with a slightly lighter green
        // fill in the middle — same border/fill grammar TreasuryManager's
        // gold tiles use, just with a wider fill fraction so the border
        // reads as thin rather than Treasury's thicker ring. Taller than
        // DungeonGrid's own 0.15 floor visual so its top face wins the
        // z-fight instead of flickering; the fill sits a further margin
        // above the border for the same reason (see TreasuryManager's
        // FillHeightMargin).
        [SerializeField] private Color _groundBorderColor = new Color(0.1f, 0.35f, 0.1f);
        [SerializeField] private Color _groundFillColor = new Color(0.35f, 0.65f, 0.35f);
        private const float GroundTileHeight = 0.17f;
        private const float GroundFillHeightMargin = 0.03f;
        private const float GroundFootprintScale = 0.95f;
        private const float GroundFillFootprintScale = 0.8f;

        // Training dummy: a wooden post with a crossbar (arms) partway up
        // and a round head on top — "a stick cross with a head" per the
        // brief, standing in for a real dummy model until one exists.
        [SerializeField] private Color _postColor = new Color(0.4f, 0.27f, 0.15f);
        [SerializeField] private Color _headColor = new Color(0.75f, 0.65f, 0.4f);
        private const float PostRadius = 0.06f;
        private const float PostHeight = 0.9f;
        private const float CrossbarRadius = 0.055f;
        private const float CrossbarLength = 0.55f;
        private const float CrossbarHeightFraction = 0.62f;
        private const float HeadRadius = 0.17f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, List<Vector2Int>> _structureCoords = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, List<Vector2Int>> _dummyAdjacentTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<Vector2Int, GameObject> _structureVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, GameObject> _groundVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
        }

        /// Total tile count across every placed Training Room — read by
        /// GremlinSpawner as one of a Gremlin's join requirements ("at
        /// least 9 tiles of Training Room").
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

        /// Places a Training Room spanning the rectangle between startCoord
        /// and endCoord inclusive. Fails atomically, same as
        /// LairManager.TryPlaceLair — no minimum footprint size, unlike the
        /// Hatchery/Beacon.
        public bool TryPlaceTrainingRoom(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceTrainingRoomInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Training Room exactly like TryPlaceTrainingRoom but
        /// without charging gold — for terrain generation (see
        /// GameBootstrap's starting domain layout), not a player purchase,
        /// same as TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingTrainingRoom(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceTrainingRoomInternal(startCoord, endCoord, chargeGold: false);
        }

        private bool TryPlaceTrainingRoomInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
        {
            var footprint = GetFootprint(startCoord, endCoord, out var newWidth, out var newHeight, out var newOrigin);
            if (!CanPlaceFootprint(footprint))
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

            // Extends an existing Training Room instead of starting a
            // separate one if footprint exactly completes a rectangle
            // together with it — see TryFindMergeableRoom. The old dummy is
            // torn down and rebuilt below at the recentered spot for the
            // room's new overall shape.
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearStructure(roomId);
            }
            else
            {
                roomId = $"TrainingRoom_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
            }

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                _groundVisuals[coord] = BuildGroundVisual(coord);
            }

            var structureCoords = GetStructureCoords(origin, width, height);
            _structureCoords[roomId] = structureCoords;
            foreach (var structureCoord in structureCoords)
            {
                _structureVisuals[structureCoord] = BuildDummyVisual(structureCoord);
                _grid.SetBlocked(structureCoord, true);
            }

            _dummyAdjacentTiles[roomId] = FindDummyAdjacentTiles(_roomTiles[roomId], structureCoords);

            return true;
        }

        /// Whether footprint, combined with some single already-placed room
        /// of this type, would exactly fill a rectangle — i.e. footprint
        /// extends an existing room rather than starting a fresh one. Only
        /// ever merges with one existing room at a time; if footprint
        /// doesn't cleanly complete a rectangle with any single existing
        /// room (including the common case of not being adjacent to one at
        /// all), this returns false and the caller places footprint as its
        /// own new room instead.
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

        /// Tears down this room's current dummy structures — used right
        /// before rebuilding them at the recomputed spots for a new
        /// (possibly merged, larger) footprint. Unblocks each old dummy
        /// tile too, since TryPlaceTrainingRoomInternal always re-blocks
        /// whatever the rebuilt structureCoords turn out to be right after
        /// calling this.
        private void ClearStructure(string roomId)
        {
            if (!_structureCoords.TryGetValue(roomId, out var coords))
            {
                return;
            }

            foreach (var coord in coords)
            {
                if (_structureVisuals.TryGetValue(coord, out var visual) && visual != null)
                {
                    Destroy(visual);
                }
                _structureVisuals.Remove(coord);
                _grid.SetBlocked(coord, false);
            }
            _structureCoords.Remove(roomId);
            _dummyAdjacentTiles.Remove(roomId);
        }

        /// Every training-dummy position for a room of this size — a brick
        /// pattern, reverse-engineered from exact examples the user drew on
        /// a grid (see chat history), not derivable from the original brief
        /// text alone. Rows (Y) come from GetRowPositions; each row's
        /// columns (X) alternate between GetColumnsNear/GetColumnsFar,
        /// starting with Far on the bottom row — e.g. a 6x4 room's two rows
        /// (y=1,2) use columns {2,4} then {1,3}, covering every interior
        /// column between the two rows instead of repeating the same
        /// columns on both. When a room's width can't tell those two column
        /// sets apart (GetColumnsNear == GetColumnsFar — true for any odd
        /// width, or width 3), adjacent rows would end up with identical,
        /// touching columns; the earlier of that pair is dropped, keeping
        /// only the later (farther) row — see the 3x4 example, which keeps
        /// just its top row.
        private List<Vector2Int> GetStructureCoords(Vector2Int origin, int width, int height)
        {
            var result = new List<Vector2Int>();

            var rows = GetRowPositions(height);
            var columnsNear = GetColumnsNear(width);
            var columnsFar = GetColumnsFar(width);
            if (rows.Count == 0 || columnsNear.Count == 0)
            {
                return result;
            }

            var columnsAreDistinct = !AreEqual(columnsNear, columnsFar);

            for (int i = 0; i < rows.Count; i++)
            {
                // Drop this row if it's 1 tile from the next one and the
                // columns can't alternate to avoid a touch — the next
                // (farther) row wins instead of this one.
                if (!columnsAreDistinct && i < rows.Count - 1 && rows[i + 1] - rows[i] == 1)
                {
                    continue;
                }

                var columns = (i % 2 == 0) ? columnsFar : columnsNear;
                foreach (var x in columns)
                {
                    result.Add(origin + new Vector2Int(x, rows[i]));
                }
            }

            return result;
        }

        /// Row (Y) positions for a room this tall — 1 (near edge) and
        /// height-2 (far edge) are always both kept once they're distinct,
        /// then the same "step 2 in from each end" repeats inward until the
        /// two ends meet or cross, landing on the exact middle row when
        /// height is odd. E.g. height 4 -> {1,2} (adjacent — see
        /// GetStructureCoords for how that's handled), height 6 -> {1,4},
        /// height 7 -> {1,3,5}.
        private static List<int> GetRowPositions(int height)
        {
            var rows = new List<int>();
            if (height < 3)
            {
                return rows;
            }

            var low = 1;
            var high = height - 2;
            while (low < high)
            {
                rows.Add(low);
                rows.Add(high);
                low += 2;
                high -= 2;
            }
            if (low == high)
            {
                rows.Add(low);
            }

            rows.Sort();
            return rows;
        }

        /// Column (X) positions for a row anchored to the near (left) edge:
        /// 1, 3, 5, ... up to width-2. See GetColumnsFar for the
        /// complementary near-the-far-edge anchoring an adjacent row
        /// alternates to.
        private static List<int> GetColumnsNear(int width)
        {
            var columns = new List<int>();
            for (int x = 1; x <= width - 2; x += 2)
            {
                columns.Add(x);
            }

            return columns;
        }

        /// Column (X) positions for a row anchored to the far (right) edge:
        /// width-2, width-4, ... down to 1. Identical to GetColumnsNear
        /// when width is odd (both ends land on the same tiles) or width is
        /// under 4 (only one interior column exists at all) — only an even
        /// width 4 or greater gives two genuinely different column sets.
        private static List<int> GetColumnsFar(int width)
        {
            var columns = new List<int>();
            for (int x = width - 2; x >= 1; x -= 2)
            {
                columns.Add(x);
            }

            columns.Sort();
            return columns;
        }

        /// Every room tile that isn't itself a dummy but is cardinally next
        /// to one — where a training creature actually stops (see
        /// TryFindNearestDummyTile/TryFindRandomDummyTile), since dummy
        /// tiles are blocked to pathfinding (see TryPlaceTrainingRoomInternal)
        /// same as LibraryManager.FindBookcaseAdjacentTiles does for
        /// bookcases.
        private static List<Vector2Int> FindDummyAdjacentTiles(List<Vector2Int> roomTiles, List<Vector2Int> structureCoords)
        {
            var structureSet = new HashSet<Vector2Int>(structureCoords);
            var adjacent = new List<Vector2Int>();

            foreach (var coord in roomTiles)
            {
                if (structureSet.Contains(coord))
                {
                    continue;
                }

                foreach (var direction in GridDirections.Cardinal)
                {
                    if (structureSet.Contains(coord + direction))
                    {
                        adjacent.Add(coord);
                        break;
                    }
                }
            }

            return adjacent;
        }

        private static bool AreEqual(List<int> a, List<int> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// Nearest (by walking distance) tile actually adjacent to a dummy
        /// structure, reachable from fromCoord — same shape as
        /// LibraryManager.TryFindNearestBookcaseTile. Dummy tiles
        /// themselves are blocked to pathfinding (see
        /// TryPlaceTrainingRoomInternal), so a training creature always
        /// stands one tile off to the side rather than on top of one.
        public bool TryFindNearestDummyTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var tiles in _dummyAdjacentTiles.Values)
            {
                foreach (var coord in tiles)
                {
                    if (distances.TryGetValue(coord, out var distance) && distance < bestDistance)
                    {
                        bestDistance = distance;
                        targetCoord = coord;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// A random reachable dummy-adjacent tile other than excludeCoord —
        /// read by GremlinAgent/WarlockAgent once a training pause ends,
        /// same "stopping for a few seconds, then moving on to another"
        /// idea LibraryManager.TryFindRandomBookcaseTile drives for
        /// research.
        public bool TryFindRandomDummyTile(Vector2Int fromCoord, Vector2Int excludeCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();

            foreach (var tiles in _dummyAdjacentTiles.Values)
            {
                foreach (var coord in tiles)
                {
                    if (coord != excludeCoord && distances.ContainsKey(coord))
                    {
                        candidates.Add(coord);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                targetCoord = default;
                return false;
            }

            targetCoord = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            return true;
        }

        /// Whether coord belongs to any placed Training Room — read by
        /// MinionGrabController to detect a "throw a creature onto the
        /// Training Room" drop (see its own DropAt), scoping
        /// DungeonGrid's generic per-tile HasRoom/RoomId down to this one
        /// room type specifically.
        public bool IsTrainingRoomTile(Vector2Int coord)
        {
            var tile = _grid.GetTile(coord);
            return tile.HasRoom && _roomTiles.ContainsKey(tile.RoomId);
        }

        public void UpdatePlacementPreview(Vector2Int startCoord, Vector2Int endCoord)
        {
            ClearPlacementPreview();

            var footprint = GetFootprint(startCoord, endCoord, out _, out _, out _);
            var color = CanPlaceFootprint(footprint) ? _previewValidColor : _previewInvalidColor;

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

        /// LairManager.RoomSold fires for every sold room — only react to
        /// our own (by roomId prefix, same convention TreasuryManager uses).
        private void OnRoomSold(string roomId)
        {
            if (!_roomTiles.TryGetValue(roomId, out var tiles))
            {
                return;
            }
            _roomTiles.Remove(roomId);

            foreach (var coord in tiles)
            {
                if (_groundVisuals.TryGetValue(coord, out var groundVisual) && groundVisual != null)
                {
                    Destroy(groundVisual);
                }
                _groundVisuals.Remove(coord);
            }

            if (_structureCoords.TryGetValue(roomId, out var structureCoords))
            {
                foreach (var structureCoord in structureCoords)
                {
                    if (_structureVisuals.TryGetValue(structureCoord, out var visual) && visual != null)
                    {
                        Destroy(visual);
                    }
                    _structureVisuals.Remove(structureCoord);
                    _grid.SetBlocked(structureCoord, false);
                }
                _structureCoords.Remove(roomId);
            }

            _dummyAdjacentTiles.Remove(roomId);
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

        private bool CanPlaceFootprint(List<Vector2Int> footprint)
        {
            foreach (var coord in footprint)
            {
                if (!_grid.CanBuildRoomOn(coord))
                {
                    return false;
                }
            }

            return true;
        }

        private GameObject CreatePreviewMarker(Vector2Int coord, Color color)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var centerY = GetGroundTopY(coord) + PreviewClearance + PreviewHeight * 0.5f;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"TrainingRoomPreview_{coord.x}_{coord.y}";
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

        /// Thin dark-green border with a slightly lighter green fill in the
        /// middle — same border/fill footprint convention
        /// TreasuryManager.CreateTileVisual uses for its gold tiles.
        private GameObject BuildGroundVisual(Vector2Int coord)
        {
            var container = new GameObject($"TrainingRoomGround_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(container.transform, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * GroundFootprintScale, GroundTileHeight, cellSize * GroundFootprintScale);
            border.GetComponent<Renderer>().material.color = _groundBorderColor;
            Destroy(border.GetComponent<Collider>());

            var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Fill";
            fill.transform.SetParent(container.transform, false);
            // Raised by half the margin so its bottom still matches the
            // border's (no gap underneath) while its top clears the
            // border's top — see GroundFillHeightMargin.
            fill.transform.position = basePosition + Vector3.up * (GroundFillHeightMargin * 0.5f);
            fill.transform.localScale = new Vector3(cellSize * GroundFillFootprintScale, GroundTileHeight + GroundFillHeightMargin, cellSize * GroundFillFootprintScale);
            fill.GetComponent<Renderer>().material.color = _groundFillColor;
            Destroy(fill.GetComponent<Collider>());

            return container;
        }

        /// Placeholder training dummy: a vertical post, a horizontal
        /// crossbar partway up (the "cross"), and a sphere head on top.
        private GameObject BuildDummyVisual(Vector2Int coord)
        {
            var container = new GameObject($"TrainingDummy_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var worldPos = _grid.GridToWorld(coord);
            var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);

            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            post.transform.SetParent(container.transform, false);
            post.transform.position = basePosition + Vector3.up * (PostHeight * 0.5f);
            // Cylinder primitive is 2 units tall at scale 1, so the height
            // scale needed for a world-space length is half that length.
            post.transform.localScale = new Vector3(PostRadius * 2f, PostHeight * 0.5f, PostRadius * 2f);
            post.GetComponent<Renderer>().material.color = _postColor;
            Destroy(post.GetComponent<Collider>());

            var crossbar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            crossbar.name = "Crossbar";
            crossbar.transform.SetParent(container.transform, false);
            crossbar.transform.position = basePosition + Vector3.up * (PostHeight * CrossbarHeightFraction);
            crossbar.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            crossbar.transform.localScale = new Vector3(CrossbarRadius * 2f, CrossbarLength * 0.5f, CrossbarRadius * 2f);
            crossbar.GetComponent<Renderer>().material.color = _postColor;
            Destroy(crossbar.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(container.transform, false);
            head.transform.position = basePosition + Vector3.up * (PostHeight + HeadRadius);
            head.transform.localScale = Vector3.one * (HeadRadius * 2f);
            head.GetComponent<Renderer>().material.color = _headColor;
            Destroy(head.GetComponent<Collider>());

            return container;
        }
    }
}
