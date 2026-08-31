using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Monsters;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Rooms
{
    /// Where jailed creatures get lectured out of existence — a Bean
    /// Counter (see BeanCounterAgent) periodically pulls a random prisoner
    /// out of whichever Jail is holding one (JailManager.
    /// TryReleaseRandomPrisoner) and tortures it with a sermon on the evils
    /// of meat, right here (see TryTormentRandomPrisoner — the room "does"
    /// the conversion, the Bean Counter just triggers it, same relationship
    /// TrainingRoomManager has to a training Gremlin's exp tick). Hard 4x5
    /// minimum footprint in either orientation (see MinFootprintShort/Long)
    /// — same "reject anything smaller than the minimum in either
    /// dimension" enforcement JailManager/SlimeHatcheryManager use, just
    /// with two different numbers instead of one square one.
    ///
    /// Interior structure is LibraryManager's own bookcase-row algorithm,
    /// axis-swapped: instead of every OTHER interior row getting a shelf
    /// spanning the room's width (east-west), every other interior COLUMN
    /// gets a bench spanning the room's height (north-south) — so a bench's
    /// long axis runs north-south and every seat in it faces east, toward
    /// the one wall board (with its procedural broccoli icon) mounted on
    /// the room's east ring column. Aisle columns between benches stay
    /// walkable, same "creature can't stand on the structure itself, walks
    /// its adjacent tile instead" rule Library's bookcases/Training Room's
    /// dummies both already use — see FindBenchAdjacentTiles.
    public class ConversionClassManager : MonoBehaviour, IRestorableRoomManager
    {
        /// Gold cost per tile of a placed Conversion Class — same
        /// unbalanced 20g/tile placeholder every other room besides
        /// Treasury/Lair uses.
        public const int CostPerTile = 20;

        // Hard minimum, checked in either orientation — a dragged rectangle
        // has to be at least 4 in one dimension and 5 in the other, same
        // enforcement shape as JailManager.MinFootprintSize but with two
        // distinct numbers instead of one square one.
        private const int MinFootprintShort = 4;
        private const int MinFootprintLong = 5;

        // Ground overlay: an olive/khaki border+fill pair — Training Room's
        // own border/fill grammar, recolored so the two rooms don't read as
        // the same shade of green at a glance.
        [SerializeField] private Color _groundBorderColor = new Color(0.32f, 0.36f, 0.14f);
        [SerializeField] private Color _groundFillColor = new Color(0.62f, 0.68f, 0.4f);
        private const float GroundTileHeight = 0.17f;
        private const float GroundFillHeightMargin = 0.03f;
        private const float GroundFootprintScale = 0.95f;
        private const float GroundFillFootprintScale = 0.8f;

        // Bench: same dark-wood-body/lighter-trim shape LibraryManager's
        // bookcase uses, just spanning the interior's height instead of its
        // width (see BuildBenchColumn).
        [SerializeField] private Color _benchBodyColor = new Color(0.32f, 0.22f, 0.12f);
        [SerializeField] private Color _benchTrimColor = new Color(0.58f, 0.44f, 0.26f);
        private const float BenchBodyHeight = 0.42f;
        private const float BenchTrimHeight = 0.06f;
        private const float BenchLengthScale = 0.92f;
        private const float BenchDepthScale = 0.55f;

        // Wall board + broccoli icon — one per room, mounted on the east
        // ring column's vertical middle tile (biased low on a tie, same
        // convention JailManager's gate-tile placement uses).
        [SerializeField] private Color _wallBoardColor = new Color(0.4f, 0.28f, 0.16f);
        [SerializeField] private Color _broccoliStemColor = new Color(0.55f, 0.42f, 0.2f);
        [SerializeField] private Color _broccoliFloretColor = new Color(0.18f, 0.45f, 0.15f);
        private const float WallBoardHeight = 0.9f;
        private const float WallBoardThickness = 0.08f;
        private const float WallBoardWidthScale = 0.85f;
        private const float WallBoardEdgeInset = 0.42f;
        private const float BroccoliStemHeight = 0.22f;
        private const float BroccoliStemRadius = 0.045f;
        private const float BroccoliFloretRadius = 0.12f;
        private const float BroccoliFloretSpread = 0.16f;
        private const float BroccoliReliefOffset = 0.05f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        // Torment outcome tuning — all placeholder, unbalanced, per the
        // brief's own examples ("gremlins hate it and join to end their
        // suffering" -> high; an intelligent Warlock resists -> low).
        // Anything not listed (including Elf, and any future creature kind)
        // falls back to a flat 50/50.
        private const float GremlinJoinChance = 0.8f;
        private const float WarlockJoinChance = 0.3f;
        private const float MazeRattlerJoinChance = 0.55f;
        private const float DefaultJoinChance = 0.5f;

        // "Explode into gold coins" — the Good-alignment failure outcome.
        // Nothing in the game can produce a Good creature yet (see
        // JailedPrisoner's own comment in JailManager), so this is real,
        // correct code that's currently unreachable — same honesty
        // convention MazeRattlerAgent's haunting behavior used to carry
        // before prisoners existed at all.
        private const int GoodFailureGoldPerLevel = 50;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private JailManager _jailManager;
        private GremlinSpawner _gremlinSpawner;
        private WarlockSpawner _warlockSpawner;
        private MazeRattlerSpawner _mazeRattlerSpawner;
        private ElfSpawner _elfSpawner;

        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<Vector2Int, GameObject> _groundVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<string, GameObject> _benchVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, GameObject> _wallBoardVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, List<Vector2Int>> _blockedTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, List<Vector2Int>> _benchAdjacentTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager, JailManager jailManager, GremlinSpawner gremlinSpawner, WarlockSpawner warlockSpawner, MazeRattlerSpawner mazeRattlerSpawner, ElfSpawner elfSpawner)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            _jailManager = jailManager;
            _gremlinSpawner = gremlinSpawner;
            _warlockSpawner = warlockSpawner;
            _mazeRattlerSpawner = mazeRattlerSpawner;
            _elfSpawner = elfSpawner;
            lairManager.RoomSold += OnRoomSold;
        }

        /// Total tile count across every placed Conversion Class — same
        /// convention every other room manager's TotalTileCount uses.
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

        /// Number of distinct placed Conversion Class rooms (not tiles) —
        /// read by BeanCounterSpawner as its own population cap, same shape
        /// JailManager.RoomCount gives MazeRattlerSpawner.
        public int RoomCount => _roomTiles.Count;

        public bool TryPlaceConversionClass(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceConversionClassInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Same as TryPlaceConversionClass but skips the gold cost — for
        /// terrain generation, not a player purchase, same as every other
        /// room manager's Place* pair. Not currently called by
        /// GameBootstrap (no starting Conversion Class exists), kept for
        /// parity.
        public bool PlaceStartingConversionClass(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceConversionClassInternal(startCoord, endCoord, chargeGold: false);
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingConversionClass(start, end);
        }

        private bool TryPlaceConversionClassInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
        {
            var footprint = GetFootprint(startCoord, endCoord, out var newWidth, out var newHeight, out var newOrigin);
            if (!MeetsMinimumSize(newWidth, newHeight) || !CanPlaceFootprint(footprint))
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

            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearStructures(roomId);
            }
            else
            {
                roomId = $"ConversionClass_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
            }

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                _groundVisuals[coord] = BuildGroundVisual(coord);
            }

            var blockedTiles = new List<Vector2Int>();
            _benchVisuals[roomId] = BuildBenchVisual(origin, width, height, blockedTiles);
            _blockedTiles[roomId] = blockedTiles;

            foreach (var coord in blockedTiles)
            {
                _grid.SetBlocked(coord, true);
            }

            _benchAdjacentTiles[roomId] = FindBenchAdjacentTiles(_roomTiles[roomId], blockedTiles);
            _wallBoardVisuals[roomId] = BuildWallBoardVisual(origin, width, height);

            return true;
        }

        private static bool MeetsMinimumSize(int width, int height)
        {
            return (width >= MinFootprintShort && height >= MinFootprintLong)
                || (width >= MinFootprintLong && height >= MinFootprintShort);
        }

        /// Same shape as every other room manager's TryFindMergeableRoom —
        /// only ever merges with one existing room at a time, false if
        /// footprint doesn't cleanly complete a rectangle with any single
        /// existing Conversion Class.
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

        /// Tears down this room's current bench/wall-board visuals and
        /// unblocks its bench tiles — used right before rebuilding both for
        /// a new (possibly merged, larger) footprint.
        private void ClearStructures(string roomId)
        {
            if (_benchVisuals.TryGetValue(roomId, out var oldBenches) && oldBenches != null)
            {
                Destroy(oldBenches);
            }
            _benchVisuals.Remove(roomId);

            if (_wallBoardVisuals.TryGetValue(roomId, out var oldBoard) && oldBoard != null)
            {
                Destroy(oldBoard);
            }
            _wallBoardVisuals.Remove(roomId);

            if (_blockedTiles.TryGetValue(roomId, out var oldBlocked))
            {
                foreach (var coord in oldBlocked)
                {
                    _grid.SetBlocked(coord, false);
                }
                _blockedTiles.Remove(roomId);
            }
        }

        /// A bench on every other interior column (see this class's header
        /// for what "interior," "column," and the bench/aisle alternation
        /// mean) — a footprint with no interior column at all (impossible
        /// at the 4x5 minimum, but guarded the same way Library guards a
        /// too-small room) returns an empty container instead.
        private GameObject BuildBenchVisual(Vector2Int origin, int width, int height, List<Vector2Int> blockedTiles)
        {
            var container = new GameObject("ConversionClassBenches");
            container.transform.SetParent(transform, false);

            var interiorWidth = width - 2;
            var interiorHeight = height - 2;
            if (interiorWidth <= 0 || interiorHeight <= 0)
            {
                return container;
            }

            for (int colIndex = 0; colIndex < interiorWidth; colIndex++)
            {
                // Every other interior column carries a bench; the columns
                // in between are left as plain walkable floor ("aisles") —
                // axis-swapped mirror of LibraryManager's row alternation.
                if (colIndex % 2 != 0)
                {
                    continue;
                }

                var colX = origin.x + 1 + colIndex;
                var colStartY = origin.y + 1;
                BuildBenchColumn(container.transform, colX, colStartY, interiorHeight);

                for (int y = 0; y < interiorHeight; y++)
                {
                    blockedTiles.Add(new Vector2Int(colX, colStartY + y));
                }
            }

            return container;
        }

        /// One elongated bench spanning every interior tile in a single
        /// column — the axis-swapped mirror of LibraryManager.
        /// BuildBookcaseRow: tileCount tiles render as a single body scaled
        /// along the column's length (north-south) rather than tileCount
        /// separate boxes, so every seat in it faces the same way (east,
        /// toward the wall board) instead of the column reading as loose
        /// individual chairs.
        private void BuildBenchColumn(Transform parent, int x, int startY, int tileCount)
        {
            var cellSize = _grid.CellSize;
            var startWorld = _grid.GridToWorld(new Vector2Int(x, startY));
            var centerZ = startWorld.z + (tileCount - 1) * cellSize * 0.5f;
            var basePosition = new Vector3(startWorld.x, _grid.FloorSurfaceY, centerZ);
            var length = tileCount * cellSize * BenchLengthScale;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = $"Bench_{x}_{startY}";
            body.transform.SetParent(parent, false);
            body.transform.position = basePosition + Vector3.up * (BenchBodyHeight * 0.5f);
            body.transform.localScale = new Vector3(cellSize * BenchDepthScale, BenchBodyHeight, length);
            body.GetComponent<Renderer>().material.color = _benchBodyColor;
            Destroy(body.GetComponent<Collider>());

            var trim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trim.name = "BenchTrim";
            trim.transform.SetParent(parent, false);
            trim.transform.position = basePosition + Vector3.up * (BenchBodyHeight + BenchTrimHeight * 0.5f);
            trim.transform.localScale = new Vector3(cellSize * BenchDepthScale * 1.05f, BenchTrimHeight, length);
            trim.GetComponent<Renderer>().material.color = _benchTrimColor;
            Destroy(trim.GetComponent<Collider>());
        }

        /// One non-blocking wall board on the room's east ring column,
        /// vertically centered (biased low on a tie, same convention
        /// JailManager's gate-tile placement uses) — a thin panel inset
        /// toward the east edge with a procedural broccoli icon centered on
        /// it. The ring tile underneath stays ordinary walkable floor.
        private GameObject BuildWallBoardVisual(Vector2Int origin, int width, int height)
        {
            var container = new GameObject("ConversionClassWallBoard");
            container.transform.SetParent(transform, false);

            var boardCoord = new Vector2Int(origin.x + width - 1, origin.y + height / 2);
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(boardCoord);
            var boardX = worldPos.x + cellSize * WallBoardEdgeInset;
            var boardCenterY = _grid.FloorSurfaceY + WallBoardHeight * 0.5f;

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(container.transform, false);
            panel.transform.position = new Vector3(boardX, boardCenterY, worldPos.z);
            panel.transform.localScale = new Vector3(WallBoardThickness, WallBoardHeight, cellSize * WallBoardWidthScale);
            panel.GetComponent<Renderer>().material.color = _wallBoardColor;
            Destroy(panel.GetComponent<Collider>());

            BuildBroccoliIcon(container.transform, new Vector3(boardX + WallBoardThickness * 0.5f + BroccoliReliefOffset, boardCenterY, worldPos.z));

            return container;
        }

        /// A small cluster of green spheres (florets) over a brown cylinder
        /// (stem) — a broccoli, built from primitives like every other
        /// structure in this project, no texture/sprite asset needed.
        private void BuildBroccoliIcon(Transform parent, Vector3 center)
        {
            var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stem.name = "BroccoliStem";
            stem.transform.SetParent(parent, false);
            stem.transform.position = center + Vector3.down * (BroccoliStemHeight * 0.3f);
            stem.transform.localScale = new Vector3(BroccoliStemRadius * 2f, BroccoliStemHeight * 0.5f, BroccoliStemRadius * 2f);
            stem.GetComponent<Renderer>().material.color = _broccoliStemColor;
            Destroy(stem.GetComponent<Collider>());

            var floretOffsets = new[]
            {
                new Vector3(0f, BroccoliStemHeight * 0.25f, 0f),
                new Vector3(0f, BroccoliStemHeight * 0.1f, -BroccoliFloretSpread),
                new Vector3(0f, BroccoliStemHeight * 0.1f, BroccoliFloretSpread),
                new Vector3(0f, BroccoliStemHeight * 0.55f, 0f),
            };

            foreach (var offset in floretOffsets)
            {
                var floret = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                floret.name = "BroccoliFloret";
                floret.transform.SetParent(parent, false);
                floret.transform.position = center + offset;
                floret.transform.localScale = Vector3.one * (BroccoliFloretRadius * 2f);
                floret.GetComponent<Renderer>().material.color = _broccoliFloretColor;
                Destroy(floret.GetComponent<Collider>());
            }
        }

        /// Every footprint tile that isn't itself a bench but is cardinally
        /// next to one — where a lecturing Bean Counter actually stops (see
        /// TryFindNearestBenchTile/TryFindRandomBenchTile), same shape as
        /// LibraryManager.FindBookcaseAdjacentTiles.
        private List<Vector2Int> FindBenchAdjacentTiles(List<Vector2Int> footprint, List<Vector2Int> blockedTiles)
        {
            var blockedSet = new HashSet<Vector2Int>(blockedTiles);
            var adjacent = new List<Vector2Int>();

            foreach (var coord in footprint)
            {
                if (blockedSet.Contains(coord))
                {
                    continue;
                }

                foreach (var direction in GridDirections.Cardinal)
                {
                    if (blockedSet.Contains(coord + direction))
                    {
                        adjacent.Add(coord);
                        break;
                    }
                }
            }

            return adjacent;
        }

        /// Nearest (by walking distance) bench-adjacent tile reachable from
        /// fromCoord — same shape as LibraryManager.
        /// TryFindNearestBookcaseTile.
        public bool TryFindNearestBenchTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var tiles in _benchAdjacentTiles.Values)
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

        /// A random reachable bench-adjacent tile other than excludeCoord —
        /// same shape as LibraryManager.TryFindRandomBookcaseTile.
        public bool TryFindRandomBenchTile(Vector2Int fromCoord, Vector2Int excludeCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();

            foreach (var tiles in _benchAdjacentTiles.Values)
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

        /// Pulls one random prisoner out of whichever Jail is holding one
        /// (JailManager.TryReleaseRandomPrisoner) and resolves the torment:
        /// a per-creature-kind chance to join (see JoinChanceFor), and on
        /// failure either an Elf transformation (Evil alignment — the only
        /// alignment reachable today) or a gold explosion (Good alignment —
        /// currently unreachable, see this class's own header). Called by
        /// BeanCounterAgent once per lecture session while it's actually at
        /// a bench. Returns false if there was nobody to torment.
        public bool TryTormentRandomPrisoner()
        {
            if (_jailManager == null || !_jailManager.TryReleaseRandomPrisoner(out var creatureKind, out var name, out var level, out var isGoodAlignment, out var pitCoord))
            {
                return false;
            }

            var joined = UnityEngine.Random.value < JoinChanceFor(creatureKind);

            if (isGoodAlignment)
            {
                if (joined)
                {
                    SpawnByKind(creatureKind, pitCoord);
                    GameplayLog.Write($"{name} agreed to eat meat and do regular jobs — rejoined the domain at ({pitCoord.x},{pitCoord.y})");
                }
                else
                {
                    var gold = GoodFailureGoldPerLevel * Mathf.Max(1, level);
                    _treasuryManager?.AddGold(gold);
                    GameplayLog.Write($"{name} refused conversion and exploded into {gold} gold at ({pitCoord.x},{pitCoord.y})");
                }
                return true;
            }

            if (joined)
            {
                SpawnByKind(creatureKind, pitCoord);
                GameplayLog.Write($"{name} caved under the sermon and rejoined the domain as a {creatureKind} at ({pitCoord.x},{pitCoord.y})");
            }
            else
            {
                _elfSpawner?.SpawnElf(pitCoord);
                GameplayLog.Write($"{name} broke down into a weak, worthless Elf at ({pitCoord.x},{pitCoord.y})");
            }
            return true;
        }

        private static float JoinChanceFor(string creatureKind)
        {
            if (creatureKind == GremlinAgent.CreatureKind)
            {
                return GremlinJoinChance;
            }
            if (creatureKind == WarlockAgent.CreatureKind)
            {
                return WarlockJoinChance;
            }
            if (creatureKind == MazeRattlerAgent.CreatureKind)
            {
                return MazeRattlerJoinChance;
            }
            return DefaultJoinChance;
        }

        private void SpawnByKind(string creatureKind, Vector2Int coord)
        {
            if (creatureKind == GremlinAgent.CreatureKind)
            {
                _gremlinSpawner?.SpawnGremlin(coord);
            }
            else if (creatureKind == WarlockAgent.CreatureKind)
            {
                _warlockSpawner?.SpawnWarlock(coord);
            }
            else if (creatureKind == MazeRattlerAgent.CreatureKind)
            {
                _mazeRattlerSpawner?.SpawnMazeRattler(coord);
            }
            else if (creatureKind == ElfAgent.CreatureKind)
            {
                _elfSpawner?.SpawnElf(coord);
            }
        }

        public void UpdatePlacementPreview(Vector2Int startCoord, Vector2Int endCoord)
        {
            ClearPlacementPreview();

            var footprint = GetFootprint(startCoord, endCoord, out var width, out var height, out _);
            var isValidSize = MeetsMinimumSize(width, height);
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
                if (_groundVisuals.TryGetValue(coord, out var groundVisual) && groundVisual != null)
                {
                    Destroy(groundVisual);
                }
                _groundVisuals.Remove(coord);
            }

            ClearStructures(roomId);
            _benchAdjacentTiles.Remove(roomId);
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
            marker.name = $"ConversionClassPreview_{coord.x}_{coord.y}";
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

        /// Thin olive border with a lighter khaki fill in the middle — same
        /// border/fill footprint convention TrainingRoomManager/
        /// LibraryManager's own ground visuals use.
        private GameObject BuildGroundVisual(Vector2Int coord)
        {
            var container = new GameObject($"ConversionClassGround_{coord.x}_{coord.y}");
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
            fill.transform.position = basePosition + Vector3.up * (GroundFillHeightMargin * 0.5f);
            fill.transform.localScale = new Vector3(cellSize * GroundFillFootprintScale, GroundTileHeight + GroundFillHeightMargin, cellSize * GroundFillFootprintScale);
            fill.GetComponent<Renderer>().material.color = _groundFillColor;
            Destroy(fill.GetComponent<Collider>());

            return container;
        }
    }
}
