using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Converts hauled-in slimes into bacon at a 2x2 shrine — "food for
    /// intelligent creatures" per the design doc. Placed the same
    /// drag-a-footprint way a Lair/Treasury is (see TryPlaceBeacon), with a
    /// hard 4x4 minimum enforced like SlimeHatcheryManager's 3x3 one. Bacon
    /// is stored on whichever footprint tiles are actually adjacent to the
    /// shrine block (see GetStorageTiles) — for the minimum 4x4 room that's
    /// every other tile, but a bigger room's far corners wouldn't store
    /// anything, "so implings aren't overtasked" per the design doc.
    public class BaconBeaconManager : MonoBehaviour
    {
        private const int MinFootprintSize = 4;

        /// Gold cost per tile of a placed Bacon Beacon — charged out of
        /// TreasuryManager's reserves (see TryPlaceBeacon).
        public const int CostPerTile = 15;

        /// The shrine is a 2x2 block. Same "true center" rule as
        /// SlimeHatcheryManager's single-tile coop, generalized to a 2x2:
        /// centered only when (width - 2) and (height - 2) are both even
        /// (so a symmetric 2x2 middle exists), otherwise one tile in from
        /// the top-right corner on each axis.
        private const int StructureSize = 2;

        private const int BaconPerSlime = 4;
        private const int BaconCapacityPerTile = 12;

        /// Bacon consumed by a hungry Gremlin/Warlock eating a single meal
        /// (see GremlinAgent/WarlockAgent's priority-80 check) — placeholder
        /// tunable, not specified by the design brief beyond "go eat bacon."
        public const int MealBaconAmount = 1;

        // Shrine visual: a raised stone dais under two "tubes" — a bright
        // one going up (slimes descend to be judged) and a dark one going
        // down (bacon rises back out) — distinct from SlimeHatchery's coop
        // crate and Treasury's flat gold tiles.
        [SerializeField] private Color _daisColor = new Color(0.4f, 0.38f, 0.42f);
        [SerializeField] private Color _tubeUpColor = new Color(0.9f, 0.75f, 0.25f);
        [SerializeField] private Color _tubeDownColor = new Color(0.5f, 0.2f, 0.2f);
        private const float DaisHeight = 0.25f;
        private const float DaisFootprintScale = 0.9f;
        private const float TubeRadius = 0.18f;
        private const float TubeHeight = 0.9f;

        // Storage-tile visual: a gray fill on a red-brown border, same
        // border/fill grammar TreasuryManager's gold tiles use.
        [SerializeField] private Color _storageBorderColor = new Color(0.55f, 0.25f, 0.2f);
        [SerializeField] private Color _storageFillColor = new Color(0.5f, 0.5f, 0.5f);
        private const float StorageFillFootprintScale = 0.65f;
        private const float StorageTileHeight = 0.17f;
        private const float StorageFillHeightMargin = 0.03f;
        private const float LabelSurfaceOffset = 0.01f;
        private const float LabelCharacterSize = 0.1f;
        private const int LabelFontSize = 24;

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
        private readonly Dictionary<string, GameObject> _shrineVisuals = new Dictionary<string, GameObject>();
        private readonly List<Vector2Int> _storageTiles = new List<Vector2Int>();
        private readonly Dictionary<Vector2Int, int> _storedBacon = new Dictionary<Vector2Int, int>();
        private readonly Dictionary<Vector2Int, TextMesh> _labels = new Dictionary<Vector2Int, TextMesh>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, string> _storageRoomByCoord = new Dictionary<Vector2Int, string>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        /// Bacon in reserves — summed across every registered storage tile,
        /// same shape as TreasuryManager.TotalGold. Read by BottomMenuBar's
        /// top-bar counter.
        public int TotalBacon
        {
            get
            {
                var total = 0;
                foreach (var amount in _storedBacon.Values)
                {
                    total += amount;
                }
                return total;
            }
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
        }

        /// Total tile count across every placed Bacon Beacon (not just its
        /// shrine-adjacent storage tiles) — read by WarlockSpawner as one of
        /// a Warlock's join requirements ("at least one bacon beacon tile
        /// for each intelligent creature"), same TotalTileCount convention
        /// SlimeHatcheryManager/TrainingRoomManager/LibraryManager use.
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

        /// Places a Bacon Beacon spanning the rectangle between startCoord
        /// and endCoord inclusive. Fails atomically, same as
        /// SlimeHatcheryManager.TryPlaceHatchery, with its own
        /// MinFootprintSize floor.
        public bool TryPlaceBeacon(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceBeaconInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Bacon Beacon exactly like TryPlaceBeacon but without
        /// charging gold — for terrain generation (see GameBootstrap's
        /// starting domain layout), not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingBeacon(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceBeaconInternal(startCoord, endCoord, chargeGold: false);
        }

        private bool TryPlaceBeaconInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
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

            // Extends an existing Bacon Beacon instead of starting a
            // separate one if footprint exactly completes a rectangle
            // together with it — see TryFindMergeableRoom. The shrine
            // recenters for the room's new overall shape, which can change
            // which tiles even count as storage (adjacent to the shrine) —
            // see ClearShrineAndStorage — so every storage tile (old and
            // new) is recomputed from scratch below rather than only
            // registering the newly-dragged ones.
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearShrineAndStorage(roomId);
            }
            else
            {
                roomId = $"BaconBeacon_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
            }

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
            }

            var shrineTiles = GetShrineTiles(origin, width, height);
            _shrineVisuals[roomId] = BuildShrineVisual(shrineTiles);

            foreach (var coord in _roomTiles[roomId])
            {
                if (shrineTiles.Contains(coord) || !IsAdjacentToShrine(coord, shrineTiles))
                {
                    continue;
                }

                RegisterStorageTile(coord, roomId);
            }

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

        /// Tears down this room's current shrine and every one of its
        /// storage tiles — used right before rebuilding both from scratch
        /// for a new (possibly merged, larger) footprint, since the
        /// shrine's recentered position can change which tiles count as
        /// storage at all. Storage tiles are tracked per-coordinate, not
        /// nested per-room (unlike _roomTiles), so this finds this room's
        /// own tiles among them via _storageRoomByCoord, the same way
        /// OnRoomSold does. Whatever bacon was stored on them is simply
        /// lost, same as OnRoomSold's own "stored bacon is simply gone"
        /// behavior.
        private void ClearShrineAndStorage(string roomId)
        {
            if (_shrineVisuals.TryGetValue(roomId, out var shrine) && shrine != null)
            {
                Destroy(shrine);
            }
            _shrineVisuals.Remove(roomId);

            var toRemove = new List<Vector2Int>();
            foreach (var coord in _storageTiles)
            {
                if (_storageRoomByCoord.TryGetValue(coord, out var owner) && owner == roomId)
                {
                    toRemove.Add(coord);
                }
            }

            foreach (var coord in toRemove)
            {
                if (_tileVisuals.TryGetValue(coord, out var visual) && visual != null)
                {
                    Destroy(visual);
                }
                _tileVisuals.Remove(coord);

                if (_labels.TryGetValue(coord, out var label) && label != null)
                {
                    Destroy(label.gameObject);
                }
                _labels.Remove(coord);

                _storedBacon.Remove(coord);
                _storageRoomByCoord.Remove(coord);
                _storageTiles.Remove(coord);
            }
        }

        /// The shrine's 2x2 block — centered when both dimensions allow a
        /// symmetric middle, otherwise one tile in from the top-right
        /// corner on each axis (same fallback SlimeHatcheryManager uses for
        /// its single-tile coop).
        private List<Vector2Int> GetShrineTiles(Vector2Int origin, int width, int height)
        {
            var hasCenter = (width - StructureSize) % 2 == 0 && (height - StructureSize) % 2 == 0;
            var blockOrigin = hasCenter
                ? origin + new Vector2Int((width - StructureSize) / 2, (height - StructureSize) / 2)
                : origin + new Vector2Int(width - 1 - StructureSize, height - 1 - StructureSize);

            var tiles = new List<Vector2Int>(StructureSize * StructureSize);
            for (int x = 0; x < StructureSize; x++)
            {
                for (int y = 0; y < StructureSize; y++)
                {
                    tiles.Add(blockOrigin + new Vector2Int(x, y));
                }
            }

            return tiles;
        }

        /// 8-directional adjacency to any shrine tile — for the minimum 4x4
        /// room this covers every non-shrine tile (all 12 of them), which
        /// is what makes "12 bacon per adjacent tile" line up with the
        /// design doc's numbers; a bigger room's far corners fall outside
        /// this and never become storage tiles.
        private static bool IsAdjacentToShrine(Vector2Int coord, List<Vector2Int> shrineTiles)
        {
            foreach (var shrineTile in shrineTiles)
            {
                if (Mathf.Abs(coord.x - shrineTile.x) <= 1 && Mathf.Abs(coord.y - shrineTile.y) <= 1)
                {
                    return true;
                }
            }

            return false;
        }

        private void RegisterStorageTile(Vector2Int coord, string roomId)
        {
            _storageTiles.Add(coord);
            _storedBacon[coord] = 0;
            _storageRoomByCoord[coord] = roomId;
            _tileVisuals[coord] = CreateStorageTileVisual(coord);
            _labels[coord] = CreateLabel(coord);
        }

        /// Converts up to slimeAmount slimes into bacon at the storage tile
        /// coord, BaconPerSlime each, capped by that tile's remaining
        /// capacity — only whole slimes are converted, so a slime that
        /// wouldn't fully fit is left uncarried for a later trip rather
        /// than partially converted. Returns how many slimes were actually
        /// consumed, mirroring TreasuryManager.Deposit's "return what
        /// happened, caller keeps the rest" shape.
        public int ConvertSlimes(Vector2Int coord, int slimeAmount)
        {
            if (slimeAmount <= 0 || !_storedBacon.TryGetValue(coord, out var current))
            {
                return 0;
            }

            var remainingCapacity = BaconCapacityPerTile - current;
            var convertible = Mathf.Min(slimeAmount, remainingCapacity / BaconPerSlime);
            if (convertible <= 0)
            {
                return 0;
            }

            _storedBacon[coord] = current + convertible * BaconPerSlime;
            _labels[coord].text = _storedBacon[coord].ToString();
            return convertible;
        }

        /// Nearest (by walking distance) storage tile with room for at
        /// least one more slime's worth of bacon, reachable from fromCoord
        /// — same shape as TreasuryManager.TryFindNearestTileWithRoom.
        public bool TryFindNearestTileWithRoom(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            // Impling-only (slime deposit) — Imps need a Bridge to cross
            // Water/Lava, see DungeonGrid.IsWalkable. Not shared with
            // TryFindNearestTileWithBacon below, which is the eating query
            // every other creature type uses and stays default (non-Imp).
            var distances = _grid.GetReachableFloorDistances(fromCoord, isImp: true);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var coord in _storageTiles)
            {
                if (_storedBacon[coord] > BaconCapacityPerTile - BaconPerSlime)
                {
                    continue;
                }

                if (distances.TryGetValue(coord, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    targetCoord = coord;
                    found = true;
                }
            }

            return found;
        }

        /// Nearest (by walking distance) storage tile that actually has
        /// bacon on it, reachable from fromCoord — the mirror-image query to
        /// TryFindNearestTileWithRoom (which looks for room to deposit
        /// *more* bacon); this looks for a tile a hungry creature can eat
        /// from.
        public bool TryFindNearestTileWithBacon(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var coord in _storageTiles)
            {
                if (_storedBacon[coord] < MealBaconAmount)
                {
                    continue;
                }

                if (distances.TryGetValue(coord, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    targetCoord = coord;
                    found = true;
                }
            }

            return found;
        }

        /// Consumes up to amount bacon from the storage tile at coord for a
        /// creature eating a meal — fails (no partial meal) if that tile
        /// doesn't have enough. Mirrors ConvertSlimes' "return what
        /// happened" shape but as a plain success bool, since a meal is
        /// always a fixed amount rather than "however much fits."
        public bool TryEatBacon(Vector2Int coord, int amount)
        {
            if (amount <= 0 || !_storedBacon.TryGetValue(coord, out var current) || current < amount)
            {
                return false;
            }

            _storedBacon[coord] = current - amount;
            _labels[coord].text = _storedBacon[coord].ToString();
            return true;
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

        /// LairManager.RoomSold fires for every sold room — only react to
        /// our own (by roomId prefix, same convention TreasuryManager uses).
        /// That stored bacon is simply gone, same as TreasuryManager's gold.
        private void OnRoomSold(string roomId)
        {
            if (!_roomTiles.TryGetValue(roomId, out var tiles))
            {
                return;
            }

            foreach (var coord in tiles)
            {
                if (_tileVisuals.TryGetValue(coord, out var visual) && visual != null)
                {
                    Destroy(visual);
                }
                _tileVisuals.Remove(coord);

                if (_labels.TryGetValue(coord, out var label) && label != null)
                {
                    Destroy(label.gameObject);
                }
                _labels.Remove(coord);

                _storedBacon.Remove(coord);
                _storageRoomByCoord.Remove(coord);
                _storageTiles.Remove(coord);
            }

            if (_shrineVisuals.TryGetValue(roomId, out var shrine) && shrine != null)
            {
                Destroy(shrine);
            }
            _shrineVisuals.Remove(roomId);

            _roomTiles.Remove(roomId);
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
            marker.name = $"BeaconPreview_{coord.x}_{coord.y}";
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

        /// A raised dais spanning both shrine tiles plus one tube per tile
        /// — up on the first tile, down on the second — per the design
        /// doc's "a tube going up, and one going down."
        private GameObject BuildShrineVisual(List<Vector2Int> shrineTiles)
        {
            var container = new GameObject("BaconShrine");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            foreach (var coord in shrineTiles)
            {
                var worldPos = _grid.GridToWorld(coord);
                var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);

                var dais = GameObject.CreatePrimitive(PrimitiveType.Cube);
                dais.name = $"ShrineDais_{coord.x}_{coord.y}";
                dais.transform.SetParent(container.transform, false);
                dais.transform.position = basePosition + Vector3.up * (DaisHeight * 0.5f);
                dais.transform.localScale = new Vector3(cellSize * DaisFootprintScale, DaisHeight, cellSize * DaisFootprintScale);
                dais.GetComponent<Renderer>().material.color = _daisColor;
                Destroy(dais.GetComponent<Collider>());
            }

            // First shrine tile (by list order — GetShrineTiles always
            // builds it x-then-y from the block's own origin) gets the
            // "up" tube, the second gets "down"; with exactly 2 tiles this
            // is an arbitrary but stable choice, not tied to world
            // direction.
            BuildTube(container.transform, shrineTiles[0], _tubeUpColor);
            BuildTube(container.transform, shrineTiles[1], _tubeDownColor);

            return container;
        }

        private void BuildTube(Transform parent, Vector2Int coord, Color color)
        {
            var worldPos = _grid.GridToWorld(coord);
            var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY + DaisHeight, worldPos.z);

            var tube = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tube.name = "ShrineTube";
            tube.transform.SetParent(parent, false);
            tube.transform.position = basePosition + Vector3.up * (TubeHeight * 0.5f);
            // Unity's cylinder primitive is 2 units tall at scale 1, so its
            // height scale is TubeHeight * 0.5.
            tube.transform.localScale = new Vector3(TubeRadius * 2f, TubeHeight * 0.5f, TubeRadius * 2f);
            tube.GetComponent<Renderer>().material.color = color;
            Destroy(tube.GetComponent<Collider>());
        }

        /// Gray fill on a red-brown border, same footprint convention
        /// TreasuryManager.CreateTileVisual uses for its gold tiles.
        private GameObject CreateStorageTileVisual(Vector2Int coord)
        {
            var container = new GameObject($"BaconStorage_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(container.transform, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * 0.95f, StorageTileHeight, cellSize * 0.95f);
            border.GetComponent<Renderer>().material.color = _storageBorderColor;
            Destroy(border.GetComponent<Collider>());

            var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Fill";
            fill.transform.SetParent(container.transform, false);
            fill.transform.position = basePosition + Vector3.up * (StorageFillHeightMargin * 0.5f);
            fill.transform.localScale = new Vector3(cellSize * StorageFillFootprintScale, StorageTileHeight + StorageFillHeightMargin, cellSize * StorageFillFootprintScale);
            fill.GetComponent<Renderer>().material.color = _storageFillColor;
            Destroy(fill.GetComponent<Collider>());

            return container;
        }

        private TextMesh CreateLabel(Vector2Int coord)
        {
            var go = new GameObject($"BaconLabel_{coord.x}_{coord.y}");
            go.transform.SetParent(transform, false);
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;
            go.transform.position = basePosition + Vector3.up * (StorageFillHeightMargin * 0.5f + (StorageTileHeight + StorageFillHeightMargin) * 0.5f + LabelSurfaceOffset);
            go.transform.rotation = Quaternion.Euler(90f, 45f, 0f);

            var textMesh = go.AddComponent<TextMesh>();
            textMesh.text = "0";
            textMesh.characterSize = LabelCharacterSize;
            textMesh.fontSize = LabelFontSize;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = Color.black;
            return textMesh;
        }
    }
}
