using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Owns Treasury rooms' per-tile gold storage. A Treasury is placed the
    /// same way a Lair is (see TryPlaceTreasury/TileInteractionController) —
    /// a player-buildable room, not a fixed landmark — and sold through the
    /// exact same generic Sell tool a Lair uses (LairManager.TrySellRoom
    /// only clears a tile's RoomId; RoomSold is where this class hears
    /// about it and cleans up its own gold/visual bookkeeping for that
    /// room, via OnRoomSold).
    public class TreasuryManager : MonoBehaviour, IRestorableRoomManager
    {
        private const int GoldCapacityPerTile = 500;

        /// Gold cost per tile of a player-placed Treasury (TryPlaceTreasury)
        /// — waived for exactly one tile when there's currently no Treasury
        /// tile at all (see TryPlaceTreasury), so a wiped-out player can
        /// always afford at least a 1x1 rebuild to get gold storage going
        /// again, even sitting at 0 gold.
        public const int CostPerTile = 10;

        // Slightly taller than DungeonGrid's own floor visual (0.15, see
        // RefreshVisual) so this overlay's top face sits just above it and
        // wins the z-fight instead of flickering against it.
        private const float TileVisualHeight = 0.17f;

        // Real dungeon_pack treasury floor texture (Assets/Resources/
        // Dungeon/Treasury/floor_treasury — a plain texture, no prefab/
        // material build step needed, same as DungeonGrid's own Floors
        // set), built into a real URP/Lit material once in Initialize
        // (see DungeonPackRoomArt.BuildMaterial). _borderColor is
        // fallback-only now, used only if the texture itself failed to
        // load — the tile's whole own visual, no separate fill inset on
        // top any more (removed, see CreateTileVisual).
        private Material _floorTreasuryMaterial;
        [SerializeField] private Color _borderColor = new Color(0.83f, 0.68f, 0.21f);

        // Border's own 0.95 footprint leaves a thin gap at every tile edge
        // where neighboring tiles don't quite touch — a full-cell gray
        // Seam layer underneath fills it in, sitting a bit lower than
        // Border (SeamHeight, between DungeonGrid's own 0.15 hidden-tile
        // height and Border's 0.17 — tall enough to fully hide that tile,
        // short enough that Border still visibly "pops out" above it) so
        // adjacent tiles read as flush-fitted floor panels with a mortar
        // line between them, not a void gap. Same fix already applied to
        // every other room this session.
        [SerializeField] private Color _seamColor = new Color(0.32f, 0.32f, 0.32f);
        private const float SeamFootprintScale = 1.0f;
        private const float SeamHeight = 0.16f;

        // Real dungeon_pack gold-pile meshes (Assets/Art/DungeonPack/
        // Treasury/GoldLevel1-5, built by Tools > DungeonPack > Setup
        // Props into Dungeon/Prop_TreasuryGold1-5) — "a visual for each
        // amount" replacing the old flat gray fill + number label. Index 0
        // is level 1 (1-100 gold) through index 4, level 5 (601+, not
        // currently reachable at GoldCapacityPerTile's own 500 cap — see
        // GetGoldTier). A tile at 0 gold shows no pile at all, just the
        // bare floor. See RefreshGoldPileVisual.
        private readonly GameObject[] _goldPilePrefabs = new GameObject[5];

        // Placement-preview markers while a Treasury drag is in progress —
        // same green/red valid/invalid ghost-square idea LairManager's
        // UpdatePlacementPreview uses, kept as this class's own copy rather
        // than routed through LairManager, since the marker visuals
        // themselves aren't Lair-specific but the bookkeeping (colors,
        // _previewMarkers list) is cheapest kept local to whichever manager
        // owns the room type being previewed.
        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;

        // Rock's own visual is a full-height cube centered at y=0 (see
        // DungeonGrid.RefreshVisual), so its top face sits at 0.5 — Floor's
        // top is DungeonGrid.FloorSurfaceY instead. Needed for the preview
        // marker, which can hover over undug Rock mid-drag same as a Lair
        // preview can.
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private int _ownerId;
        private int _nextRoomId;
        private readonly List<Vector2Int> _tiles = new List<Vector2Int>();
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<Vector2Int, int> _storedGold = new Dictionary<Vector2Int, int>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();

        // The gold-pile mesh currently standing on a tile (null/no entry
        // for an empty, 0-gold tile) plus which tier it represents — see
        // RefreshGoldPileVisual, which only tears down and rebuilds when
        // the tier actually changes rather than on every single deposit.
        private readonly Dictionary<Vector2Int, GameObject> _goldPileVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, int> _goldPileTiers = new Dictionary<Vector2Int, int>();

        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        /// Fired whenever a tile's stored gold actually changes (Deposit,
        /// TrySpendGold, AddGold — all funnel through RefreshGoldPileVisual)
        /// — the host's NetGame relays this to the client so its own
        /// (gold-free) TreasuryManager can mirror the pile visual instead of
        /// showing bare floor forever (see ApplyReplicatedGold).
        public event Action<Vector2Int, int> GoldChanged;

        /// Every registered tile's current stored gold — read once by the
        /// host's NetGame to catch a newly-joined client up on gold that
        /// was deposited before it connected (live deltas only cover
        /// changes from here on).
        public IEnumerable<KeyValuePair<Vector2Int, int>> StoredGoldByTile => _storedGold;

        /// Client-side mirror of a host tile's stored gold, applied off
        /// NetGame's replication rather than any real deposit/spend — a
        /// no-op if coord isn't a tile this (gold-free) TreasuryManager
        /// knows about yet (RestoreRoom/RegisterTile hasn't run for it).
        public void ApplyReplicatedGold(Vector2Int coord, int amount)
        {
            if (!_storedGold.ContainsKey(coord))
            {
                return;
            }

            _storedGold[coord] = amount;
            RefreshGoldPileVisual(coord);
        }

        /// Gold in reserves — summed across every registered Treasury tile.
        /// Read by the top-bar counter in BottomMenuBar.
        public int TotalGold
        {
            get
            {
                var total = 0;
                foreach (var amount in _storedGold.Values)
                {
                    total += amount;
                }
                return total;
            }
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, int ownerId = 0)
        {
            _grid = grid;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            _floorTreasuryMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/Treasury/floor_treasury");
            for (int level = 1; level <= _goldPilePrefabs.Length; level++)
            {
                _goldPilePrefabs[level - 1] = Resources.Load<GameObject>($"Dungeon/Prop_TreasuryGold{level}");
            }
        }

        /// Places a Treasury spanning the rectangle between startCoord and
        /// endCoord inclusive — same drag-a-footprint shape TryPlaceLair
        /// uses, and the same underlying validity rule (DungeonGrid.
        /// CanBuildRoomOn). Fails atomically: every tile in the rectangle
        /// must be valid and affordable (CostPerTile each, minus a one-tile
        /// discount when there's no existing Treasury tile — see
        /// ComputeCost) or nothing is placed/charged.
        public bool TryPlaceTreasury(Vector2Int startCoord, Vector2Int endCoord)
        {
            var footprint = GetFootprint(startCoord, endCoord);
            if (!CanPlaceFootprint(footprint))
            {
                return false;
            }

            if (!TrySpendGold(PreviewCost(footprint.Count)))
            {
                return false;
            }

            PlaceFootprint(footprint);
            return true;
        }

        /// World-setup placement for the game's starting Treasury
        /// (GameBootstrap only) — same footprint validity rule as
        /// TryPlaceTreasury but skips the gold cost, since this is terrain
        /// generation, not a player purchase.
        public bool PlaceStartingTreasury(Vector2Int startCoord, Vector2Int endCoord)
        {
            var footprint = GetFootprint(startCoord, endCoord);
            if (!CanPlaceFootprint(footprint))
            {
                return false;
            }

            PlaceFootprint(footprint);
            return true;
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingTreasury(start, end);
        }

        /// Gold cost of placing a footprint of this many tiles — every tile
        /// at CostPerTile, except when there's currently no Treasury tile
        /// at all, in which case one tile of the new footprint is free (the
        /// "always affordable at 0 gold" rebuild case — see CostPerTile).
        /// Public so BottomMenuBar can show the live cost while a Treasury
        /// drag is in progress, matching what TryPlaceTreasury will
        /// actually charge.
        public int PreviewCost(int footprintTileCount)
        {
            var billableTiles = _tiles.Count == 0 ? Mathf.Max(0, footprintTileCount - 1) : footprintTileCount;
            return billableTiles * CostPerTile;
        }

        /// Spends amount out of TotalGold if there's enough, draining
        /// across registered tiles (in no particular order — gold is
        /// fungible, tiles are just where it happens to be sitting) until
        /// amount is covered. Atomic: leaves every tile untouched if
        /// TotalGold can't cover the full amount.
        public bool TrySpendGold(int amount)
        {
            if (amount <= 0)
            {
                return true;
            }

            if (TotalGold < amount)
            {
                return false;
            }

            var remaining = amount;
            foreach (var coord in _tiles)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var current = _storedGold[coord];
                var take = Mathf.Min(current, remaining);
                if (take <= 0)
                {
                    continue;
                }

                _storedGold[coord] = current - take;
                RefreshGoldPileVisual(coord);
                remaining -= take;
            }

            return true;
        }

        /// Adds amount gold, distributed across registered tiles (in no
        /// particular order, same "gold is fungible" convention
        /// TrySpendGold uses) up to each tile's GoldCapacityPerTile — used
        /// for refunds (see LairManager.TrySellRoom) rather than depositing
        /// into one specific tile like Deposit does. Whatever doesn't fit
        /// because every tile is already full (only possible with no/tiny
        /// Treasury capacity) is simply lost — same "not currently
        /// consequential" placeholder gap as the gold a sold Treasury's own
        /// stash loses (see OnRoomSold).
        public void AddGold(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            var remaining = amount;
            foreach (var coord in _tiles)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var current = _storedGold[coord];
                var room = GoldCapacityPerTile - current;
                if (room <= 0)
                {
                    continue;
                }

                var add = Mathf.Min(room, remaining);
                _storedGold[coord] = current + add;
                RefreshGoldPileVisual(coord);
                remaining -= add;
            }
        }

        private void PlaceFootprint(List<Vector2Int> footprint)
        {
            var roomId = $"Treasury_{_nextRoomId++}";
            _roomTiles[roomId] = footprint;

            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                RegisterTile(coord);
            }
        }

        /// Adds coord as a storage slot, starting at 0 gold (empty, no
        /// gold-pile visual yet — see RefreshGoldPileVisual/GetGoldTier)
        /// with its floor tile visual. Only called from TryPlaceTreasury
        /// now — a tile without a RoomId backing it wouldn't be sellable
        /// or protected from a second room being placed on top of it, so
        /// this stays private rather than a standalone entry point.
        private void RegisterTile(Vector2Int coord)
        {
            if (_storedGold.ContainsKey(coord))
            {
                return;
            }

            _tiles.Add(coord);
            _storedGold[coord] = 0;
            _tileVisuals[coord] = CreateTileVisual(coord);
            _goldPileTiers[coord] = 0;
        }

        /// Deposits up to amount gold into coord, capped at
        /// GoldCapacityPerTile. Returns how much actually fit — the caller
        /// (ImplingAgent) is expected to keep whatever didn't.
        public int Deposit(Vector2Int coord, int amount)
        {
            if (amount <= 0 || !_storedGold.TryGetValue(coord, out var current))
            {
                return 0;
            }

            var deposited = Mathf.Min(amount, GoldCapacityPerTile - current);
            if (deposited <= 0)
            {
                return 0;
            }

            _storedGold[coord] = current + deposited;
            RefreshGoldPileVisual(coord);
            return deposited;
        }

        /// Nearest (by actual walking distance) registered tile that isn't
        /// already at capacity, reachable from fromCoord — same
        /// travel-distance flood-fill DungeonGrid already uses for job
        /// ranking. Unlike a dig/reinforce/build job's target, a Treasury
        /// tile is itself walkable Floor, so the impling can path directly
        /// onto it — no neighbor-approach step needed.
        public bool TryFindNearestTileWithRoom(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            // Impling-only (gold deposit) — Imps need a Bridge to cross
            // Water/Lava, see DungeonGrid.IsWalkable.
            var distances = _grid.GetReachableFloorDistances(fromCoord, isImp: true);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var coord in _tiles)
            {
                if (_storedGold[coord] >= GoldCapacityPerTile)
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

        /// Redraws the drag-in-progress ghost footprint between startCoord
        /// and endCoord — green if that rectangle could be placed right now,
        /// red if it couldn't (called every frame the drag moves, see
        /// TileInteractionController.ContinueGesture, so this always tears
        /// down and rebuilds rather than trying to diff against last frame).
        public void UpdatePlacementPreview(Vector2Int startCoord, Vector2Int endCoord)
        {
            ClearPlacementPreview();

            var footprint = GetFootprint(startCoord, endCoord);
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

        /// Tears down whatever ghost footprint UpdatePlacementPreview last
        /// drew — called once the drag ends (placed or not), so no stray
        /// marker survives after TileInteractionController.EndGesture.
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

        /// LairManager.RoomSold fires for every sold room regardless of
        /// kind — only react to ones that are actually ours (by roomId
        /// prefix; LairManager's own roomIds are "Lair_N", never
        /// "Treasury_N"). Clears every tile that room owned: visuals, gold
        /// pile, and its stored-gold entry — that gold is simply gone,
        /// there's no "refund the stash" mechanic.
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

                if (_goldPileVisuals.TryGetValue(coord, out var pile) && pile != null)
                {
                    Destroy(pile);
                }
                _goldPileVisuals.Remove(coord);
                _goldPileTiers.Remove(coord);

                _storedGold.Remove(coord);
                _tiles.Remove(coord);
            }

            _roomTiles.Remove(roomId);
        }

        private List<Vector2Int> GetFootprint(Vector2Int startCoord, Vector2Int endCoord)
        {
            var minX = Mathf.Min(startCoord.x, endCoord.x);
            var maxX = Mathf.Max(startCoord.x, endCoord.x);
            var minY = Mathf.Min(startCoord.y, endCoord.y);
            var maxY = Mathf.Max(startCoord.y, endCoord.y);

            var footprint = new List<Vector2Int>((maxX - minX + 1) * (maxY - minY + 1));
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
            marker.name = $"TreasuryPreview_{coord.x}_{coord.y}";
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

        /// Real dungeon_pack treasury floor (falls back to a flat gold-
        /// colored border if _floorTreasuryMaterial failed to load), same
        /// footprint convention DungeonGrid's own floor tiles use (0.95 *
        /// cellSize, see RefreshVisual) so the border sits flush with the
        /// tile edges. A full-cell gray Seam sits beneath it (see its own
        /// field header) so the gap this 0.95 footprint would otherwise
        /// leave at every tile edge reads as a mortar line instead of a
        /// void gap. No separate fill inset any more — the room's actual
        /// stored gold shows as a real pile mesh instead now (see
        /// RefreshGoldPileVisual). Returns the container so OnRoomSold can
        /// destroy exactly this tile's visual without hunting for it by
        /// name.
        private GameObject CreateTileVisual(Vector2Int coord)
        {
            var container = new GameObject($"Treasury_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seam.name = "Seam";
            seam.transform.SetParent(container.transform, false);
            seam.transform.position = basePosition;
            seam.transform.localScale = new Vector3(cellSize * SeamFootprintScale, SeamHeight, cellSize * SeamFootprintScale);
            Prims.Tint(seam, _seamColor);
            Destroy(seam.GetComponent<Collider>());

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(container.transform, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * 0.95f, TileVisualHeight, cellSize * 0.95f);
            if (_floorTreasuryMaterial != null)
            {
                // Shared, pre-built material (see DungeonPackRoomArt.
                // BuildMaterial) — no color tint, the treasury art
                // already carries its own correct look.
                border.GetComponent<Renderer>().sharedMaterial = _floorTreasuryMaterial;
            }
            else
            {
                Prims.Tint(border, _borderColor);
            }
            Destroy(border.GetComponent<Collider>());

            return container;
        }

        /// Which of the 5 gold-pile tiers (see TREASURY_README.txt)
        /// amount falls into — 0 means "empty, no pile at all". The
        /// thresholds are the pack's own bracket boundaries; tier 5
        /// (601+) isn't reachable yet at GoldCapacityPerTile's current
        /// 500 cap — deliberately left as-is (a future feature's
        /// concern, not this one's), so it's simply unused for now.
        private static int GetGoldTier(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }
            if (amount <= 100)
            {
                return 1;
            }
            if (amount <= 200)
            {
                return 2;
            }
            if (amount <= 400)
            {
                return 3;
            }
            if (amount <= 600)
            {
                return 4;
            }
            return 5;
        }

        /// Swaps coord's gold-pile mesh to match its current _storedGold
        /// amount — "a visual for each amount" replacing the old flat
        /// fill + number label. A no-op if the tile's tier hasn't actually
        /// changed (called on every single deposit/withdrawal, so this
        /// keeps a rapid string of small transfers from destroying and
        /// re-instantiating the same mesh over and over). Tier 0 (empty)
        /// leaves the tile bare, just its own floor. Gracefully does
        /// nothing beyond clearing the old mesh if the new tier's prop
        /// hasn't been set up yet (Tools > DungeonPack > Setup Props).
        private void RefreshGoldPileVisual(Vector2Int coord)
        {
            GoldChanged?.Invoke(coord, _storedGold[coord]);

            var tier = GetGoldTier(_storedGold[coord]);
            if (_goldPileTiers.TryGetValue(coord, out var currentTier) && currentTier == tier)
            {
                return;
            }
            _goldPileTiers[coord] = tier;

            if (_goldPileVisuals.TryGetValue(coord, out var existing) && existing != null)
            {
                Destroy(existing);
            }
            _goldPileVisuals.Remove(coord);

            if (tier == 0)
            {
                return;
            }

            var prefab = _goldPilePrefabs[tier - 1];
            if (prefab == null)
            {
                return;
            }

            var worldPos = _grid.GridToWorld(coord);
            var pile = Instantiate(prefab, transform, false);
            pile.name = $"TreasuryGoldPile_{coord.x}_{coord.y}";
            // Every level is pre-sized to a single tile and pivoted at
            // y=0 (per the pack's own readme) — natural scale, no
            // footprint correction needed.
            pile.transform.position = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);
            _goldPileVisuals[coord] = pile;
        }
    }
}
