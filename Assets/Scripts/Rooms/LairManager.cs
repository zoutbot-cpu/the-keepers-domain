using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    public readonly struct RoomPlacedEvent
    {
        public readonly string RoomId;
        public readonly Vector2Int AnchorCoord;
        public readonly Vector3 CenterWorldPos;

        public RoomPlacedEvent(string roomId, Vector2Int anchorCoord, Vector3 centerWorldPos)
        {
            RoomId = roomId;
            AnchorCoord = anchorCoord;
            CenterWorldPos = centerWorldPos;
        }
    }

    /// Places Lair rooms on claimed, dug, empty tiles. A Lair's footprint is
    /// whatever rectangle the player drags out (see TileInteractionController)
    /// rather than a fixed size — most monsters only need a 1x1 slot, but
    /// bigger ones will need a 2x2 or larger Lair to have anywhere to rest.
    /// A placed Lair doesn't spawn or hold anything by itself; it just sits
    /// there "unclaimed" until something claims it as a resting spot (see
    /// TryClaimLair) — decoupled from implings/monsters entirely, same as
    /// before, it only announces RoomPlaced/RoomSold and lets whoever cares
    /// react.
    public class LairManager : MonoBehaviour
    {
        /// Gold cost per tile of a placed Lair — charged out of
        /// TreasuryManager's reserves (see TryPlaceLair).
        public const int CostPerTile = 5;

        // Unclaimed visual: three nested squares — a dark red "carpet" square
        // inside a lighter red ring inside a darker outer square.
        [SerializeField] private Color _unclaimedOuterColor = new Color(0.22f, 0.05f, 0.05f);
        [SerializeField] private Color _unclaimedRingColor = new Color(0.75f, 0.15f, 0.15f);
        [SerializeField] private Color _unclaimedCarpetColor = new Color(0.45f, 0.08f, 0.08f);

        // Claimed visual: a yellow "nest" — a bright rim around a duller
        // basin, same border/fill grammar TreasuryManager uses for its gold
        // tiles, just recolored so a claimed Lair reads as a bowl for a
        // monster to sleep in rather than an empty resting spot.
        [SerializeField] private Color _nestRimColor = new Color(0.85f, 0.7f, 0.15f);
        [SerializeField] private Color _nestBasinColor = new Color(0.55f, 0.44f, 0.08f);

        // Footprint fraction of a cell each layer occupies, outermost to
        // innermost — mirrors TreasuryManager's border(0.95)/fill(0.65)
        // inset convention, just extended to a third ring for the unclaimed
        // visual's "carpet inside a square inside a square" look. The outer
        // layer is a full 1.0 cell (not 0.95, unlike Treasury's border) —
        // DungeonGrid's own floor tile underneath is 0.95, so matching that
        // exactly would make the outer layer's side faces perfectly
        // coplanar with the floor tile's and z-fight/flicker at every tile
        // edge (that pink/purple flicker was the HasRoom floor color
        // showing through). A strictly larger footprint fully encloses it
        // instead, with no shared faces to fight over.
        private const float OuterFootprintScale = 1.0f;
        private const float RingFootprintScale = 0.65f;
        private const float CarpetFootprintScale = 0.35f;
        private const float NestRimFootprintScale = 1.0f;
        private const float NestBasinFootprintScale = 0.6f;

        // Same grounded-bottom trick TreasuryManager uses (see its
        // TileVisualHeight/FillHeightMargin): each successive (inner) layer
        // is HeightStep taller than the last, and raised by half of that
        // difference, so every layer's bottom lines up while each top clears
        // the one beneath it instead of z-fighting.
        private const float LayerBaseHeight = 0.17f;
        private const float LayerHeightStep = 0.03f;

        // Placement-preview markers shown while a Lair drag is in progress
        // (see UpdatePlacementPreview) — bright, and grounded per-tile (see
        // GetGroundTopY) so it reads as sitting on the actual floor/rock
        // surface rather than floating above the whole scene.
        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;

        // Rock's own visual is a full-height cube centered at y=0 (see
        // DungeonGrid.RefreshVisual), so its top face sits at 0.5 — Floor's
        // top is DungeonGrid.FloorSurfaceY instead.
        private const float RockTopY = 0.5f;

        // Sell-tool hover marker — a red "$" floating above whichever
        // room tile is currently under the pointer while Sell is armed
        // (see ShowSellPreview / TileInteractionController). Reuses the
        // same flat, iso-readable label rotation TreasuryManager's gold
        // counts use, just elevated instead of flush on the tile surface —
        // "floating above" per the brief, rather than laid on the ground.
        [SerializeField] private Color _sellPreviewColor = new Color(0.95f, 0.2f, 0.2f);
        private const float SellPreviewHeightAboveGround = 0.9f;
        private const float SellPreviewCharacterSize = 0.3f;
        private const int SellPreviewFontSize = 48;

        [SerializeField] private DungeonGrid _grid;

        private TreasuryManager _treasuryManager;
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();

        /// Claims are per-tile, not per-room — a multi-tile Lair (e.g. a
        /// 4x4) can house up to one creature per tile, each independently
        /// claiming/releasing its own square, rather than one creature
        /// claiming the whole room at once.
        private readonly HashSet<Vector2Int> _claimedTiles = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly List<GameObject> _previewMarkers = new List<GameObject>();
        private TextMesh _sellPreviewLabel;

        public event Action<RoomPlacedEvent> RoomPlaced;

        /// Fired with the sold room's id. Nothing currently listens —
        /// implings already spawned before a sold Lair keep working with the
        /// lair position they were given at spawn time, since ImplingAgent
        /// only holds a Vector3 target, not a live reference to the room.
        /// That's a known simplification: selling a Lair doesn't currently
        /// evict or reassign whatever had claimed it.
        public event Action<string> RoomSold;

        public void Initialize(DungeonGrid grid, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
        }

        /// Places a Lair spanning the rectangle between startCoord and
        /// endCoord inclusive — a plain tap (startCoord == endCoord) places
        /// the common 1x1 case, a drag places whatever bigger footprint a
        /// larger monster will need. Fails atomically: every tile in the
        /// rectangle must be valid and CostPerTile-per-tile affordable, or
        /// nothing is placed/charged.
        public bool TryPlaceLair(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceLairInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Lair exactly like TryPlaceLair but without charging
        /// gold — for terrain generation (see GameBootstrap's starting
        /// domain layout), not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury.
        public bool PlaceStartingLair(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceLairInternal(startCoord, endCoord, chargeGold: false);
        }

        private bool TryPlaceLairInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
        {
            var footprint = GetFootprint(startCoord, endCoord);
            if (!CanPlaceFootprint(footprint))
            {
                return false;
            }

            if (chargeGold && _treasuryManager != null && !_treasuryManager.TrySpendGold(footprint.Count * CostPerTile))
            {
                return false;
            }

            var roomId = $"Lair_{_nextRoomId++}";
            _roomTiles[roomId] = footprint;

            var centerWorld = Vector3.zero;
            foreach (var coord in footprint)
            {
                _grid.TryAssignRoom(coord, roomId);
                centerWorld += _grid.GridToWorld(coord);
                BuildUnclaimedVisual(coord);
            }
            centerWorld /= footprint.Count;

            var anchorCoord = new Vector2Int(Mathf.Min(startCoord.x, endCoord.x), Mathf.Min(startCoord.y, endCoord.y));
            RoomPlaced?.Invoke(new RoomPlacedEvent(roomId, anchorCoord, centerWorld));
            return true;
        }

        /// Removes whatever room owns coord (a no-op if there isn't one).
        /// Only clears the RoomId marker on its tiles — the tiles stay
        /// Claimed Floor, ready to build something else on. Refunds gold
        /// for the sold tiles at that room type's own CostPerTile (see
        /// GetCostPerTileForRoomId) back into the Treasury — the same
        /// generic Sell tool every room type shares (see
        /// TileInteractionController's GestureMode.Sell), so the refund
        /// lives here rather than duplicated in each room manager's own
        /// OnRoomSold.
        public bool TrySellRoom(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return false;
            }

            var tile = _grid.GetTile(coord);
            if (!tile.HasRoom)
            {
                return false;
            }

            var roomId = tile.RoomId;
            var clearedTileCount = _grid.RemoveRoomTiles(roomId);

            if (_roomTiles.TryGetValue(roomId, out var tiles))
            {
                foreach (var t in tiles)
                {
                    ClearTileVisual(t);
                    _claimedTiles.Remove(t);
                }
                _roomTiles.Remove(roomId);
            }

            if (_treasuryManager != null)
            {
                _treasuryManager.AddGold(clearedTileCount * GetCostPerTileForRoomId(roomId));
            }

            RoomSold?.Invoke(roomId);
            return true;
        }

        /// Which CostPerTile a sold room refunds at — determined by roomId's
        /// prefix, the same "{TypeName}_{index}" naming convention every
        /// room manager already uses to recognize its own rooms in
        /// RoomSold (see e.g. TreasuryManager.OnRoomSold's own comment).
        /// Falls back to 0 for an unrecognized prefix rather than guessing,
        /// so an unknown/future room type just refunds nothing until it's
        /// added here.
        private int GetCostPerTileForRoomId(string roomId)
        {
            if (roomId.StartsWith("Lair_")) return CostPerTile;
            if (roomId.StartsWith("Treasury_")) return TreasuryManager.CostPerTile;
            if (roomId.StartsWith("SlimeHatchery_")) return SlimeHatcheryManager.CostPerTile;
            if (roomId.StartsWith("BaconBeacon_")) return BaconBeaconManager.CostPerTile;
            if (roomId.StartsWith("TrainingRoom_")) return TrainingRoomManager.CostPerTile;
            if (roomId.StartsWith("Library_")) return LibraryManager.CostPerTile;
            if (roomId.StartsWith("Jail_")) return JailManager.CostPerTile;
            if (roomId.StartsWith("Bridge_")) return BridgeManager.CostPerTile;
            return 0;
        }

        public bool IsLairTileClaimed(Vector2Int coord)
        {
            return _claimedTiles.Contains(coord);
        }

        /// Total tile count across every placed Lair, claimed or not — read
        /// by WarlockSpawner as one of a Warlock's join requirements ("at
        /// least one lair tile"), same TotalTileCount convention
        /// SlimeHatcheryManager/TrainingRoomManager/LibraryManager use.
        /// Deliberately not filtered to unclaimed Lairs the way
        /// HasUnclaimedLair is — see WarlockSpawner.MeetsJoinRequirements.
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

        /// Whether at least one Lair tile (across every placed Lair) is
        /// currently unclaimed — read by GremlinSpawner as one of a
        /// Gremlin's join requirements ("1 free lair spot").
        public bool HasUnclaimedLair()
        {
            foreach (var tiles in _roomTiles.Values)
            {
                foreach (var coord in tiles)
                {
                    if (!_claimedTiles.Contains(coord))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// Nearest (by walking distance) unclaimed Lair tile, reachable
        /// from fromCoord — GremlinAgent/WarlockAgent check this first (see
        /// their own TryBeginPursueLair) so a creature claims an existing
        /// free tile — e.g. one of the starting Lair's from GameBootstrap —
        /// instead of always building a brand-new Lair.
        public bool TryFindNearestUnclaimedLairTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var tiles in _roomTiles.Values)
            {
                foreach (var coord in tiles)
                {
                    if (_claimedTiles.Contains(coord))
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

            return found;
        }

        /// Claims a single Lair tile as occupied by a resting monster and
        /// swaps it to the claimed "nest" visual. Fails if coord isn't part
        /// of a Lair or is already claimed.
        public bool TryClaimLairTile(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return false;
            }

            var tile = _grid.GetTile(coord);
            if (!tile.HasRoom || !_roomTiles.ContainsKey(tile.RoomId) || _claimedTiles.Contains(coord))
            {
                return false;
            }

            _claimedTiles.Add(coord);
            BuildClaimedVisual(coord);
            return true;
        }

        /// Reverses TryClaimLairTile for a single tile — e.g. once whatever
        /// had claimed it dies or moves on. Swaps it back to the unclaimed
        /// visual.
        public bool ReleaseLairTile(Vector2Int coord)
        {
            if (!_claimedTiles.Remove(coord))
            {
                return false;
            }

            BuildUnclaimedVisual(coord);
            return true;
        }

        /// Manual stand-in for a monster claiming/vacating a Lair tile —
        /// mainly useful for debug/testing (see BottomMenuBar's Toggle
        /// Claim button) since GremlinAgent/WarlockAgent drive
        /// TryClaimLairTile themselves now. A no-op if coord isn't part of
        /// a Lair.
        public bool ToggleLairClaim(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return false;
            }

            var tile = _grid.GetTile(coord);
            if (!tile.HasRoom)
            {
                return false;
            }

            return IsLairTileClaimed(coord) ? ReleaseLairTile(coord) : TryClaimLairTile(coord);
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

        /// Shows (creating on first use, then just moving/re-showing) a
        /// floating red "$" above coord — called every frame the Sell tool
        /// is armed and the pointer has a tile under it, whether or not a
        /// drag is in progress (see TileInteractionController). No-ops into
        /// ClearSellPreview if coord doesn't actually carry a room, so
        /// hovering plain floor never shows a misleading "about to sell"
        /// marker.
        public void ShowSellPreview(Vector2Int coord)
        {
            if (!_grid.InBounds(coord) || !_grid.GetTile(coord).HasRoom)
            {
                ClearSellPreview();
                return;
            }

            if (_sellPreviewLabel == null)
            {
                _sellPreviewLabel = CreateSellPreviewLabel();
            }

            _sellPreviewLabel.gameObject.SetActive(true);
            var worldPos = _grid.GridToWorld(coord);
            _sellPreviewLabel.transform.position = new Vector3(worldPos.x, _grid.FloorSurfaceY + SellPreviewHeightAboveGround, worldPos.z);
        }

        /// Hides the sell-preview marker (doesn't destroy it — ShowSellPreview
        /// just re-enables and repositions the same object next time).
        public void ClearSellPreview()
        {
            if (_sellPreviewLabel != null)
            {
                _sellPreviewLabel.gameObject.SetActive(false);
            }
        }

        private TextMesh CreateSellPreviewLabel()
        {
            var go = new GameObject("SellPreview");
            go.transform.SetParent(transform, false);
            // Same flat rotation TreasuryManager's gold-count labels use
            // (see its CreateLabel) — proven readable from the fixed iso
            // camera angle, just elevated here instead of flush on the tile.
            go.transform.rotation = Quaternion.Euler(90f, 45f, 0f);

            var textMesh = go.AddComponent<TextMesh>();
            textMesh.text = "$";
            textMesh.characterSize = SellPreviewCharacterSize;
            textMesh.fontSize = SellPreviewFontSize;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = _sellPreviewColor;
            return textMesh;
        }

        private GameObject CreatePreviewMarker(Vector2Int coord, Color color)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var centerY = GetGroundTopY(coord) + PreviewClearance + PreviewHeight * 0.5f;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"LairPreview_{coord.x}_{coord.y}";
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

        private void BuildUnclaimedVisual(Vector2Int coord)
        {
            ClearTileVisual(coord);
            var container = new GameObject($"LairUnclaimed_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            _tileVisuals[coord] = container;

            CreateInsetLayer(container.transform, coord, "Outer", OuterFootprintScale, 0, _unclaimedOuterColor);
            CreateInsetLayer(container.transform, coord, "Ring", RingFootprintScale, 1, _unclaimedRingColor);
            CreateInsetLayer(container.transform, coord, "Carpet", CarpetFootprintScale, 2, _unclaimedCarpetColor);
        }

        private void BuildClaimedVisual(Vector2Int coord)
        {
            ClearTileVisual(coord);
            var container = new GameObject($"LairNest_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            _tileVisuals[coord] = container;

            CreateInsetLayer(container.transform, coord, "Rim", NestRimFootprintScale, 0, _nestRimColor);
            CreateInsetLayer(container.transform, coord, "Basin", NestBasinFootprintScale, 1, _nestBasinColor);
        }

        private void ClearTileVisual(Vector2Int coord)
        {
            if (_tileVisuals.TryGetValue(coord, out var go) && go != null)
            {
                Destroy(go);
            }
            _tileVisuals.Remove(coord);
        }

        /// One flat inset square, grounded against the others built for the
        /// same tile (see LayerBaseHeight/LayerHeightStep) — layerIndex 0 is
        /// the outermost/shortest, increasing indices nest further in and
        /// stack taller so each one's top face clears the last.
        private void CreateInsetLayer(Transform parent, Vector2Int coord, string label, float footprintScale, int layerIndex, Color color)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;
            var height = LayerBaseHeight + layerIndex * LayerHeightStep;
            var centerY = basePosition.y + layerIndex * (LayerHeightStep * 0.5f);

            var layer = GameObject.CreatePrimitive(PrimitiveType.Cube);
            layer.name = $"Lair{label}_{coord.x}_{coord.y}";
            layer.transform.SetParent(parent, false);
            layer.transform.localPosition = new Vector3(basePosition.x, centerY, basePosition.z);
            layer.transform.localScale = new Vector3(cellSize * footprintScale, height, cellSize * footprintScale);
            layer.GetComponent<Renderer>().material.color = color;
            Destroy(layer.GetComponent<Collider>());
        }
    }
}
