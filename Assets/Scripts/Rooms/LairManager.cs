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
    public class LairManager : MonoBehaviour, IRestorableRoomManager
    {
        /// Gold cost per tile of a placed Lair — charged out of
        /// TreasuryManager's reserves (see TryPlaceLair).
        public const int CostPerTile = 5;

        // Real dungeon_pack carpet art (Assets/Resources/Dungeon/Lair/
        // CarpetTiles, see Tools > DungeonPack — these are plain textures,
        // no prefab/material build step needed, same as DungeonGrid's own
        // Floors set) — a 4-piece autotile set (center/side/outside-corner,
        // plus an inside-corner piece unused here — see SelectCarpetMaterial)
        // replacing the old flat-colored nested-square look. Each is built
        // into its own real URP/Lit material once in Initialize (see
        // BuildCarpetMaterial) rather than a texture applied at runtime to
        // whatever material GameObject.CreatePrimitive happens to hand
        // back — that implicit default isn't guaranteed URP-shaded and
        // rendered as Unity's pink/error material for TrainingRoomManager's
        // own equivalent floor layer.
        private Material _carpetCenterMaterial;
        private Material _carpetSideMaterial;
        private Material _carpetOutsideCornerMaterial;

        // The claimed "nest" is now a real mesh prop (Assets/Art/DungeonPack/
        // Lair/NestBed, built by Tools > DungeonPack > Setup Props into
        // Dungeon/Prop_NestBed) sitting on top of the same carpet floor an
        // unclaimed tile shows, instead of a separately colored flat shape —
        // see BuildNestBed. Null (skipped, carpet-only) until that tool has
        // been run at least once.
        private GameObject _nestBedPrefab;

        private const float CarpetFloorHeight = 0.17f;
        // A full 1.0 cell, not TreasuryManager's usual 0.95 border inset —
        // DungeonGrid's own (now-hidden) floor tile underneath is 0.95, so
        // matching that exactly would make this layer's side faces
        // perfectly coplanar with it and z-fight/flicker at every tile
        // edge. A strictly larger footprint fully encloses it instead, with
        // no shared faces to fight over.
        private const float CarpetFootprintScale = 1.0f;

        // nest_bed's own raw mesh already measures close to a single cell
        // (~0.756 units square, see DungeonPackPropSetup's own bounds log)
        // — scaled up slightly to read as a proper piece of furniture
        // filling most of its tile, same "scale to fit the footprint"
        // approach ThroneRoom.BuildThrone uses for the throne prop.
        private const float NestBedFootprintScale = 0.95f;

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

        /// Each placed Lair's own footprint rectangle — used purely to work
        /// out which tiles border the room's outer edge, for picking the
        /// right carpet piece (see TryGetRoomBounds/SelectCarpetTexture). A
        /// Lair never merges placements (unlike Hatchery/Tavern/Training Room/
        /// Library/Jail/ConversionClass — TryPlaceLairInternal always mints
        /// a fresh roomId), so this is always exactly the dragged rectangle,
        /// no merge-shape bookkeeping needed.
        private readonly Dictionary<string, RectInt> _roomBounds = new Dictionary<string, RectInt>();

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

            _carpetCenterMaterial = BuildCarpetMaterial("Dungeon/Lair/CarpetTiles/carpet_center");
            _carpetSideMaterial = BuildCarpetMaterial("Dungeon/Lair/CarpetTiles/carpet_side");
            _carpetOutsideCornerMaterial = BuildCarpetMaterial("Dungeon/Lair/CarpetTiles/carpet_outside_corner");
            _nestBedPrefab = Resources.Load<GameObject>("Dungeon/Prop_NestBed");
        }

        /// A real URP/Lit material with texturePath's texture baked in as
        /// its _BaseMap — built explicitly via Shader.Find, the same way
        /// every DungeonPack*Setup Editor tool builds its own materials.
        /// Null if the texture itself failed to load.
        private static Material BuildCarpetMaterial(string texturePath)
        {
            var texture = Resources.Load<Texture2D>(texturePath);
            if (texture == null)
            {
                return null;
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetTexture("_BaseMap", texture);
            return material;
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

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingLair(start, end);
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

            var minX = Mathf.Min(startCoord.x, endCoord.x);
            var maxX = Mathf.Max(startCoord.x, endCoord.x);
            var minY = Mathf.Min(startCoord.y, endCoord.y);
            var maxY = Mathf.Max(startCoord.y, endCoord.y);
            _roomBounds[roomId] = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);

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
            _roomBounds.Remove(roomId);

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
        /// added here. "BaconBeacon_" is kept alongside "Tavern_" (its
        /// renamed successor, see TavernManager) purely so a room saved
        /// under the old name before that rename still refunds correctly
        /// when sold — new Taverns always mint the "Tavern_" prefix.
        private int GetCostPerTileForRoomId(string roomId)
        {
            if (roomId.StartsWith("Lair_")) return CostPerTile;
            if (roomId.StartsWith("Treasury_")) return TreasuryManager.CostPerTile;
            if (roomId.StartsWith("SlimeHatchery_")) return SlimeHatcheryManager.CostPerTile;
            if (roomId.StartsWith("Tavern_")) return TavernManager.CostPerTile;
            if (roomId.StartsWith("BaconBeacon_")) return TavernManager.CostPerTile;
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

            BuildCarpetFloor(container.transform, coord);
        }

        private void BuildClaimedVisual(Vector2Int coord)
        {
            ClearTileVisual(coord);
            var container = new GameObject($"LairNest_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            _tileVisuals[coord] = container;

            BuildCarpetFloor(container.transform, coord);
            BuildNestBed(container.transform, coord);
        }

        /// coord's own placed Lair's footprint rectangle, resolved via the
        /// grid tile's own RoomId (the same info TryClaimLairTile already
        /// reads) rather than threading a roomId through every visual call
        /// site. False if coord isn't currently part of a tracked Lair (e.g.
        /// mid-placement bookkeeping edge cases) — callers just fall back to
        /// the plain center tile in that case rather than guessing a shape.
        private bool TryGetRoomBounds(Vector2Int coord, out RectInt bounds)
        {
            var roomId = _grid.GetTile(coord).RoomId;
            return _roomBounds.TryGetValue(roomId, out bounds);
        }

        /// The tile's actual floor — one real dungeon_pack carpet quad,
        /// materialed and rotated per SelectCarpetMaterial, replacing
        /// RefreshVisual's own now-hidden isPlainFloor look (which never
        /// applies once a Lair covers the tile) the same way every other
        /// room manager's own ground layer does.
        private void BuildCarpetFloor(Transform parent, Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;
            var centerY = basePosition.y + CarpetFloorHeight * 0.5f;

            var material = SelectCarpetMaterial(coord, out var yRotationDegrees);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = $"LairCarpet_{coord.x}_{coord.y}";
            floor.transform.SetParent(parent, false);
            floor.transform.localPosition = new Vector3(basePosition.x, centerY, basePosition.z);
            floor.transform.localRotation = Quaternion.Euler(0f, yRotationDegrees, 0f);
            floor.transform.localScale = new Vector3(cellSize * CarpetFootprintScale, CarpetFloorHeight, cellSize * CarpetFootprintScale);

            if (material != null)
            {
                // Shared, pre-built material (see BuildCarpetMaterial).
                floor.GetComponent<Renderer>().sharedMaterial = material;
            }

            Destroy(floor.GetComponent<Collider>());
        }

        /// Which carpet material coord's tile needs, and how far to rotate
        /// it around Y so its baked-in border faces the right way — a real
        /// rectangle corner (two perpendicular touching sides) takes
        /// priority over the plain-edge case. Assumes the source art's
        /// default (0°) orientation borders the tile's north and west edges
        /// (carpet_outside_corner) / north edge alone (carpet_side) — that's
        /// this session's best read of the art with no in-Editor render to
        /// confirm against, so a systematic off-by-90°/mirrored look is the
        /// first thing to check here if it doesn't read right once seen.
        /// A 1-wide/1-tall room's tiles (which can touch two *parallel*
        /// sides at once, not caught by any of these four cases) fall
        /// through to whichever single side matches first instead — no
        /// dedicated art exists for that shape in this set.
        private Material SelectCarpetMaterial(Vector2Int coord, out float yRotationDegrees)
        {
            yRotationDegrees = 0f;
            if (!TryGetRoomBounds(coord, out var bounds))
            {
                return _carpetCenterMaterial;
            }

            var touchesWest = coord.x == bounds.xMin;
            var touchesEast = coord.x == bounds.xMax - 1;
            var touchesSouth = coord.y == bounds.yMin;
            var touchesNorth = coord.y == bounds.yMax - 1;

            if (touchesNorth && touchesWest) { yRotationDegrees = 0f; return _carpetOutsideCornerMaterial; }
            if (touchesNorth && touchesEast) { yRotationDegrees = 90f; return _carpetOutsideCornerMaterial; }
            if (touchesSouth && touchesEast) { yRotationDegrees = 180f; return _carpetOutsideCornerMaterial; }
            if (touchesSouth && touchesWest) { yRotationDegrees = 270f; return _carpetOutsideCornerMaterial; }

            if (touchesNorth) { yRotationDegrees = 0f; return _carpetSideMaterial; }
            if (touchesEast) { yRotationDegrees = 90f; return _carpetSideMaterial; }
            if (touchesSouth) { yRotationDegrees = 180f; return _carpetSideMaterial; }
            if (touchesWest) { yRotationDegrees = 270f; return _carpetSideMaterial; }

            return _carpetCenterMaterial;
        }

        /// The real nest_bed prop, scaled to fit within coord's tile and
        /// sitting on top of the carpet floor BuildCarpetFloor already laid
        /// down — replaces the old flat-colored "nest" shape entirely. A
        /// no-op (carpet-only tile, no visible change from unclaimed) until
        /// Tools > DungeonPack > Setup Props has built Dungeon/Prop_NestBed
        /// at least once.
        private void BuildNestBed(Transform parent, Vector2Int coord)
        {
            if (_nestBedPrefab == null)
            {
                return;
            }

            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);

            var bed = Instantiate(_nestBedPrefab, parent, false);
            bed.name = "NestBed";

            var renderers = bed.GetComponentsInChildren<Renderer>();
            var scale = 1f;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                var footprint = Mathf.Max(bounds.size.x, bounds.size.z);
                if (footprint > 0.01f)
                {
                    scale = (cellSize * NestBedFootprintScale) / footprint;
                }
            }

            bed.transform.localScale = Vector3.one * scale;
            bed.transform.localPosition = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z);
        }

        private void ClearTileVisual(Vector2Int coord)
        {
            if (_tileVisuals.TryGetValue(coord, out var go) && go != null)
            {
                Destroy(go);
            }
            _tileVisuals.Remove(coord);
        }
    }
}
