using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.DebugUI;

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
    /// Attracts the Maze Rattler (haunts the pit tiles, no interaction with
    /// prisoners). Capture is real now: dropping a carried Gremlin/Warlock/
    /// Maze Rattler/Elf onto a pit tile via the Grab hand (see
    /// MinionGrabController.TryDrop/JailManager.TryCapture) imprisons it as
    /// inert JailedPrisoner data — no longer a live agent — until Conversion
    /// Class's Bean Counter processes it (ConversionClassManager.
    /// TryTormentRandomPrisoner/JailManager.TryReleaseRandomPrisoner). This
    /// class only owns the prisoner bookkeeping and its "bound blob"
    /// visual; the actual torment/outcome logic lives in
    /// ConversionClassManager. Placement/visuals otherwise follow the same
    /// "player-placed, subscribes to RoomSold" shape TrainingRoomManager/
    /// LibraryManager use, including their adjacent-placement merge rule
    /// (see TryFindMergeableRoom): a
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
    ///   like any room. The pit's own rim is a short, plain dark wall
    ///   (see BuildRimWallVisual) just deep enough to close the gap under
    ///   the light gray fence — deliberately NOT a slab under the whole
    ///   tile: a full-footprint column reaching up to ground level would
    ///   sit physically in front of (above) the sunk floor from any
    ///   downward-looking angle and bury it, which is exactly the "solid
    ///   black box" bug an earlier, much deeper version of this wall had.
    ///   Interior pit tiles have no elevation seam against their
    ///   neighbors and don't need a wall at all, same as ordinary
    ///   DungeonGrid floor never does. The black-panel-with-a-gray-cross
    ///   "block" look the wall originally carried now lives on the ring's
    ///   own floor instead (see BuildGrateFloorVisual) — the fence is the
    ///   pit's one real decorated rim marker.
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

        // How far down the rim wall reaches — just 2 tile-heights (cellSize
        // is 1 world unit per grid step, same "world units == grid steps"
        // assumption PitDepth relies on). Doesn't need to fake bottomless
        // depth any more: the pit's actual rim marker is the light gray
        // fence below, not this wall — the wall now only needs to close
        // the immediate gap under that fence.
        private const float RimWallDepth = 2f;
        private const float RimWallThickness = 0.1f;

        // Right at the tile's true edge (half a cell is 0.5) rather than
        // the fence's own inset — the wall and the low fence rail below
        // sit at slightly different depths along the same edge so their
        // faces don't coplanar-z-fight with each other.
        private const float RimWallEdgeInset = 0.49f;

        // Plain dark fill, no cross texture — the wall itself is
        // deliberately unstyled now that the fence is the pit's one real
        // rim marker. Also reused as the ring floor grate's own panel
        // color (see BuildGrateFloorVisual) so the two still read as the
        // same material.
        [SerializeField] private Color _rimWallColor = new Color(0.05f, 0.05f, 0.05f);

        // Light gray rim fence — the pit's only decorated rim marker now.
        // Short rails along every outward-facing edge of the pit except
        // the one gate tile/edge, grounded at ordinary FloorSurfaceY
        // (ground level) regardless of the pit's own sunk floor, since
        // it's a guard rail marking the rim, not something that spans
        // the pit's depth.
        [SerializeField] private Color _fenceColor = new Color(0.75f, 0.75f, 0.75f);
        private const float FenceRailHeight = 0.35f;
        private const float FenceRailThickness = 0.06f;
        private const float FenceEdgeInset = 0.42f;

        // Ring floor "grate" — every walkway tile around the pit gets a
        // black panel with a light gray plus/cross centered on it, arms
        // reaching to the middle of each of the tile's four sides. The
        // same block pattern the rim wall used to carry, moved onto the
        // ring's own floor instead (see BuildGrateFloorVisual) now that
        // the wall itself is plain.
        [SerializeField] private Color _grateCrossColor = new Color(0.75f, 0.75f, 0.75f);
        private const float GrateFloorHeight = 0.17f;
        private const float GrateFloorFootprintScale = 0.95f;
        private const float GrateCrossBarThickness = 0.09f;

        // How far the cross bars sit proud of the panel beneath them —
        // without this they'd sit exactly coplanar with the panel and
        // z-fight/flicker.
        private const float GrateCrossReliefOffset = 0.02f;

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

        // Per-tile — the dirt floor (pit tiles) and grate floor (ring
        // tiles) doesn't depend on the room's overall shape the way the
        // rim structures below do, EXCEPT that a merge can promote a
        // former ring tile to pit (see IsPitTile's own comment), which is
        // the one case that has to swap a tile from _ringFloorVisuals
        // into _floorVisuals — see the per-tile loop in
        // TryPlaceJailInternal.
        private readonly Dictionary<Vector2Int, GameObject> _floorVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, GameObject> _ringFloorVisuals = new Dictionary<Vector2Int, GameObject>();

        // Per-room — fence rails, rim walls, and the one staircase/gate,
        // torn down and rebuilt in full every time this room's footprint
        // changes (initial placement or a merge-extension), since which
        // tiles are boundary vs. interior — and where the gate lands —
        // depends on the room's overall shape.
        private readonly Dictionary<string, List<GameObject>> _rimStructures = new Dictionary<string, List<GameObject>>();

        // Per-room snapshot of that room's current pit tiles — recomputed
        // alongside _rimStructures every placement/merge (see
        // TryPlaceJailInternal), so MazeRattlerAgent's haunting behavior
        // (TryFindRandomPitTile) always sees the up-to-date set without
        // re-deriving it from scratch on every call.
        private readonly Dictionary<string, HashSet<Vector2Int>> _pitTilesByRoom = new Dictionary<string, HashSet<Vector2Int>>();

        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        // Prisoner color/size — a small bound gray blob sitting on its own
        // pit tile, distinct from the "haunting" Maze Rattler capsules that
        // pass through the same pit tiles (see TryFindRandomPitTile) so the
        // two never read as the same thing.
        [SerializeField] private Color _prisonerColor = new Color(0.55f, 0.55f, 0.58f);
        private const float PrisonerRadiusScale = 0.16f;
        private const float PrisonerHeightScale = 0.22f;

        /// A captured creature, pulled out of Grab-and-drop onto a pit tile
        /// (see MinionGrabController.TryDrop) and held here as inert data —
        /// not a live agent/GameObject — until Conversion Class's own Bean
        /// Counter processes it (see ConversionClassManager.
        /// TryTormentRandomPrisoner). IsGoodAlignment exists for the torment
        /// table's "Good creature" branch, but nothing in the game can
        /// produce a Good creature yet — every creature captured today
        /// (Gremlin/Warlock/Maze Rattler/Elf) is Evil, same "real code,
        /// currently unreachable" honesty this class's own header used to
        /// carry for the whole capture mechanic before this existed.
        private class JailedPrisoner
        {
            public string CreatureKind;
            public string Name;
            public int Level;
            public bool IsGoodAlignment;
            public Vector2Int PitCoord;
        }

        // Keyed by the pit tile the prisoner occupies — occupancy is just
        // key presence, same "dictionary keyed by coord" shape
        // _floorVisuals/_ringFloorVisuals already use.
        private readonly Dictionary<Vector2Int, JailedPrisoner> _prisoners = new Dictionary<Vector2Int, JailedPrisoner>();
        private readonly Dictionary<Vector2Int, GameObject> _prisonerVisuals = new Dictionary<Vector2Int, GameObject>();

        public void Initialize(DungeonGrid grid, BuilderJobBoard jobBoard, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _jobBoard = jobBoard;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
        }

        /// How many prisoners are currently held across every placed Jail —
        /// read by BeanCounterAgent to decide whether there's anyone to
        /// torment this lecture session.
        public int PrisonerCount => _prisoners.Count;

        /// Whether coord is a pit tile of any placed Jail — a public wrapper
        /// over the per-room _pitTilesByRoom bookkeeping (previously only
        /// checked internally), needed by MinionGrabController to recognize
        /// a "drop onto Jail" gesture the same way it already recognizes a
        /// "drop onto Training Room" one.
        public bool IsPitTile(Vector2Int coord)
        {
            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                if (pitTiles.Contains(coord))
                {
                    return true;
                }
            }

            return false;
        }

        /// Captures a creature into the nearest reachable, currently-empty
        /// pit tile of any placed Jail — called by MinionGrabController
        /// when a carried creature is dropped onto a Jail pit tile.
        /// Opportunistic: returns false (never forces the caller to fail
        /// the drop) if there's no Jail, every pit tile is already
        /// occupied, or none is reachable from nearCoord.
        public bool TryCapture(Vector2Int nearCoord, string creatureKind, string name, int level, bool isGoodAlignment)
        {
            var distances = _grid.GetReachableFloorDistances(nearCoord);
            var bestDistance = int.MaxValue;
            var found = false;
            var targetCoord = default(Vector2Int);

            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (_prisoners.ContainsKey(coord))
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
            }

            if (!found)
            {
                return false;
            }

            _prisoners[targetCoord] = new JailedPrisoner
            {
                CreatureKind = creatureKind,
                Name = name,
                Level = level,
                IsGoodAlignment = isGoodAlignment,
                PitCoord = targetCoord
            };
            _prisonerVisuals[targetCoord] = BuildPrisonerVisual(targetCoord);
            GameplayLog.Write($"{name} was thrown in the Jail at ({targetCoord.x},{targetCoord.y})");
            return true;
        }

        /// Pops one random currently-held prisoner (across every placed
        /// Jail) for Conversion Class's Bean Counter to process — see
        /// ConversionClassManager.TryTormentRandomPrisoner. Frees the pit
        /// tile and tears down its visual as part of the release, whatever
        /// the torment's eventual outcome turns out to be.
        public bool TryReleaseRandomPrisoner(out string creatureKind, out string name, out int level, out bool isGoodAlignment, out Vector2Int pitCoord)
        {
            if (_prisoners.Count == 0)
            {
                creatureKind = null;
                name = null;
                level = 0;
                isGoodAlignment = false;
                pitCoord = default;
                return false;
            }

            var index = UnityEngine.Random.Range(0, _prisoners.Count);
            var i = 0;
            Vector2Int keyCoord = default;
            foreach (var coord in _prisoners.Keys)
            {
                if (i == index)
                {
                    keyCoord = coord;
                    break;
                }
                i++;
            }

            var prisoner = _prisoners[keyCoord];
            creatureKind = prisoner.CreatureKind;
            name = prisoner.Name;
            level = prisoner.Level;
            isGoodAlignment = prisoner.IsGoodAlignment;
            pitCoord = prisoner.PitCoord;

            ReleasePrisonerAt(keyCoord);
            return true;
        }

        private void ReleasePrisonerAt(Vector2Int coord)
        {
            if (_prisonerVisuals.TryGetValue(coord, out var visual) && visual != null)
            {
                Destroy(visual);
            }
            _prisonerVisuals.Remove(coord);
            _prisoners.Remove(coord);
        }

        /// A small bound gray blob sitting on its own pit tile — same
        /// primitives-only, collider-stripped convention every structure in
        /// this class already uses.
        private GameObject BuildPrisonerVisual(Vector2Int coord)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = $"JailPrisoner_{coord.x}_{coord.y}";
            visual.transform.SetParent(transform, false);
            visual.transform.localScale = new Vector3(PrisonerRadiusScale, PrisonerHeightScale, PrisonerRadiusScale);
            var groundY = _grid.FloorSurfaceY - PitDepth;
            var worldPos = _grid.GridToWorld(coord);
            visual.transform.position = new Vector3(worldPos.x, groundY + PrisonerHeightScale, worldPos.z);
            visual.GetComponent<Renderer>().material.color = _prisonerColor;
            Destroy(visual.GetComponent<Collider>());
            return visual;
        }

        /// Total tile count across every placed Jail — mirrors the
        /// TotalTileCount convention SlimeHatcheryManager/
        /// TrainingRoomManager/LibraryManager/LairManager all expose.
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

        /// Number of distinct placed Jail rooms (not tiles) — read by
        /// MazeRattlerSpawner as its join requirement ("1 Jail per 5 Maze
        /// Rattlers"). A room count rather than a tile count on purpose:
        /// Jail's 5x5 minimum footprint (25+ tiles) makes a per-tile ratio
        /// meaningless for a "5 per Jail" rule the way it works for the
        /// smaller utility rooms' per-tile population caps.
        public int RoomCount => _roomTiles.Count;

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
            // Guarded by _floorVisuals/_ringFloorVisuals.ContainsKey so an
            // already-treated tile from an earlier placement is left
            // untouched rather than rebuilt.
            var allTiles = _roomTiles[roomId];
            var pitTiles = new HashSet<Vector2Int>();
            foreach (var coord in allTiles)
            {
                if (!IsPitTile(coord, origin, width, height))
                {
                    if (!_ringFloorVisuals.ContainsKey(coord))
                    {
                        _ringFloorVisuals[coord] = BuildGrateFloorVisual(coord);
                    }
                    continue;
                }

                pitTiles.Add(coord);
                _grid.SetPitDepth(coord, PitDepth);
                if (!_floorVisuals.ContainsKey(coord))
                {
                    _floorVisuals[coord] = BuildDirtFloorVisual(coord);
                }

                // A merge can promote a former ring tile straight to pit
                // (see IsPitTile's own comment) — clear whatever grate
                // floor it picked up while it was still ring, since a
                // tile is never both at once.
                if (_ringFloorVisuals.TryGetValue(coord, out var grate) && grate != null)
                {
                    Destroy(grate);
                }
                _ringFloorVisuals.Remove(coord);
            }

            _pitTilesByRoom[roomId] = pitTiles;

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

                // A prisoner held on a sold tile is simply lost, same
                // "stored contents don't get refunded" convention
                // Treasury's gold and Bacon Beacon's bacon already follow.
                if (_prisoners.ContainsKey(coord))
                {
                    ReleasePrisonerAt(coord);
                }

                if (_floorVisuals.TryGetValue(coord, out var floor) && floor != null)
                {
                    Destroy(floor);
                }
                _floorVisuals.Remove(coord);

                if (_ringFloorVisuals.TryGetValue(coord, out var grate) && grate != null)
                {
                    Destroy(grate);
                }
                _ringFloorVisuals.Remove(coord);
            }

            ClearRimStructures(roomId);
            _rimStructures.Remove(roomId);
            _pitTilesByRoom.Remove(roomId);
        }

        /// A random reachable pit tile (any placed Jail's) — read by
        /// MazeRattlerAgent's haunting behavior ("go haunt the prisoners in
        /// the jail"), same flat unweighted-random-pick shape
        /// GremlinAgent.TryBeginRoam uses for its own "wander to a random
        /// reachable floor tile" fallback.
        public bool TryFindRandomPitTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();

            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (distances.ContainsKey(coord))
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

        /// The ring's own floor treatment — a black panel with a light
        /// gray plus/cross centered on it, arms reaching to the middle of
        /// each of the tile's four sides, laid flat on the ground rather
        /// than standing upright the way the rim wall's blocks used to
        /// (see this class's own header comment). Sits flush on top of
        /// DungeonGrid's own floor cube at ordinary FloorSurfaceY — the
        /// ring is never sunk, unlike BuildDirtFloorVisual's pit tiles.
        private GameObject BuildGrateFloorVisual(Vector2Int coord)
        {
            var container = new GameObject($"JailGrateFloor_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(container.transform, false);
            panel.transform.position = basePosition;
            panel.transform.localScale = new Vector3(cellSize * GrateFloorFootprintScale, GrateFloorHeight, cellSize * GrateFloorFootprintScale);
            panel.GetComponent<Renderer>().material.color = _rimWallColor;
            Destroy(panel.GetComponent<Collider>());

            // The cross sits proud of the panel (raised further up by
            // GrateCrossReliefOffset) so it never coplanar-z-fights with
            // the panel beneath it.
            var crossY = basePosition.y + GrateFloorHeight * 0.5f + GrateCrossReliefOffset;

            var barX = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barX.name = "CrossX";
            barX.transform.SetParent(container.transform, false);
            barX.transform.position = new Vector3(basePosition.x, crossY, basePosition.z);
            barX.transform.localScale = new Vector3(cellSize, GrateCrossBarThickness, GrateCrossBarThickness);
            barX.GetComponent<Renderer>().material.color = _grateCrossColor;
            Destroy(barX.GetComponent<Collider>());

            var barZ = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barZ.name = "CrossZ";
            barZ.transform.SetParent(container.transform, false);
            barZ.transform.position = new Vector3(basePosition.x, crossY, basePosition.z);
            barZ.transform.localScale = new Vector3(GrateCrossBarThickness, GrateCrossBarThickness, cellSize);
            barZ.GetComponent<Renderer>().material.color = _grateCrossColor;
            Destroy(barZ.GetComponent<Collider>());

            return container;
        }

        /// Every outward-facing edge of coord (a pit tile) gets a short
        /// plain rim wall plus either a fence rail or — for the one
        /// designated gate edge — a staircase down into the pit flanked
        /// by gate posts instead of a fence. An edge whose neighbor is
        /// also a pit tile is an interior tile boundary, not a rim, so it
        /// gets nothing — two pit tiles sitting at the same depth have no
        /// seam to fill. Every other neighbor is one of this room's own
        /// ring tiles (the pit is always inset exactly 1 tile from the
        /// room's outer edge), never a tile outside the room entirely.
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

        /// A short, plain dark wall standing right at one outward-facing
        /// edge of coord, from ordinary ground level (FloorSurfaceY — the
        /// walkway ring's own floor height, not Rock's taller top face;
        /// using Rock's height here used to poke a visible black slab up
        /// above the ring's floor surface, since the ring is walkable
        /// Floor now, not Rock) down RimWallDepth (2 tile-heights) — just
        /// enough to close the immediate gap under the fence without any
        /// void showing through below ground, now that the pit's real rim
        /// marker is the light gray fence rather than a deep textured wall
        /// (see this class's own header comment). Inset to the tile's
        /// true edge like the fence rail rather than covering the tile's
        /// own footprint, so it never sits in front of (and hides) the
        /// sunk floor.
        private GameObject BuildRimWallVisual(Vector2Int coord, Vector2Int direction)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var outward = new Vector3(direction.x, 0f, direction.y);
            var basePosition = worldPos + outward * (cellSize * RimWallEdgeInset);
            var isEastWestEdge = direction.x != 0;
            var wallTopY = _grid.FloorSurfaceY;
            var centerY = wallTopY - RimWallDepth * 0.5f;

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = $"JailRimWall_{coord.x}_{coord.y}_{direction.x}_{direction.y}";
            wall.transform.SetParent(transform, false);
            wall.transform.position = new Vector3(basePosition.x, centerY, basePosition.z);
            wall.transform.localScale = isEastWestEdge
                ? new Vector3(RimWallThickness, RimWallDepth, cellSize * 0.98f)
                : new Vector3(cellSize * 0.98f, RimWallDepth, RimWallThickness);
            wall.GetComponent<Renderer>().material.color = _rimWallColor;
            Destroy(wall.GetComponent<Collider>());
            return wall;
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
