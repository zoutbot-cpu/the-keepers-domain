using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Where intelligent creatures do research to gain exp, falling back to
    /// studying combat for a smaller trickle of exp when no research is
    /// available — per the design doc. Placed the same drag-a-footprint way
    /// a Lair/Treasury/Training Room is (see TryPlaceLibrary), with no
    /// minimum size enforced at placement (unlike the Hatchery/Tavern) —
    /// Warlock's own join requirement is what actually demands a 3x3, see
    /// WarlockSpawner.MeetsJoinRequirements/HasLibraryAtLeast below.
    ///
    /// A bookcase structure sits on every OTHER interior ("non-edge") row of
    /// the footprint — tiles on the rectangle's own border never get one, so
    /// a room smaller than 3x3 has no interior tiles and ends up with no
    /// bookcases at all. Within a bookcase row, every tile gets its own real
    /// bookcase_module prop (see BuildBookcaseRow/BuildBookcaseModule) —
    /// only the primitive fallback (used until Prop_BookcaseModule has been
    /// built, see BuildBookcaseRowFallback) actually merges the row into one
    /// elongated shelf ("connect east to west" per the brief). Either way,
    /// the interior row in between two bookcase rows is left as plain open
    /// floor instead of a third bookcase row — that's what makes "a
    /// creature should be able to walk between two rows of book cases"
    /// possible at all, since bookcase tiles themselves are blocked to
    /// pathfinding (see BuildBookcaseVisual/_grid.SetBlocked) while the
    /// border ring and these in-between aisle rows stay ordinary walkable
    /// Library floor.
    ///
    /// Research now actually runs (see WarlockAgent's Researching state) —
    /// a Warlock alternates between walking to a bookcase-adjacent tile,
    /// pausing there 3-5 seconds (still gaining the usual exp on its own
    /// timer while paused), and moving on to a different one, rather than
    /// standing motionless at a single spot.
    public class LibraryManager : MonoBehaviour, IRestorableRoomManager
    {
        /// Gold cost per tile of a placed Library — charged out of
        /// TreasuryManager's reserves, same as Hatchery/Tavern/Training Room.
        public const int CostPerTile = 20;

        /// Research now actually runs (see WarlockAgent) — a Warlock
        /// standing on any Library tile gains this much exp every
        /// ResearchTickSeconds, per the numbers this class originally
        /// documented before the behavior existed.
        public const int ResearchExpPerTick = 5;
        public const float ResearchTickSeconds = 2f;

        // Ground overlay on every footprint tile: the real dungeon_pack
        // parquet floor texture (Assets/Resources/Dungeon/Library/
        // floor_library_parquet — a plain texture, no prefab/material build
        // step needed, same as DungeonGrid's own Floors set), built into a
        // real URP/Lit material once in Initialize (see
        // DungeonPackRoomArt.BuildMaterial for why — an implicit-default-
        // material + runtime SetTexture attempt rendered pink there) — the
        // tile's whole own visual now, no separate fill inset on top
        // (removed, see BuildGroundVisual).
        private Material _floorParquetMaterial;
        private const float GroundTileHeight = 0.17f;
        private const float GroundFootprintScale = 0.95f;

        // Border's own GroundFootprintScale (0.95) leaves a thin gap at
        // every tile edge where neighboring tiles don't quite touch — a
        // full-cell gray Seam layer underneath fills it in, sitting a bit
        // lower than Border (SeamHeight, between DungeonGrid's own 0.15
        // hidden-tile height and Border's 0.17 — tall enough to fully hide
        // that tile, short enough that Border still visibly "pops out"
        // above it) so adjacent tiles read as flush-fitted floor panels
        // with a mortar line between them, not a void gap.
        [SerializeField] private Color _seamColor = new Color(0.32f, 0.32f, 0.32f);
        private const float SeamFootprintScale = 1.0f;
        private const float SeamHeight = 0.16f;

        // Real dungeon_pack mesh (Assets/Art/DungeonPack/Library/
        // BookcaseModule, built by Tools > DungeonPack > Setup Props into
        // Dungeon/Prop_BookcaseModule) — a dark-wood shelf packed with
        // colored book spines. Scaled non-uniformly per axis rather than
        // one uniform factor: Y from its own natural height (see
        // BookcaseModuleTargetHeight — a uniform scale-to-width would blow
        // its ~1.92-unit height up to ~3.5 units), X and Z each stretched
        // by fixed literal factors (tuned in-Editor by eye, not derived
        // from the mesh's own bounds like Y is) so neighboring modules in a
        // row read as nearly touching instead of the gaps its true ~0.5-
        // unit width would otherwise leave. One module centered (and
        // rotated 180° — its front otherwise faces the wrong way, see
        // BuildBookcaseModule) per bookcase-row tile, rather than one shape
        // stretched across the whole row. Falls back to the original
        // primitive-built stretched body+trim below if Prop_BookcaseModule
        // hasn't been set up yet, same graceful-degradation pattern
        // ThroneRoom.BuildThrone uses for the throne prop.
        private GameObject _bookcaseModulePrefab;
        private const float BookcaseModuleTargetHeight = 1.4f;
        private const float BookcaseModuleXScale = 1.95f;
        private const float BookcaseModuleZScale = 2f;

        // Fallback-only primitive bookcase: a dark-wood body with a lighter
        // trim cap on top — same body/roof-cap shape SlimeHatcheryManager's
        // coop uses, spanning the full length of its row's interior run
        // (unlike the real per-tile modules above), used only if
        // Prop_BookcaseModule hasn't been set up yet. See BuildBookcaseRow.
        [SerializeField] private Color _bookcaseBodyColor = new Color(0.28f, 0.18f, 0.1f);
        [SerializeField] private Color _bookcaseTrimColor = new Color(0.55f, 0.4f, 0.22f);
        private const float BookcaseBodyHeight = 0.85f;
        private const float BookcaseTrimHeight = 0.1f;
        private const float BookcaseLengthScale = 0.92f;
        private const float BookcaseDepthScale = 0.55f;

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
        private readonly Dictionary<string, Vector2Int> _roomSizes = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> _groundVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<string, GameObject> _bookcaseVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, List<Vector2Int>> _blockedTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, List<Vector2Int>> _bookcaseAdjacentTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            _floorParquetMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/Library/floor_library_parquet");
            _bookcaseModulePrefab = Resources.Load<GameObject>("Dungeon/Prop_BookcaseModule");
        }

        /// Total tile count across every placed Library — same convention
        /// as TrainingRoomManager/SlimeHatcheryManager's TotalTileCount.
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

        /// Whether at least one placed Library is at least minWidth x
        /// minHeight — read by WarlockSpawner as one of a Warlock's join
        /// requirements ("at least a 3x3 library"). Several small Libraries
        /// don't add up; each placed Library's own footprint is checked
        /// independently.
        public bool HasLibraryAtLeast(int minWidth, int minHeight)
        {
            foreach (var size in _roomSizes.Values)
            {
                if (size.x >= minWidth && size.y >= minHeight)
                {
                    return true;
                }
            }

            return false;
        }

        /// Places a Library spanning the rectangle between startCoord and
        /// endCoord inclusive. Fails atomically, same as
        /// LairManager.TryPlaceLair — no minimum footprint size, unlike the
        /// Hatchery/Tavern.
        public bool TryPlaceLibrary(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceLibraryInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Library exactly like TryPlaceLibrary but without
        /// charging gold — for terrain generation (see GameBootstrap's
        /// starting domain layout), not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingLibrary(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceLibraryInternal(startCoord, endCoord, chargeGold: false);
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingLibrary(start, end);
        }

        private bool TryPlaceLibraryInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
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

            // Extends an existing Library instead of starting a separate
            // one if footprint exactly completes a rectangle together with
            // it (e.g. dragging one more row onto the side of an existing
            // room) — see TryFindMergeableRoom. The old bookcase layout is
            // torn down and rebuilt below for the room's new overall shape,
            // same as a fresh placement, since bookcase rows depend on the
            // whole footprint, not just the newly-dragged tiles.
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearBookcases(roomId);
            }
            else
            {
                roomId = $"Library_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
            }

            _roomSizes[roomId] = new Vector2Int(width, height);

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                _groundVisuals[coord] = BuildGroundVisual(coord);
            }

            var blockedTiles = new List<Vector2Int>();
            _bookcaseVisuals[roomId] = BuildBookcaseVisual(origin, width, height, blockedTiles);
            _blockedTiles[roomId] = blockedTiles;

            foreach (var coord in blockedTiles)
            {
                _grid.SetBlocked(coord, true);
            }

            _bookcaseAdjacentTiles[roomId] = FindBookcaseAdjacentTiles(_roomTiles[roomId], blockedTiles);

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

        /// Tears down this room's current bookcase visual and unblocks its
        /// currently-blocked tiles — used right before rebuilding both from
        /// scratch for a new (possibly merged, larger) footprint, since a
        /// bookcase row's position/length depends on the room's overall
        /// shape.
        private void ClearBookcases(string roomId)
        {
            if (_bookcaseVisuals.TryGetValue(roomId, out var oldVisual) && oldVisual != null)
            {
                Destroy(oldVisual);
            }
            _bookcaseVisuals.Remove(roomId);

            if (_blockedTiles.TryGetValue(roomId, out var oldBlocked))
            {
                foreach (var coord in oldBlocked)
                {
                    _grid.SetBlocked(coord, false);
                }
                _blockedTiles.Remove(roomId);
            }
        }

        /// A bookcase row on every other interior row of the footprint (see
        /// the class header for what "interior," "row," and the
        /// bookcase/aisle alternation mean) — a footprint smaller than 3x3
        /// in either dimension has no interior tiles at all, so this
        /// returns an empty (but still valid, for OnRoomSold's bookkeeping)
        /// container and leaves blockedTiles untouched. Every tile actually
        /// given a bookcase is appended to blockedTiles for the caller to
        /// mark unwalkable (see TryPlaceLibraryInternal) — building the
        /// visual and collecting which tiles it occupies happen together
        /// since they're driven by the exact same row loop.
        private GameObject BuildBookcaseVisual(Vector2Int origin, int width, int height, List<Vector2Int> blockedTiles)
        {
            var container = new GameObject("LibraryBookcases");
            container.transform.SetParent(transform, false);

            var interiorWidth = width - 2;
            var interiorHeight = height - 2;
            if (interiorWidth <= 0 || interiorHeight <= 0)
            {
                return container;
            }

            for (int rowIndex = 0; rowIndex < interiorHeight; rowIndex++)
            {
                // Every other interior row carries a bookcase; the rows in
                // between are left as plain walkable floor ("aisles") so a
                // creature can walk between two rows of bookcases instead
                // of the whole interior being sealed off.
                if (rowIndex % 2 != 0)
                {
                    continue;
                }

                var rowY = origin.y + 1 + rowIndex;
                var rowStartX = origin.x + 1;
                BuildBookcaseRow(container.transform, rowStartX, rowY, interiorWidth);

                for (int x = 0; x < interiorWidth; x++)
                {
                    blockedTiles.Add(new Vector2Int(rowStartX + x, rowY));
                }
            }

            return container;
        }

        /// Every footprint tile that isn't itself a bookcase but is
        /// cardinally next to one — where a researching Warlock actually
        /// stops (see TryFindNearestBookcaseTile/TryFindRandomBookcaseTile),
        /// since it can't stand on the (now blocked) bookcase tile itself.
        private List<Vector2Int> FindBookcaseAdjacentTiles(List<Vector2Int> footprint, List<Vector2Int> blockedTiles)
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

        /// A bookcase for every interior tile in a single row — one real
        /// bookcase_module per tile (see BuildBookcaseModule) once
        /// Prop_BookcaseModule exists, otherwise the original single
        /// stretched-primitive fallback spanning the whole row (see
        /// BuildBookcaseRowFallback).
        private void BuildBookcaseRow(Transform parent, int startX, int y, int tileCount)
        {
            if (_bookcaseModulePrefab != null)
            {
                for (int x = 0; x < tileCount; x++)
                {
                    BuildBookcaseModule(parent, new Vector2Int(startX + x, y));
                }
                return;
            }

            BuildBookcaseRowFallback(parent, startX, y, tileCount);
        }

        /// The real bookcase_module prop, non-uniformly scaled (see
        /// BookcaseModuleTargetHeight/XScale/ZScale's own header) and
        /// centered on coord — rotated 180° on Y since the source mesh's
        /// front otherwise faces the wrong way.
        private void BuildBookcaseModule(Transform parent, Vector2Int coord)
        {
            var worldPos = _grid.GridToWorld(coord);
            var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);

            var module = Instantiate(_bookcaseModulePrefab, parent, false);
            module.name = $"Bookcase_{coord.x}_{coord.y}";

            var renderers = module.GetComponentsInChildren<Renderer>();
            var yScale = 1f;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                if (bounds.size.y > 0.01f)
                {
                    yScale = BookcaseModuleTargetHeight / bounds.size.y;
                }
            }

            module.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            module.transform.localScale = new Vector3(BookcaseModuleXScale, yScale, BookcaseModuleZScale);
            module.transform.localPosition = basePosition;
        }

        /// Fallback-only: one elongated bookcase spanning every interior
        /// tile in a single row — tileCount tiles rendered as a single body
        /// scaled along the row's length rather than tileCount separate
        /// boxes. The body's depth (BookcaseDepthScale) stays well short of
        /// a full tile, so it never touches the next row's body even when
        /// the two rows sit on directly adjacent tiles ("not north to
        /// south"). See BuildBookcaseRow.
        private void BuildBookcaseRowFallback(Transform parent, int startX, int y, int tileCount)
        {
            var cellSize = _grid.CellSize;
            var startWorld = _grid.GridToWorld(new Vector2Int(startX, y));
            var centerX = startWorld.x + (tileCount - 1) * cellSize * 0.5f;
            var basePosition = new Vector3(centerX, _grid.FloorSurfaceY, startWorld.z);
            var length = tileCount * cellSize * BookcaseLengthScale;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = $"Bookcase_{startX}_{y}";
            body.transform.SetParent(parent, false);
            body.transform.position = basePosition + Vector3.up * (BookcaseBodyHeight * 0.5f);
            body.transform.localScale = new Vector3(length, BookcaseBodyHeight, cellSize * BookcaseDepthScale);
            Prims.Tint(body, _bookcaseBodyColor);
            Destroy(body.GetComponent<Collider>());

            var trim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trim.name = "BookcaseTrim";
            trim.transform.SetParent(parent, false);
            trim.transform.position = basePosition + Vector3.up * (BookcaseBodyHeight + BookcaseTrimHeight * 0.5f);
            trim.transform.localScale = new Vector3(length, BookcaseTrimHeight, cellSize * BookcaseDepthScale * 1.05f);
            Prims.Tint(trim, _bookcaseTrimColor);
            Destroy(trim.GetComponent<Collider>());
        }

        /// Nearest (by walking distance) Library tile that's actually
        /// adjacent to a bookcase, reachable from fromCoord — same shape as
        /// TryFindNearestTile, but only considers tiles a researching
        /// Warlock can meaningfully stop at (see WarlockAgent's Researching
        /// state).
        public bool TryFindNearestBookcaseTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var tiles in _bookcaseAdjacentTiles.Values)
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

        /// A random reachable bookcase-adjacent Library tile other than
        /// excludeCoord (so a Warlock moving on from one bookcase always
        /// heads toward a different one) — read by WarlockAgent once a
        /// research pause ends, per the "stopping for 3-5 seconds at a
        /// bookcase, then moving on to another" brief.
        public bool TryFindRandomBookcaseTile(Vector2Int fromCoord, Vector2Int excludeCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();

            foreach (var tiles in _bookcaseAdjacentTiles.Values)
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
            _roomSizes.Remove(roomId);

            foreach (var coord in tiles)
            {
                if (_groundVisuals.TryGetValue(coord, out var groundVisual) && groundVisual != null)
                {
                    Destroy(groundVisual);
                }
                _groundVisuals.Remove(coord);
            }

            if (_bookcaseVisuals.TryGetValue(roomId, out var bookcases) && bookcases != null)
            {
                Destroy(bookcases);
            }
            _bookcaseVisuals.Remove(roomId);

            if (_blockedTiles.TryGetValue(roomId, out var blocked))
            {
                foreach (var coord in blocked)
                {
                    _grid.SetBlocked(coord, false);
                }
                _blockedTiles.Remove(roomId);
            }

            _bookcaseAdjacentTiles.Remove(roomId);
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
            marker.name = $"LibraryPreview_{coord.x}_{coord.y}";
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

        /// A real dungeon_pack-textured parquet border (see
        /// _floorParquetMaterial), with a slightly lighter flat-purple fill
        /// inset on top — same border/fill footprint convention
        /// TreasuryManager.CreateTileVisual uses for its gold tiles. A
        /// full-cell gray Seam sits beneath both (see its own field header)
        /// so the gap Border's own 0.95 footprint would otherwise leave at
        /// every tile edge reads as a mortar line instead of a void gap.
        private GameObject BuildGroundVisual(Vector2Int coord)
        {
            var container = new GameObject($"LibraryGround_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seam.name = "Seam";
            seam.transform.SetParent(container.transform, false);
            // Same "position = basePosition, no offset" convention Border
            // itself uses — SeamHeight sits between DungeonGrid's own
            // hidden-tile height (0.15) and Border's (0.17), so it wins the
            // z-fight against the hidden tile the same way Border does,
            // while still visibly sitting lower than Border.
            seam.transform.position = basePosition;
            seam.transform.localScale = new Vector3(cellSize * SeamFootprintScale, SeamHeight, cellSize * SeamFootprintScale);
            Prims.Tint(seam, _seamColor);
            Destroy(seam.GetComponent<Collider>());

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(container.transform, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * GroundFootprintScale, GroundTileHeight, cellSize * GroundFootprintScale);
            if (_floorParquetMaterial != null)
            {
                // Shared, pre-built material (see DungeonPackRoomArt.
                // BuildMaterial) — no color tint, the parquet art already
                // carries its own correct look.
                border.GetComponent<Renderer>().sharedMaterial = _floorParquetMaterial;
            }
            Destroy(border.GetComponent<Collider>());

            return container;
        }
    }
}
