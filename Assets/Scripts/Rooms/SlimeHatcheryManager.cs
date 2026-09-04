using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Breeds slimes in a coop structure sitting on one tile of the room —
    /// "food for barbaric creatures" per the design doc. Placed the same
    /// drag-a-footprint way a Lair/Treasury is (see TryPlaceHatchery), but
    /// with a hard 3x3 minimum enforced (unlike Lair/Treasury, which accept
    /// anything CanBuildRoomOn allows) and a fixed single-tile coop
    /// structure whose position depends on the footprint's shape — see
    /// GetStructureCoord. Bred slimes are real wandering SlimeAgent
    /// instances, not an abstract count — implings haul them out to a
    /// Tavern (see ImplingAgent's hauling states) rather than anything
    /// being consumed here directly.
    public class SlimeHatcheryManager : MonoBehaviour, IRestorableRoomManager
    {
        private const int MinFootprintSize = 3;

        /// Gold cost per tile of a placed Slime Hatchery — charged out of
        /// TreasuryManager's reserves (see TryPlaceHatchery).
        public const int CostPerTile = 15;

        /// A single, centered coop tile only has a true center when both
        /// dimensions are odd. Otherwise ("no middlepoint") the design doc
        /// calls for the coop to sit one tile in from the top-right corner
        /// instead.
        private const int StructureSize = 1;

        private const float BreedIntervalSeconds = 2f;

        // Real dungeon_pack mesh (Assets/Art/DungeonPack/SlimeHatchery/
        // ChickenCoop, built by Tools > DungeonPack > Setup Props into
        // Dungeon/Prop_ChickenCoop) — a wood-plank coop with a dark roof,
        // already close to a single tile's footprint. Falls back to the
        // original primitive-built box+roof below if Prop_ChickenCoop
        // hasn't been set up yet, same graceful-degradation pattern
        // ThroneRoom.BuildThrone uses for the throne prop.
        private GameObject _chickenCoopPrefab;
        private const float ChickenCoopFootprintScale = 0.9f;

        // Fallback-only primitive coop: a light-wood crate with a darker
        // slanted-look roof cap on top, used only if Prop_ChickenCoop
        // hasn't been set up yet. See BuildCoopVisual.
        [SerializeField] private Color _coopBoxColor = new Color(0.55f, 0.42f, 0.25f);
        [SerializeField] private Color _coopRoofColor = new Color(0.35f, 0.24f, 0.14f);
        private const float CoopBoxFootprintScale = 0.75f;
        private const float CoopBoxHeight = 0.4f;
        private const float CoopRoofFootprintScale = 0.85f;
        private const float CoopRoofHeight = 0.12f;

        // Real dungeon_pack meadow floor texture (Assets/Resources/Dungeon/
        // SlimeHatchery/floor_meadow_logs — a plain texture, no prefab/
        // material build step needed, same as DungeonGrid's own Floors
        // set), built into a real URP/Lit material once in Initialize (see
        // DungeonPackRoomArt.BuildMaterial for why — an implicit-default-
        // material + runtime SetTexture attempt rendered pink here). Same
        // layering trick TreasuryManager uses for its gold tiles (taller
        // than DungeonGrid's own 0.15 floor visual so its top face wins
        // the z-fight instead of flickering).
        private Material _floorMeadowMaterial;

        // Fallback-only flat color, used only if the meadow texture itself
        // failed to load (_floorMeadowMaterial null) — without this the
        // Ground tile below would render with Unity's own untouched
        // default primitive material (flat gray/white) instead of any
        // deliberate color. See BuildGroundVisual.
        [SerializeField] private Color _groundColor = new Color(0.42f, 0.29f, 0.16f);
        private const float GroundTileHeight = 0.17f;

        // One continuous field spanning the whole room rectangle now (see
        // BuildGroundVisual) instead of a per-tile grid of textured
        // squares — rooms of this type always merge into an exact
        // rectangle (see TryFindMergeableRoom), so there's no per-tile
        // gap to hide and no Seam layer is needed here any more. Instead,
        // a scatter of small brown dirt patches (see BuildSpots) sits on
        // top of the field for texture variation.
        [SerializeField] private Color _spotColor = new Color(0.33f, 0.22f, 0.12f);
        private const float SpotHeight = 0.02f;
        private const float SpotMinRadiusScale = 0.15f;
        private const float SpotMaxRadiusScale = 0.35f;
        private const float SpotDensityPerTile = 0.35f;

        // Perimeter fence: a small log laid flat along every footprint
        // edge that borders a tile outside the room — an edge shared with
        // another footprint tile is interior and gets no log, so this
        // naturally traces just the outer boundary of the whole room
        // rather than every individual tile.
        [SerializeField] private Color _fenceLogColor = new Color(0.4f, 0.27f, 0.15f);
        private const float FenceLogRadius = 0.06f;
        private const float FenceLogLengthScale = 0.8f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private int _ownerId;

        // Off in the Level Designer (see Initialize's simulateBreeding
        // param) so placing/loading a Hatchery there never starts spawning
        // real SlimeAgents while the map is just being edited — breeding
        // is gameplay simulation, not something a level's saved state
        // should carry.
        private bool _simulateBreeding = true;

        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, Vector2Int> _structureCoords = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<string, List<SlimeAgent>> _liveSlimes = new Dictionary<string, List<SlimeAgent>>();
        private readonly Dictionary<string, float> _breedTimers = new Dictionary<string, float>();
        private readonly Dictionary<Vector2Int, string> _structureRoomByCoord = new Dictionary<Vector2Int, string>();
        private readonly Dictionary<Vector2Int, GameObject> _structureVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<string, GameObject> _groundVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, GameObject> _fenceVisuals = new Dictionary<string, GameObject>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager, bool simulateBreeding = true, int ownerId = 0)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            _simulateBreeding = simulateBreeding;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            _chickenCoopPrefab = Resources.Load<GameObject>("Dungeon/Prop_ChickenCoop");
            _floorMeadowMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/SlimeHatchery/floor_meadow_logs");
        }

        /// Total tile count across every placed Slime Hatchery — read by
        /// GremlinSpawner as one of a Gremlin's join requirements ("fewer
        /// non-Imp creatures than Hatchery tiles").
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

        private void Update()
        {
            if (!_simulateBreeding || _roomTiles.Count == 0)
            {
                return;
            }

            foreach (var roomId in _roomTiles.Keys)
            {
                TickBreeding(roomId);
            }
        }

        /// Cap is 1 slime per tile the room actually occupies — a bigger
        /// Hatchery supports more slimes wandering it at once, not a flat
        /// number. Once capped, the timer is pinned at exactly
        /// BreedIntervalSeconds (not left to keep accumulating) so freeing
        /// up a slot — an impling collecting one — spawns a replacement on
        /// the very next tick instead of after a further backlog.
        private void TickBreeding(string roomId)
        {
            var timer = _breedTimers[roomId] + Time.deltaTime;
            if (timer < BreedIntervalSeconds)
            {
                _breedTimers[roomId] = timer;
                return;
            }

            if (_liveSlimes[roomId].Count >= _roomTiles[roomId].Count)
            {
                _breedTimers[roomId] = BreedIntervalSeconds;
                return;
            }

            _breedTimers[roomId] = timer - BreedIntervalSeconds;
            SpawnSlime(roomId);
        }

        private void SpawnSlime(string roomId)
        {
            var spawnCoord = _structureCoords[roomId];
            var go = new GameObject($"Slime_{roomId}");
            go.transform.SetParent(transform, false);

            var agent = go.AddComponent<SlimeAgent>();
            agent.Initialize(_grid, this, roomId, _roomTiles[roomId], spawnCoord);
            _liveSlimes[roomId].Add(agent);
        }

        /// Places a Slime Hatchery spanning the rectangle between startCoord
        /// and endCoord inclusive. Fails atomically, same as
        /// LairManager.TryPlaceLair, and additionally rejects anything
        /// smaller than MinFootprintSize in either dimension — a rule the
        /// design doc calls out specifically for this room, unlike Lair/
        /// Treasury which have no stated minimum.
        public bool TryPlaceHatchery(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceHatcheryInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Slime Hatchery exactly like TryPlaceHatchery but
        /// without charging gold — for terrain generation (see
        /// GameBootstrap's starting domain layout), not a player purchase,
        /// same as TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingHatchery(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceHatcheryInternal(startCoord, endCoord, chargeGold: false);
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingHatchery(start, end);
        }

        private bool TryPlaceHatcheryInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
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

            // Extends an existing Slime Hatchery instead of starting a
            // separate one if footprint exactly completes a rectangle
            // together with it — see TryFindMergeableRoom. Live
            // slimes/breed timer carry over untouched (still keyed by the
            // same roomId, and _roomTiles[roomId] is the same List instance
            // every live SlimeAgent already holds a reference to — growing
            // it in place immediately widens their wander bounds too); only
            // the coop, fence, and field — which all depend on the room's
            // overall shape — are torn down and rebuilt below.
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearStructure(roomId);
                ClearFence(roomId);
                ClearGround(roomId);
            }
            else
            {
                roomId = $"SlimeHatchery_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
                _liveSlimes[roomId] = new List<SlimeAgent>();
                _breedTimers[roomId] = 0f;
            }

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
            }

            _groundVisuals[roomId] = BuildGroundVisual(roomId, origin, width, height);

            var structureCoord = GetStructureCoord(origin, width, height);
            _structureCoords[roomId] = structureCoord;
            _structureRoomByCoord[structureCoord] = roomId;
            _structureVisuals[structureCoord] = BuildCoopVisual(structureCoord);
            _fenceVisuals[roomId] = BuildFenceVisual(_roomTiles[roomId]);

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

        /// Tears down this room's current coop structure — used right
        /// before rebuilding it at the recentered spot for a new (possibly
        /// merged, larger) footprint.
        private void ClearStructure(string roomId)
        {
            if (!_structureCoords.TryGetValue(roomId, out var coord))
            {
                return;
            }

            if (_structureVisuals.TryGetValue(coord, out var visual) && visual != null)
            {
                Destroy(visual);
            }
            _structureVisuals.Remove(coord);
            _structureRoomByCoord.Remove(coord);
            _structureCoords.Remove(roomId);
        }

        /// Tears down this room's current perimeter fence — used right
        /// before rebuilding it for a new (possibly merged, larger)
        /// footprint, since the fence traces the whole room's outer
        /// boundary.
        private void ClearFence(string roomId)
        {
            if (_fenceVisuals.TryGetValue(roomId, out var fence) && fence != null)
            {
                Destroy(fence);
            }
            _fenceVisuals.Remove(roomId);
        }

        /// Tears down this room's current field visual — used right
        /// before rebuilding it for a new (possibly merged, larger)
        /// footprint, since the field spans the whole room's rectangle.
        /// Same shape as ClearStructure/ClearFence.
        private void ClearGround(string roomId)
        {
            if (_groundVisuals.TryGetValue(roomId, out var ground) && ground != null)
            {
                Destroy(ground);
            }
            _groundVisuals.Remove(roomId);
        }

        /// The coop's own tile — centered when both dimensions are odd
        /// (there's a real middle tile), otherwise one tile in from the
        /// top-right corner on each axis, per the design doc's "no
        /// middlepoint" fallback.
        private Vector2Int GetStructureCoord(Vector2Int origin, int width, int height)
        {
            var hasCenter = width % 2 == 1 && height % 2 == 1;
            if (hasCenter)
            {
                return origin + new Vector2Int(width / 2, height / 2);
            }

            return origin + new Vector2Int(width - 1 - StructureSize, height - 1 - StructureSize);
        }

        /// Nearest (by walking distance) hatchery coop tile that currently
        /// has at least one live slime ready to collect, reachable from
        /// fromCoord — same shape as TreasuryManager.TryFindNearestTileWithRoom.
        public bool TryFindReadyHatchery(Vector2Int fromCoord, out Vector2Int structureCoord)
        {
            // Impling-only (hauling) — Imps need a Bridge to cross
            // Water/Lava, see DungeonGrid.IsWalkable.
            var distances = _grid.GetReachableFloorDistances(fromCoord, isImp: true);
            var bestDistance = int.MaxValue;
            structureCoord = default;
            var found = false;

            foreach (var entry in _structureCoords)
            {
                if (!_liveSlimes.TryGetValue(entry.Key, out var live) || live.Count <= 0)
                {
                    continue;
                }

                if (distances.TryGetValue(entry.Value, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    structureCoord = entry.Value;
                    found = true;
                }
            }

            return found;
        }

        /// Collects up to maxAmount live slimes from the hatchery whose
        /// coop sits at structureCoord — nearest to the coop first, for
        /// plausibility — destroying each one's wandering visual as it's
        /// picked up. Returns how many were actually taken; callers
        /// (ImplingAgent) add that straight to their inventory.
        public int CollectSlime(Vector2Int structureCoord, int maxAmount)
        {
            if (maxAmount <= 0 || !_structureRoomByCoord.TryGetValue(structureCoord, out var roomId) || !_liveSlimes.TryGetValue(roomId, out var live))
            {
                return 0;
            }

            var collectCount = Mathf.Min(maxAmount, live.Count);
            if (collectCount <= 0)
            {
                return 0;
            }

            var coopWorldPos = _grid.GridToWorld(structureCoord);
            live.Sort((a, b) => Vector3.Distance(a.transform.position, coopWorldPos).CompareTo(Vector3.Distance(b.transform.position, coopWorldPos)));

            for (int i = 0; i < collectCount; i++)
            {
                var slime = live[0];
                live.RemoveAt(0);
                if (slime != null)
                {
                    Destroy(slime.gameObject);
                }
            }

            return collectCount;
        }

        /// Keeps _liveSlimes in sync when a slime disappears on its own
        /// (see SlimeAgent.Update's own-tile check) rather than through
        /// CollectSlime, which already removes from this list itself.
        public void NotifySlimeDestroyed(string roomId, SlimeAgent agent)
        {
            if (_liveSlimes.TryGetValue(roomId, out var live))
            {
                live.Remove(agent);
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
        private void OnRoomSold(string roomId)
        {
            if (!_roomTiles.Remove(roomId))
            {
                return;
            }

            ClearGround(roomId);

            if (_fenceVisuals.TryGetValue(roomId, out var fence) && fence != null)
            {
                Destroy(fence);
            }
            _fenceVisuals.Remove(roomId);

            if (_structureCoords.TryGetValue(roomId, out var structureCoord))
            {
                if (_structureVisuals.TryGetValue(structureCoord, out var visual) && visual != null)
                {
                    Destroy(visual);
                }
                _structureVisuals.Remove(structureCoord);
                _structureRoomByCoord.Remove(structureCoord);
                _structureCoords.Remove(roomId);
            }

            // Not strictly necessary for correctness — every live slime
            // notices its own tile no longer belongs to this room on its
            // very next Update and disappears on its own (see SlimeAgent) —
            // but destroying them immediately here avoids one stray frame
            // of blue balls standing on what's now plain claimed floor.
            if (_liveSlimes.TryGetValue(roomId, out var live))
            {
                foreach (var slime in live)
                {
                    if (slime != null)
                    {
                        Destroy(slime.gameObject);
                    }
                }
                _liveSlimes.Remove(roomId);
            }

            _breedTimers.Remove(roomId);
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
            marker.name = $"HatcheryPreview_{coord.x}_{coord.y}";
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

        /// One continuous dungeon_pack-textured meadow field spanning the
        /// room's whole rectangle (rooms of this type always merge into an
        /// exact rectangle — see TryFindMergeableRoom) instead of the old
        /// per-tile grid of separately-bordered squares, with a scatter of
        /// brown dirt spots (see BuildSpots) laid on top of it. Same
        /// taller-than-Floor layering trick TreasuryManager's gold tiles
        /// use, just sized to the whole room instead of one tile.
        private GameObject BuildGroundVisual(string roomId, Vector2Int origin, int width, int height)
        {
            var container = new GameObject($"HatcheryField_{roomId}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var minCornerX = origin.x * cellSize;
            var minCornerZ = origin.y * cellSize;
            var fieldWidth = width * cellSize;
            var fieldDepth = height * cellSize;
            var basePosition = new Vector3(minCornerX + fieldWidth * 0.5f, 0f, minCornerZ + fieldDepth * 0.5f) + Vector3.down * 0.5f;

            var field = GameObject.CreatePrimitive(PrimitiveType.Cube);
            field.name = "Field";
            field.transform.SetParent(container.transform, false);
            field.transform.position = basePosition;
            field.transform.localScale = new Vector3(fieldWidth, GroundTileHeight, fieldDepth);
            if (_floorMeadowMaterial != null)
            {
                // A per-room instance (not the shared material directly —
                // see DungeonPackRoomArt.BuildMaterial) since each room's
                // texture tiling below depends on its own size; no color
                // tint, the meadow art already carries its own correct
                // look.
                var fieldMaterial = new Material(_floorMeadowMaterial);
                fieldMaterial.mainTextureScale = new Vector2(width, height);
                field.GetComponent<Renderer>().material = fieldMaterial;
            }
            else
            {
                // Fallback flat color — see _groundColor's own header for
                // why this branch exists at all.
                Prims.Tint(field, _groundColor);
            }
            Destroy(field.GetComponent<Collider>());

            BuildSpots(container.transform, basePosition.y, minCornerX, minCornerZ, width, height, fieldWidth, fieldDepth);

            return container;
        }

        /// A scatter of small brown dirt patches laid on top of the field
        /// — stands in for the old per-tile texture-grid look with
        /// something that reads as one continuous meadow instead of a
        /// checkerboard. Count and placement come from a System.Random
        /// seeded off the room's own origin/size, so a given rectangle
        /// always gets the same-looking scatter rather than reshuffling on
        /// every rebuild (e.g. RestoreRoom on load).
        private void BuildSpots(Transform parent, float fieldCenterY, float minCornerX, float minCornerZ, int width, int height, float fieldWidth, float fieldDepth)
        {
            var cellSize = _grid.CellSize;
            var spotY = fieldCenterY + GroundTileHeight * 0.5f + SpotHeight * 0.5f;
            var rng = new System.Random(unchecked(minCornerX.GetHashCode() * 92821 + minCornerZ.GetHashCode() * 68917 + width * 1237 + height * 7919));
            var spotCount = Mathf.Max(3, Mathf.RoundToInt(width * height * SpotDensityPerTile));

            for (int i = 0; i < spotCount; i++)
            {
                var spotX = minCornerX + (float)rng.NextDouble() * fieldWidth;
                var spotZ = minCornerZ + (float)rng.NextDouble() * fieldDepth;
                var radius = cellSize * Mathf.Lerp(SpotMinRadiusScale, SpotMaxRadiusScale, (float)rng.NextDouble());

                var spot = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                spot.name = $"Spot_{i}";
                spot.transform.SetParent(parent, false);
                spot.transform.position = new Vector3(spotX, spotY, spotZ);
                spot.transform.localScale = new Vector3(radius * 2f, SpotHeight * 0.5f, radius * 2f);
                Prims.Tint(spot, _spotColor);
                Destroy(spot.GetComponent<Collider>());
            }
        }

        /// One small log per footprint edge that borders a tile outside
        /// the room — an edge shared with another footprint tile is
        /// interior and is skipped, so only the room's true outer boundary
        /// gets fenced.
        private GameObject BuildFenceVisual(List<Vector2Int> footprint)
        {
            var footprintSet = new HashSet<Vector2Int>(footprint);
            var container = new GameObject("HatcheryFence");
            container.transform.SetParent(transform, false);

            foreach (var coord in footprint)
            {
                foreach (var direction in GridDirections.Cardinal)
                {
                    if (footprintSet.Contains(coord + direction))
                    {
                        continue;
                    }

                    BuildFenceLog(container.transform, coord, direction);
                }
            }

            return container;
        }

        /// A short cylinder laid flat across one tile's outer edge —
        /// rotated so its long axis runs along the edge (tangent to
        /// direction) rather than across it.
        private void BuildFenceLog(Transform parent, Vector2Int coord, Vector2Int direction)
        {
            var cellSize = _grid.CellSize;
            var edgeCenter = _grid.GridToWorld(coord) + new Vector3(direction.x, 0f, direction.y) * (cellSize * 0.5f);

            var log = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            log.name = $"FenceLog_{coord.x}_{coord.y}";
            log.transform.SetParent(parent, false);
            log.transform.position = new Vector3(edgeCenter.x, _grid.FloorSurfaceY + FenceLogRadius, edgeCenter.z);
            // Up/down edges run along X, left/right edges run along Z —
            // rotate the cylinder's default vertical axis onto whichever
            // one is tangent to this edge.
            log.transform.rotation = direction.y != 0 ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(90f, 0f, 0f);
            // Cylinder primitive is 2 units tall at scale 1, so the height
            // scale needed for a world-space length is half that length.
            log.transform.localScale = new Vector3(FenceLogRadius * 2f, cellSize * FenceLogLengthScale * 0.5f, FenceLogRadius * 2f);
            Prims.Tint(log, _fenceLogColor);
            Destroy(log.GetComponent<Collider>());
        }

        /// The real chicken_coop prop, scaled to fit within coord's tile —
        /// falls back to the original primitive-built box+roof below if
        /// Prop_ChickenCoop hasn't been set up yet.
        private GameObject BuildCoopVisual(Vector2Int coord)
        {
            var container = new GameObject($"SlimeCoop_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);

            if (_chickenCoopPrefab != null)
            {
                var coop = Instantiate(_chickenCoopPrefab, container.transform, false);
                coop.name = "Coop";

                var scale = DungeonPackRoomArt.ComputeUniformScaleToFootprint(coop, cellSize * ChickenCoopFootprintScale);
                coop.transform.localScale = Vector3.one * scale;
                coop.transform.localPosition = basePosition;
                return container;
            }

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "CoopBox";
            box.transform.SetParent(container.transform, false);
            box.transform.position = basePosition + Vector3.up * (CoopBoxHeight * 0.5f);
            box.transform.localScale = new Vector3(cellSize * CoopBoxFootprintScale, CoopBoxHeight, cellSize * CoopBoxFootprintScale);
            Prims.Tint(box, _coopBoxColor);
            Destroy(box.GetComponent<Collider>());

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "CoopRoof";
            roof.transform.SetParent(container.transform, false);
            roof.transform.position = basePosition + Vector3.up * (CoopBoxHeight + CoopRoofHeight * 0.5f);
            roof.transform.localScale = new Vector3(cellSize * CoopRoofFootprintScale, CoopRoofHeight, cellSize * CoopRoofFootprintScale);
            Prims.Tint(roof, _coopRoofColor);
            Destroy(roof.GetComponent<Collider>());

            return container;
        }
    }
}
