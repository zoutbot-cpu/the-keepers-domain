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
    /// BaconBeacon (see ImplingAgent's hauling states) rather than anything
    /// being consumed here directly.
    public class SlimeHatcheryManager : MonoBehaviour
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

        // Coop box: a light-wood crate with a darker slanted-look roof cap
        // on top — cheap with primitives, distinct from Treasury's flat
        // gold-tile look and Lair's nested-square look.
        [SerializeField] private Color _coopBoxColor = new Color(0.55f, 0.42f, 0.25f);
        [SerializeField] private Color _coopRoofColor = new Color(0.35f, 0.24f, 0.14f);
        private const float CoopBoxFootprintScale = 0.75f;
        private const float CoopBoxHeight = 0.4f;
        private const float CoopRoofFootprintScale = 0.85f;
        private const float CoopRoofHeight = 0.12f;

        // Ground overlay: a flat dirt-brown fill on every footprint tile
        // (including the coop's own) — same layering trick TreasuryManager
        // uses for its gold tiles (taller than DungeonGrid's own 0.15 floor
        // visual so its top face wins the z-fight instead of flickering).
        [SerializeField] private Color _groundColor = new Color(0.42f, 0.29f, 0.16f);
        private const float GroundTileHeight = 0.17f;
        private const float GroundFootprintScale = 0.95f;

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
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, Vector2Int> _structureCoords = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<string, List<SlimeAgent>> _liveSlimes = new Dictionary<string, List<SlimeAgent>>();
        private readonly Dictionary<string, float> _breedTimers = new Dictionary<string, float>();
        private readonly Dictionary<Vector2Int, string> _structureRoomByCoord = new Dictionary<Vector2Int, string>();
        private readonly Dictionary<Vector2Int, GameObject> _structureVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, GameObject> _groundVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<string, GameObject> _fenceVisuals = new Dictionary<string, GameObject>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
        }

        private void Update()
        {
            if (_roomTiles.Count == 0)
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
            var footprint = GetFootprint(startCoord, endCoord, out var width, out var height, out var origin);
            if (width < MinFootprintSize || height < MinFootprintSize || !CanPlaceFootprint(footprint))
            {
                return false;
            }

            if (_treasuryManager != null && !_treasuryManager.TrySpendGold(footprint.Count * CostPerTile))
            {
                return false;
            }

            var roomId = $"SlimeHatchery_{_nextRoomId++}";
            _roomTiles[roomId] = footprint;
            _liveSlimes[roomId] = new List<SlimeAgent>();
            _breedTimers[roomId] = 0f;

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                _groundVisuals[coord] = BuildGroundVisual(coord);
            }

            var structureCoord = GetStructureCoord(origin, width, height);
            _structureCoords[roomId] = structureCoord;
            _structureRoomByCoord[structureCoord] = roomId;
            _structureVisuals[structureCoord] = BuildCoopVisual(structureCoord);
            _fenceVisuals[roomId] = BuildFenceVisual(footprint);

            return true;
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
            var distances = _grid.GetReachableFloorDistances(fromCoord);
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
            marker.name = $"HatcheryPreview_{coord.x}_{coord.y}";
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

        /// Flat dirt-brown fill covering the tile — same footprint/height
        /// convention TreasuryManager.CreateTileVisual uses for its gold
        /// tiles (0.95 * cellSize, taller than DungeonGrid's own 0.15 Floor
        /// visual), just a single solid color rather than a border/fill
        /// pair since there's no per-tile stored amount to frame here.
        private GameObject BuildGroundVisual(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = $"HatcheryGround_{coord.x}_{coord.y}";
            ground.transform.SetParent(transform, false);
            ground.transform.position = basePosition;
            ground.transform.localScale = new Vector3(cellSize * GroundFootprintScale, GroundTileHeight, cellSize * GroundFootprintScale);
            ground.GetComponent<Renderer>().material.color = _groundColor;
            Destroy(ground.GetComponent<Collider>());

            return ground;
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
            log.GetComponent<Renderer>().material.color = _fenceLogColor;
            Destroy(log.GetComponent<Collider>());
        }

        private GameObject BuildCoopVisual(Vector2Int coord)
        {
            var container = new GameObject($"SlimeCoop_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var basePosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "CoopBox";
            box.transform.SetParent(container.transform, false);
            box.transform.position = basePosition + Vector3.up * (CoopBoxHeight * 0.5f);
            box.transform.localScale = new Vector3(cellSize * CoopBoxFootprintScale, CoopBoxHeight, cellSize * CoopBoxFootprintScale);
            box.GetComponent<Renderer>().material.color = _coopBoxColor;
            Destroy(box.GetComponent<Collider>());

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "CoopRoof";
            roof.transform.SetParent(container.transform, false);
            roof.transform.position = basePosition + Vector3.up * (CoopBoxHeight + CoopRoofHeight * 0.5f);
            roof.transform.localScale = new Vector3(cellSize * CoopRoofFootprintScale, CoopRoofHeight, cellSize * CoopRoofFootprintScale);
            roof.GetComponent<Renderer>().material.color = _coopRoofColor;
            Destroy(roof.GetComponent<Collider>());

            return container;
        }
    }
}
