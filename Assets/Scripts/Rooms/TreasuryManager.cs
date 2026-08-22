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
    public class TreasuryManager : MonoBehaviour
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

        // The fill sits this much taller than the border so its top face is
        // unambiguously above the border's, not just coplanar with it —
        // equal heights left the two z-fighting, which on most GPUs made
        // the border win and cover the fill instead of framing it.
        private const float FillHeightMargin = 0.03f;

        [SerializeField] private Color _borderColor = new Color(0.83f, 0.68f, 0.21f);
        [SerializeField] private Color _fillColor = new Color(0.5f, 0.5f, 0.5f);

        // Fraction of a cell the gray fill occupies — the remainder (both
        // sides) is the gold ring left showing underneath, i.e. the border's
        // thickness. Lower = thicker border.
        [SerializeField] private float _fillFootprintScale = 0.65f;

        [SerializeField] private float _labelSurfaceOffset = 0.01f;
        [SerializeField] private float _labelCharacterSize = 0.1f;
        [SerializeField] private int _labelFontSize = 24;
        [SerializeField] private Color _labelColor = Color.black;

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
        private int _nextRoomId;
        private readonly List<Vector2Int> _tiles = new List<Vector2Int>();
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<Vector2Int, int> _storedGold = new Dictionary<Vector2Int, int>();
        private readonly Dictionary<Vector2Int, TextMesh> _labels = new Dictionary<Vector2Int, TextMesh>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

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

        public void Initialize(DungeonGrid grid, LairManager lairManager)
        {
            _grid = grid;
            lairManager.RoomSold += OnRoomSold;
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
                _labels[coord].text = _storedGold[coord].ToString();
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
                _labels[coord].text = _storedGold[coord].ToString();
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

        /// Adds coord as a storage slot, starting at 0 gold, with its gray/
        /// gold-border tile visual and a number label on its surface. Only
        /// called from TryPlaceTreasury now — a tile without a RoomId
        /// backing it wouldn't be sellable or protected from a second room
        /// being placed on top of it, so this stays private rather than a
        /// standalone entry point.
        private void RegisterTile(Vector2Int coord)
        {
            if (_storedGold.ContainsKey(coord))
            {
                return;
            }

            _tiles.Add(coord);
            _storedGold[coord] = 0;
            _tileVisuals[coord] = CreateTileVisual(coord);
            _labels[coord] = CreateLabel(coord);
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
            _labels[coord].text = _storedGold[coord].ToString();
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
        /// "Treasury_N"). Clears every tile that room owned: visuals,
        /// label, and its stored-gold entry — that gold is simply gone,
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

                if (_labels.TryGetValue(coord, out var label) && label != null)
                {
                    Destroy(label.gameObject);
                }
                _labels.Remove(coord);

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
            marker.name = $"TreasuryPreview_{coord.x}_{coord.y}";
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

        /// Gray fill on a gold border, the same footprint convention
        /// DungeonGrid's own floor tiles use (0.95 * cellSize, see
        /// RefreshVisual) so the border sits flush with the tile edges —
        /// only the fill is inset, leaving the border ring showing round it.
        /// Returns the container so OnRoomSold can destroy exactly this
        /// tile's visual without hunting for it by name.
        private GameObject CreateTileVisual(Vector2Int coord)
        {
            var container = new GameObject($"Treasury_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
            border.name = "Border";
            border.transform.SetParent(container.transform, false);
            border.transform.position = basePosition;
            border.transform.localScale = new Vector3(cellSize * 0.95f, TileVisualHeight, cellSize * 0.95f);
            border.GetComponent<Renderer>().material.color = _borderColor;
            Destroy(border.GetComponent<Collider>());

            var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Fill";
            fill.transform.SetParent(container.transform, false);
            // Raised by half the margin so its bottom still matches the
            // border's (no gap underneath) while its top clears the
            // border's top — see FillHeightMargin.
            fill.transform.position = basePosition + Vector3.up * (FillHeightMargin * 0.5f);
            fill.transform.localScale = new Vector3(cellSize * _fillFootprintScale, TileVisualHeight + FillHeightMargin, cellSize * _fillFootprintScale);
            fill.GetComponent<Renderer>().material.color = _fillColor;
            Destroy(fill.GetComponent<Collider>());

            return container;
        }

        /// "Display the amount of gold as a number on it for now" — a plain
        /// world-space TextMesh, no Canvas/UI setup needed for a prototype
        /// placeholder. Laid flat on the tile's gray fill (rotated 90° on X
        /// so its face points up, plus the camera's 45° yaw — see
        /// IsoCameraController — so the digits read upright from the fixed
        /// iso view) rather than floating above it, and sized small (see
        /// _labelCharacterSize) so up to 3 digits clear the gold border.
        private TextMesh CreateLabel(Vector2Int coord)
        {
            var go = new GameObject($"GoldLabel_{coord.x}_{coord.y}");
            go.transform.SetParent(transform, false);
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;
            go.transform.position = basePosition + Vector3.up * (FillHeightMargin * 0.5f + (TileVisualHeight + FillHeightMargin) * 0.5f + _labelSurfaceOffset);
            go.transform.rotation = Quaternion.Euler(90f, 45f, 0f);

            var textMesh = go.AddComponent<TextMesh>();
            textMesh.text = "0";
            textMesh.characterSize = _labelCharacterSize;
            textMesh.fontSize = _labelFontSize;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = _labelColor;
            return textMesh;
        }
    }
}
