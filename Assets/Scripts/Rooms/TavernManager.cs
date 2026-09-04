using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Converts hauled-in slimes into bacon at a 2x2 shrine — "food for
    /// intelligent creatures" per the design doc. Placed the same
    /// drag-a-footprint way a Lair/Treasury is (see TryPlaceTavern), with a
    /// hard 4x4 minimum enforced like SlimeHatcheryManager's 3x3 one. All of
    /// a room's bacon lives in one shared tank at the shrine (see
    /// _roomBacon) rather than being split across individual tiles — the
    /// footprint tiles actually adjacent to the shrine block (see
    /// IsAdjacentToShrine) are just where an impling/creature stands to
    /// interact with it, and only set that room's tank capacity (still
    /// BaconCapacityPerTile per such tile, so a bigger room's far corners
    /// still don't add capacity, "so implings aren't overtasked" per the
    /// design doc — they just no longer have their own separate counter).
    public class TavernManager : MonoBehaviour, IRestorableRoomManager
    {
        private const int MinFootprintSize = 4;

        /// Gold cost per tile of a placed Tavern — charged out of
        /// TreasuryManager's reserves (see TryPlaceTavern).
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

        // Real dungeon_pack mesh (Assets/Art/DungeonPack/Tavern/
        // BaconBeaconMachine, built by Tools > DungeonPack > Setup Props
        // into Dungeon/Prop_BaconBeaconMachine) — a near-square machine
        // body with bright pipe/gauge accents, replacing the old primitive
        // dais+tubes below. Falls back to that primitive-built version
        // (still defined by the fields/consts just below) if
        // Prop_BaconBeaconMachine hasn't been set up yet, same graceful-
        // degradation pattern ThroneRoom.BuildThrone uses for the throne
        // prop.
        private GameObject _baconBeaconMachinePrefab;
        private const float BaconBeaconMachineFootprintScale = 0.95f;

        // Fallback-only primitive shrine: a raised stone dais under two
        // "tubes" — a bright one going up (slimes descend to be judged) and
        // a dark one going down (bacon rises back out) — used only if
        // Prop_BaconBeaconMachine hasn't been set up yet. See
        // BuildShrineVisual.
        [SerializeField] private Color _daisColor = new Color(0.4f, 0.38f, 0.42f);
        [SerializeField] private Color _tubeUpColor = new Color(0.9f, 0.75f, 0.25f);
        [SerializeField] private Color _tubeDownColor = new Color(0.5f, 0.2f, 0.2f);
        private const float DaisHeight = 0.25f;
        private const float DaisFootprintScale = 0.9f;
        private const float TubeRadius = 0.18f;
        private const float TubeHeight = 0.9f;

        // Real dungeon_pack mesh (Assets/Art/DungeonPack/Tavern/InnBar,
        // built by the same Setup Props tool into Dungeon/Prop_InnBar) — a
        // long, thin bar counter with no existing tile slot to sit on
        // (unlike every other prop in this project), so one is placed per
        // Tavern room along its south edge at natural scale instead — see
        // BuildInnBar. Purely decorative (no gameplay effect); a best-guess
        // placement/orientation with no in-Editor render to confirm
        // against, so is the first thing to reposition/rotate if it
        // doesn't read right once seen. Skipped entirely (no fallback
        // shape) until Prop_InnBar has been set up.
        private GameObject _innBarPrefab;

        // Real dungeon_pack tavern-wood floor texture (Assets/Resources/
        // Dungeon/Tavern/floor_tavern_wood — a plain texture, no prefab/
        // material build step needed, same as DungeonGrid's own Floors
        // set), built into a real URP/Lit material once in Initialize (see
        // DungeonPackRoomArt.BuildMaterial for why — an implicit-default-
        // material + runtime SetTexture attempt rendered pink there) — the
        // storage tile's whole own visual now, no separate fill inset on
        // top (removed, see CreateStorageTileVisual).
        private Material _floorTavernWoodMaterial;
        private const float StorageTileHeight = 0.17f;
        private const float LabelCharacterSize = 0.1f;
        private const int LabelFontSize = 24;

        // The tank's own bacon count floats above the shrine/machine
        // itself now (see CreateTankLabel) rather than sitting flush on a
        // storage tile — a guess at a height that clears the real
        // bacon_beacon_machine prop (up to ~1.4 units tall once scaled to
        // its 2x2 footprint), with no in-Editor render to confirm against.
        private const float TankLabelHeightAboveGround = 1.6f;

        // Storage tile Border's own 0.95 footprint leaves a thin gap at
        // every tile edge where neighboring tiles don't quite touch — a
        // full-cell gray Seam layer underneath fills it in, sitting a bit
        // lower than Border/Fill (SeamHeight, between DungeonGrid's own
        // 0.15 hidden-tile height and Border's 0.17 — tall enough to fully
        // hide that tile, short enough that Border/Fill still visibly "pop
        // out" above it) so adjacent tiles read as flush-fitted floor
        // panels with a mortar line between them, not a void gap. Same
        // fix already applied to TrainingRoomManager/LibraryManager's own
        // ground tiles.
        [SerializeField] private Color _seamColor = new Color(0.32f, 0.32f, 0.32f);
        private const float SeamFootprintScale = 1.0f;
        private const float SeamHeight = 0.16f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private int _ownerId;
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, GameObject> _shrineVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, GameObject> _innBarVisuals = new Dictionary<string, GameObject>();
        private readonly List<Vector2Int> _storageTiles = new List<Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, string> _storageRoomByCoord = new Dictionary<Vector2Int, string>();

        /// All of a room's bacon lives in one place now — "the tank" (the
        /// shrine/machine itself), not scattered across its individual
        /// storage tiles — so this is keyed by roomId, not by coord, and
        /// there's exactly one TankLabel per room (see CreateTankLabel)
        /// instead of one per storage tile. _roomCapacity is each room's
        /// total (storage tile count * BaconCapacityPerTile — the same
        /// total capacity the old one-bucket-per-tile model added up to,
        /// just tracked as a single number), fixed at placement/merge time.
        private readonly Dictionary<string, int> _roomBacon = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _roomCapacity = new Dictionary<string, int>();
        private readonly Dictionary<string, TextMesh> _tankLabels = new Dictionary<string, TextMesh>();

        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        /// Bacon in reserves — summed across every room's own tank, same
        /// shape as TreasuryManager.TotalGold. Read by BottomMenuBar's
        /// top-bar counter.
        public int TotalBacon
        {
            get
            {
                var total = 0;
                foreach (var amount in _roomBacon.Values)
                {
                    total += amount;
                }
                return total;
            }
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            _baconBeaconMachinePrefab = Resources.Load<GameObject>("Dungeon/Prop_BaconBeaconMachine");
            _innBarPrefab = Resources.Load<GameObject>("Dungeon/Prop_InnBar");
            _floorTavernWoodMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/Tavern/floor_tavern_wood");
        }

        /// Total tile count across every placed Tavern (not just its
        /// shrine-adjacent storage tiles) — read by WarlockSpawner as one of
        /// a Warlock's join requirements ("at least one tavern tile
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

        /// Places a Tavern spanning the rectangle between startCoord
        /// and endCoord inclusive. Fails atomically, same as
        /// SlimeHatcheryManager.TryPlaceHatchery, with its own
        /// MinFootprintSize floor.
        public bool TryPlaceTavern(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceTavernInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Tavern exactly like TryPlaceTavern but without
        /// charging gold — for terrain generation (see GameBootstrap's
        /// starting domain layout), not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingTavern(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceTavernInternal(startCoord, endCoord, chargeGold: false);
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingTavern(start, end);
        }

        private bool TryPlaceTavernInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
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

            // Extends an existing Tavern instead of starting a
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
                roomId = $"Tavern_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
            }

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
            }

            var shrineTiles = GetShrineTiles(origin, width, height);
            _shrineVisuals[roomId] = BuildShrineVisual(shrineTiles);
            _innBarVisuals[roomId] = BuildInnBar(origin, width);

            var storageTileCount = 0;
            foreach (var coord in _roomTiles[roomId])
            {
                if (shrineTiles.Contains(coord) || !IsAdjacentToShrine(coord, shrineTiles))
                {
                    continue;
                }

                RegisterStorageTile(coord, roomId);
                storageTileCount++;
            }

            // The tank always starts empty here, same as the old per-tile
            // model already did on a merge/rebuild (ClearShrineAndStorage's
            // own "stored bacon is simply lost" behavior, unchanged) —
            // capacity is recomputed from the room's current storage tile
            // count either way.
            _roomBacon[roomId] = 0;
            _roomCapacity[roomId] = storageTileCount * BaconCapacityPerTile;
            _tankLabels[roomId] = CreateTankLabel(roomId, shrineTiles);

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

        /// Tears down this room's current shrine, tank label, and every
        /// one of its storage tiles — used right before rebuilding all
        /// three from scratch for a new (possibly merged, larger)
        /// footprint, since the shrine's recentered position can change
        /// which tiles count as storage at all. Storage tiles are tracked
        /// per-coordinate, not nested per-room (unlike _roomTiles), so this
        /// finds this room's own tiles among them via _storageRoomByCoord,
        /// the same way OnRoomSold does. Whatever bacon was in the tank is
        /// simply lost, same as OnRoomSold's own "stored bacon is simply
        /// gone" behavior.
        private void ClearShrineAndStorage(string roomId)
        {
            if (_shrineVisuals.TryGetValue(roomId, out var shrine) && shrine != null)
            {
                Destroy(shrine);
            }
            _shrineVisuals.Remove(roomId);

            if (_innBarVisuals.TryGetValue(roomId, out var innBar) && innBar != null)
            {
                Destroy(innBar);
            }
            _innBarVisuals.Remove(roomId);

            if (_tankLabels.TryGetValue(roomId, out var tankLabel) && tankLabel != null)
            {
                Destroy(tankLabel.gameObject);
            }
            _tankLabels.Remove(roomId);
            _roomBacon.Remove(roomId);
            _roomCapacity.Remove(roomId);

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
            _storageRoomByCoord[coord] = roomId;
            _tileVisuals[coord] = CreateStorageTileVisual(coord);
        }

        /// Converts up to slimeAmount slimes into bacon in coord's own
        /// room's tank, BaconPerSlime each, capped by that room's
        /// remaining capacity (not coord's own — the tile is just where the
        /// impling happened to stand, see TryFindNearestTileWithRoom) —
        /// only whole slimes are converted, so a slime that wouldn't fully
        /// fit is left uncarried for a later trip rather than partially
        /// converted. Returns how many slimes were actually consumed,
        /// mirroring TreasuryManager.Deposit's "return what happened,
        /// caller keeps the rest" shape.
        public int ConvertSlimes(Vector2Int coord, int slimeAmount)
        {
            if (slimeAmount <= 0 || !_storageRoomByCoord.TryGetValue(coord, out var roomId) || !_roomBacon.TryGetValue(roomId, out var current))
            {
                return 0;
            }

            var capacity = _roomCapacity.TryGetValue(roomId, out var roomCapacity) ? roomCapacity : 0;
            var remainingCapacity = capacity - current;
            var convertible = Mathf.Min(slimeAmount, remainingCapacity / BaconPerSlime);
            if (convertible <= 0)
            {
                return 0;
            }

            _roomBacon[roomId] = current + convertible * BaconPerSlime;
            UpdateTankLabel(roomId);
            return convertible;
        }

        /// Nearest (by walking distance) storage tile whose room's tank has
        /// room for at least one more slime's worth of bacon, reachable
        /// from fromCoord — same shape as TreasuryManager.
        /// TryFindNearestTileWithRoom. The tile itself is just a place to
        /// stand; capacity is checked against its room's shared tank (see
        /// ConvertSlimes), not the tile.
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
                if (!_storageRoomByCoord.TryGetValue(coord, out var roomId) || !_roomBacon.TryGetValue(roomId, out var current))
                {
                    continue;
                }

                var capacity = _roomCapacity.TryGetValue(roomId, out var roomCapacity) ? roomCapacity : 0;
                if (current > capacity - BaconPerSlime)
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

        /// Nearest (by walking distance) storage tile whose room's tank
        /// actually has bacon in it, reachable from fromCoord — the
        /// mirror-image query to TryFindNearestTileWithRoom (which looks
        /// for room to deposit *more* bacon); this looks for a tile a
        /// hungry creature can eat from.
        public bool TryFindNearestTileWithBacon(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var coord in _storageTiles)
            {
                if (!_storageRoomByCoord.TryGetValue(coord, out var roomId) || !_roomBacon.TryGetValue(roomId, out var current) || current < MealBaconAmount)
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

        /// Consumes up to amount bacon from coord's own room's tank for a
        /// creature eating a meal (coord is just where it's standing, see
        /// TryFindNearestTileWithBacon) — fails (no partial meal) if that
        /// tank doesn't have enough. Mirrors ConvertSlimes' "return what
        /// happened" shape but as a plain success bool, since a meal is
        /// always a fixed amount rather than "however much fits."
        public bool TryEatBacon(Vector2Int coord, int amount)
        {
            if (amount <= 0 || !_storageRoomByCoord.TryGetValue(coord, out var roomId) || !_roomBacon.TryGetValue(roomId, out var current) || current < amount)
            {
                return false;
            }

            _roomBacon[roomId] = current - amount;
            UpdateTankLabel(roomId);
            return true;
        }

        private void UpdateTankLabel(string roomId)
        {
            if (_tankLabels.TryGetValue(roomId, out var label) && label != null)
            {
                label.text = _roomBacon[roomId].ToString();
            }
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

                _storageRoomByCoord.Remove(coord);
                _storageTiles.Remove(coord);
            }

            if (_shrineVisuals.TryGetValue(roomId, out var shrine) && shrine != null)
            {
                Destroy(shrine);
            }
            _shrineVisuals.Remove(roomId);

            if (_innBarVisuals.TryGetValue(roomId, out var innBar) && innBar != null)
            {
                Destroy(innBar);
            }
            _innBarVisuals.Remove(roomId);

            if (_tankLabels.TryGetValue(roomId, out var tankLabel) && tankLabel != null)
            {
                Destroy(tankLabel.gameObject);
            }
            _tankLabels.Remove(roomId);
            _roomBacon.Remove(roomId);
            _roomCapacity.Remove(roomId);

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
                if (!_grid.CanBuildRoomOn(coord, _ownerId))
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
            marker.name = $"TavernPreview_{coord.x}_{coord.y}";
            marker.transform.SetParent(transform, false);
            marker.transform.localPosition = new Vector3(worldPos.x, centerY, worldPos.z);
            marker.transform.localScale = new Vector3(cellSize * PreviewFootprintScale, PreviewHeight, cellSize * PreviewFootprintScale);
            Prims.Tint(marker, color);
            Destroy(marker.GetComponent<Collider>());
            return marker;
        }

        private float GetGroundTopY(Vector2Int coord)
        {
            return _grid.GetTile(coord).Type == TileType.Rock ? RockTopY : _grid.FloorSurfaceY;
        }

        /// The real bacon_beacon_machine prop centered over the shrine's
        /// 2x2 block, scaled to fit it (its own footprint is already
        /// nearly a 2x2 square, see the prop's own header) — falls back to
        /// the original primitive-built dais+tubes below if
        /// Prop_BaconBeaconMachine hasn't been set up yet.
        private GameObject BuildShrineVisual(List<Vector2Int> shrineTiles)
        {
            if (_baconBeaconMachinePrefab != null)
            {
                return BuildShrineMachineVisual(shrineTiles);
            }

            return BuildShrineFallbackVisual(shrineTiles);
        }

        private GameObject BuildShrineMachineVisual(List<Vector2Int> shrineTiles)
        {
            var container = new GameObject("BaconShrine");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var centerWorld = Vector3.zero;
            foreach (var coord in shrineTiles)
            {
                centerWorld += _grid.GridToWorld(coord);
                BuildWoodFloorTile(container.transform, coord);
            }
            centerWorld /= shrineTiles.Count;

            var machine = Instantiate(_baconBeaconMachinePrefab, container.transform, false);
            machine.name = "BaconBeaconMachine";

            var scale = DungeonPackRoomArt.ComputeUniformScaleToFootprint(machine, cellSize * StructureSize * BaconBeaconMachineFootprintScale);
            machine.transform.localScale = Vector3.one * scale;
            machine.transform.localPosition = new Vector3(centerWorld.x, _grid.FloorSurfaceY, centerWorld.z);

            return container;
        }

        /// Fallback-only: a raised dais spanning both shrine tiles plus one
        /// tube per tile — up on the first tile, down on the second — per
        /// the design doc's "a tube going up, and one going down." See
        /// BuildShrineVisual.
        private GameObject BuildShrineFallbackVisual(List<Vector2Int> shrineTiles)
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
                Prims.Tint(dais, _daisColor);
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

        /// One inn_bar prop per placed Tavern room, centered along its
        /// south row at natural (unscaled) size — see this class's own
        /// InnBar field header for why (no existing tile slot fits its
        /// long, thin shape, and this is a best-guess placement with no
        /// in-Editor render to confirm against). Sits on the room's own
        /// south row of tiles rather than projecting past the footprint,
        /// so it's never floating over whatever's beyond the room. Returns
        /// null (nothing built, no fallback shape) if Prop_InnBar hasn't
        /// been set up yet.
        private GameObject BuildInnBar(Vector2Int origin, int width)
        {
            if (_innBarPrefab == null)
            {
                return null;
            }

            var cellSize = _grid.CellSize;
            var southRowWorld = _grid.GridToWorld(origin);
            var centerX = southRowWorld.x + (width - 1) * cellSize * 0.5f;

            var innBar = Instantiate(_innBarPrefab, transform, false);
            innBar.name = "InnBar";
            innBar.transform.localPosition = new Vector3(centerX, _grid.FloorSurfaceY, southRowWorld.z);
            return innBar;
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
            Prims.Tint(tube, color);
            Destroy(tube.GetComponent<Collider>());
        }

        /// A real dungeon_pack-textured tavern-wood border (see
        /// _floorTavernWoodMaterial) — no fill inset on top (removed; read
        /// as a stray white square once seen in-Editor). A full-cell gray
        /// Seam sits beneath it (see its own field header) so the gap
        /// Border's own 0.95 footprint would otherwise leave at every tile
        /// edge reads as a mortar line instead of a void gap.
        private GameObject CreateStorageTileVisual(Vector2Int coord)
        {
            var container = new GameObject($"BaconStorage_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            BuildWoodFloorTile(container.transform, coord);
            return container;
        }

        /// The Seam+Border tavern-wood floor tile pattern every visible
        /// Tavern tile gets — a storage tile's own whole visual
        /// (CreateStorageTileVisual), and (since the real
        /// bacon_beacon_machine mesh doesn't necessarily cover its whole
        /// 2x2 block on its own) the floor under the shrine tiles too, see
        /// BuildShrineMachineVisual — without it a tile shows DungeonGrid's
        /// own untextured claimed-floor visual (rendering pink) instead of
        /// tavern floor.
        private void BuildWoodFloorTile(Transform parent, Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seam.name = "Seam";
            seam.transform.SetParent(parent, false);
            seam.transform.position = basePosition;
            seam.transform.localScale = new Vector3(cellSize * SeamFootprintScale, SeamHeight, cellSize * SeamFootprintScale);
            Prims.Tint(seam, _seamColor);
            Destroy(seam.GetComponent<Collider>());

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(parent, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * 0.95f, StorageTileHeight, cellSize * 0.95f);
            if (_floorTavernWoodMaterial != null)
            {
                // Shared, pre-built material (see DungeonPackRoomArt.
                // BuildMaterial) — no color tint, the wood art already
                // carries its own correct look.
                border.GetComponent<Renderer>().sharedMaterial = _floorTavernWoodMaterial;
            }
            Destroy(border.GetComponent<Collider>());
        }

        /// The room's one bacon-count label, floating above the shrine
        /// (the "tank") instead of sitting flush on any individual storage
        /// tile — all of a room's bacon lives there now, see _roomBacon's
        /// own field header.
        private TextMesh CreateTankLabel(string roomId, List<Vector2Int> shrineTiles)
        {
            var centerWorld = Vector3.zero;
            foreach (var coord in shrineTiles)
            {
                centerWorld += _grid.GridToWorld(coord);
            }
            centerWorld /= shrineTiles.Count;

            var go = new GameObject($"TavernTankLabel_{roomId}");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(centerWorld.x, _grid.FloorSurfaceY + TankLabelHeightAboveGround, centerWorld.z);
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
