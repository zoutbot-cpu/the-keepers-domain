using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Rooms
{
    /// Lets creatures (Imps included) cross Water/Lava tiles that would
    /// otherwise block them (see DungeonGrid.IsWalkable) — placed with a
    /// line-paint gesture (BuildMode.Bridge, see TileInteractionController)
    /// rather than the rectangle-drag every other room uses, since a bridge
    /// is naturally a 1-wide path rather than a footprint. Each bridge tile
    /// is its own independent "room" (roomId "Bridge_{n}" per tile, not
    /// merged into a rectangle like Jail/TrainingRoom) — that's what lets
    /// one Lava tile decay on its own timer without touching its neighbors.
    ///
    /// Placement (TryPlaceBridgeTile) requires the target tile to be Water
    /// or Lava with no room yet, and to have at least one cardinal neighbor
    /// that's either already-Claimed Floor or another already-bridged
    /// Water/Lava tile — "must start adjacent to owned tile," and the same
    /// single rule lets a line keep extending afterward, tile by tile, the
    /// same way DungeonGrid.BordersClaimedTile grows ordinary territory
    /// outward from its own frontier.
    ///
    /// Each tile's plank visual is one of the dungeon_pack's five modular
    /// bridge meshes (Prop_BridgeEdge / _BridgeMiddle / _BridgeCorner /
    /// _BridgeTJunction / _BridgeFourWay, see DungeonPackPropSetup /
    /// BRIDGE_README.txt), chosen from how many of the tile's four cardinal
    /// sides connect to another bridge tile (or, at a run's end, to Claimed
    /// land): 4 -> four-way plate, 3 -> T piece, 2 perpendicular bridge arms
    /// -> corner piece, 2 straight -> middle piece, a run end touching land
    /// -> edge piece (landing flange facing that land). A tile's
    /// choice/orientation depends on its neighbors, so placing or removing
    /// one tile re-runs it for that tile and its cardinal neighbors
    /// (RefreshTileAndNeighbours). Falls back to a flat colored slab if the
    /// props haven't been built yet.
    ///
    /// A Lava bridge tile decays back to unbridged Lava on its own timer
    /// (LavaBridgeDecaySeconds, see Update/DecayTile) — no gold refund,
    /// same "no rescue on loss" convention Jail's own sold-with-prisoners
    /// case follows. A Water bridge tile never decays. Selling (the generic
    /// Sell tool, via LairManager.RoomSold — see OnRoomSold) does refund,
    /// same as every other room type.
    public class BridgeManager : MonoBehaviour
    {
        /// Placeholder gold cost per tile, charged instantly on placement —
        /// unbalanced like every other room's current cost.
        public const int CostPerTile = 15;

        /// "Bridges Decay over lava after 5 min."
        private const float LavaBridgeDecaySeconds = 300f;

        // Fallback slab (used only until Tools/DungeonPack/Setup Props has
        // built the real meshes) — see BuildPlankFallback.
        [SerializeField] private Color _plankColor = new Color(0.45f, 0.32f, 0.18f);
        private const float PlankFloorHeight = 0.17f;
        private const float PlankFootprintScale = 0.9f;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private int _ownerId;
        private int _nextRoomId;

        private GameObject _edgePrefab;
        private GameObject _middlePrefab;
        private GameObject _cornerPrefab;
        private GameObject _tJunctionPrefab;
        private GameObject _fourWayPrefab;

        private readonly Dictionary<Vector2Int, string> _roomIdByCoord = new Dictionary<Vector2Int, string>();
        private readonly Dictionary<string, Vector2Int> _coordByRoomId = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();

        // Only ever populated for Lava tiles — see TryPlaceBridgeTile/Update.
        private readonly Dictionary<Vector2Int, float> _lavaDecayDeadline = new Dictionary<Vector2Int, float>();
        private readonly List<Vector2Int> _decayScratch = new List<Vector2Int>();

        private enum BridgePiece { Edge, Middle, Corner, TJunction, FourWay }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            // Same "load once, no scene wiring" convention JailManager and
            // every other dungeon_pack-backed room manager uses.
            _edgePrefab = Resources.Load<GameObject>("Dungeon/Prop_BridgeEdge");
            _middlePrefab = Resources.Load<GameObject>("Dungeon/Prop_BridgeMiddle");
            _cornerPrefab = Resources.Load<GameObject>("Dungeon/Prop_BridgeCorner");
            _tJunctionPrefab = Resources.Load<GameObject>("Dungeon/Prop_BridgeTJunction");
            _fourWayPrefab = Resources.Load<GameObject>("Dungeon/Prop_BridgeFourWay");
        }

        private void Update()
        {
            if (_lavaDecayDeadline.Count == 0)
            {
                return;
            }

            _decayScratch.Clear();
            foreach (var entry in _lavaDecayDeadline)
            {
                if (Time.time >= entry.Value)
                {
                    _decayScratch.Add(entry.Key);
                }
            }

            foreach (var coord in _decayScratch)
            {
                DecayTile(coord);
            }
        }

        /// Whether coord could become a bridge tile right now — Water or
        /// Lava, no room yet, and touching either Claimed floor or another
        /// already-bridged tile. A built bridge tile claims itself (see
        /// TryPlaceBridgeTile/DungeonGrid.TryAssignBridgeRoom), so
        /// DungeonGrid.BordersClaimedTile's own claimed-neighbor rule
        /// already covers both cases — one shared definition of "touching
        /// owned territory" instead of a second copy here.
        private bool CanPlaceBridgeTile(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return false;
            }

            var tile = _grid.GetTile(coord);
            if ((tile.Type != TileType.Water && tile.Type != TileType.Lava) || tile.HasRoom)
            {
                return false;
            }

            return _grid.BordersClaimedTile(coord);
        }

        /// Places (and instantly charges for) one bridge tile — called per
        /// tile as a line/square paint gesture passes over Water/Lava (see
        /// TileInteractionController.ApplyGestureAction's BuildBridge case).
        /// Silently no-ops (returns false) on an invalid target or
        /// insufficient gold, same soft-fail convention every other
        /// gesture action in this game follows.
        public bool TryPlaceBridgeTile(Vector2Int coord)
        {
            if (!CanPlaceBridgeTile(coord))
            {
                // Silent-to-the-player still (soft-fail convention), but
                // logged so a "why won't it place" is diagnosable. Only the
                // "right tile type, wrong adjacency" case is worth a line —
                // a paint gesture sweeps over plenty of floor / already-
                // bridged tiles it was never going to bridge, and logging
                // every one of those would just bury the useful message.
                if (_grid.InBounds(coord))
                {
                    var missed = _grid.GetTile(coord);
                    if ((missed.Type == TileType.Water || missed.Type == TileType.Lava) && !missed.HasRoom)
                    {
                        GameplayLog.Write(_ownerId, $"Bridge not placed at ({coord.x},{coord.y}) — must be cardinally adjacent to Claimed floor or an existing bridge tile");
                    }
                }

                return false;
            }

            if (_treasuryManager != null && !_treasuryManager.TrySpendGold(CostPerTile))
            {
                GameplayLog.Write(_ownerId, $"Bridge not placed at ({coord.x},{coord.y}) — need {CostPerTile}g stored in the Treasury");
                return false;
            }

            var roomId = $"Bridge_{_nextRoomId++}";
            if (!_grid.TryAssignBridgeRoom(coord, roomId, _ownerId))
            {
                // Shouldn't happen — CanPlaceBridgeTile just verified this
                // exact tile — but refund rather than silently eat the gold
                // if it somehow fails.
                _treasuryManager?.AddGold(CostPerTile);
                return false;
            }

            _roomIdByCoord[coord] = roomId;
            _coordByRoomId[roomId] = coord;

            // The new tile's edge/middle choice AND that of its existing
            // neighbors can change now that this tile exists (an endpoint
            // that was a dangling middle piece, an edge piece that should
            // now point the other way, ...), so rebuild the whole cluster.
            RefreshTileAndNeighbours(coord);

            if (_grid.GetTile(coord).Type == TileType.Lava)
            {
                _lavaDecayDeadline[coord] = Time.time + LavaBridgeDecaySeconds;
            }

            GameplayLog.Write(_ownerId, $"Bridge built at ({coord.x},{coord.y})");
            return true;
        }

        /// LairManager.RoomSold fires for every sold room — only react to
        /// our own (by roomId prefix, same convention every other room
        /// manager uses). The grid tile itself is already cleared by
        /// LairManager.TrySellRoom by the time this fires; this just tears
        /// down our own bookkeeping/visual, same as JailManager.OnRoomSold.
        private void OnRoomSold(string roomId)
        {
            if (!_coordByRoomId.TryGetValue(roomId, out var coord))
            {
                return;
            }

            CleanupTile(coord, roomId);
        }

        /// A Lava bridge tile's own timer expiring — unlike OnRoomSold, the
        /// grid tile hasn't been cleared by anything else yet, so this does
        /// that itself. No gold refund — decay is attrition, not a sale.
        private void DecayTile(Vector2Int coord)
        {
            if (!_roomIdByCoord.TryGetValue(coord, out var roomId))
            {
                _lavaDecayDeadline.Remove(coord);
                return;
            }

            _grid.RemoveRoomTiles(roomId);
            CleanupTile(coord, roomId);
            GameplayLog.Write(_ownerId, $"Bridge decayed over lava at ({coord.x},{coord.y})");
        }

        private void CleanupTile(Vector2Int coord, string roomId)
        {
            if (_tileVisuals.TryGetValue(coord, out var visual) && visual != null)
            {
                Destroy(visual);
            }
            _tileVisuals.Remove(coord);
            _roomIdByCoord.Remove(coord);
            _coordByRoomId.Remove(roomId);
            _lavaDecayDeadline.Remove(coord);

            // Losing this tile can flip a neighbor from middle back to edge
            // (or spin an edge piece around), same as gaining one does.
            foreach (var offset in GridDirections.Cardinal)
            {
                RefreshTileVisual(coord + offset);
            }
        }

        // ---- visuals ----

        private void RefreshTileAndNeighbours(Vector2Int coord)
        {
            RefreshTileVisual(coord);
            foreach (var offset in GridDirections.Cardinal)
            {
                RefreshTileVisual(coord + offset);
            }
        }

        /// Rebuilds coord's plank mesh from its current neighbor layout.
        /// No-ops for any coord that isn't one of our bridge tiles, so it's
        /// safe to fire blindly at all four neighbors of a placed/removed
        /// tile.
        private void RefreshTileVisual(Vector2Int coord)
        {
            if (!_roomIdByCoord.ContainsKey(coord))
            {
                return;
            }

            if (_tileVisuals.TryGetValue(coord, out var existing) && existing != null)
            {
                Destroy(existing);
            }

            _tileVisuals[coord] = BuildTileVisual(coord);
        }

        private GameObject BuildTileVisual(Vector2Int coord)
        {
            var (piece, yaw) = ClassifyBridgeTile(coord);
            var prefab = piece switch
            {
                BridgePiece.Edge => _edgePrefab,
                BridgePiece.Middle => _middlePrefab,
                BridgePiece.Corner => _cornerPrefab,
                BridgePiece.TJunction => _tJunctionPrefab,
                BridgePiece.FourWay => _fourWayPrefab,
                _ => null,
            };
            if (prefab == null)
            {
                return BuildPlankFallback(coord);
            }

            var position = _grid.GridToWorld(coord);
            // Both meshes pivot at deck level (local y=0), which
            // BRIDGE_README.txt calls "flush with normal floor level".
            position.y = _grid.FloorSurfaceY;

            var go = Instantiate(prefab, transform, false);
            go.name = $"Bridge{piece}_{coord.x}_{coord.y}";
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        /// Picks the mesh + yaw for coord from its cardinal neighbours:
        ///  - 4 arms       -> four-way plate (symmetric, yaw 0);
        ///  - 3 arms       -> T piece, its closed side rotated to the one
        ///                    direction with no arm;
        ///  - 2 bridge arms
        ///    at right angle -> corner piece, bent to join them;
        ///  - otherwise    -> edge piece (landing flange on local -Z) when
        ///                    exactly one end of the run axis is Claimed
        ///                    land, else a straight middle piece aligned to
        ///                    the run axis.
        /// An "arm" is a bridge tile in that direction, or — only where the
        /// bridge doesn't run straight through this tile — a Claimed land
        /// tile to walk off onto.
        private (BridgePiece piece, float yaw) ClassifyBridgeTile(Vector2Int coord)
        {
            var west = coord + Vector2Int.left;
            var east = coord + Vector2Int.right;
            var south = coord + Vector2Int.down;
            var north = coord + Vector2Int.up;

            bool landW = IsLandNeighbour(west), landE = IsLandNeighbour(east);
            bool landS = IsLandNeighbour(south), landN = IsLandNeighbour(north);
            bool brW = _roomIdByCoord.ContainsKey(west), brE = _roomIdByCoord.ContainsKey(east);
            bool brS = _roomIdByCoord.ContainsKey(south), brN = _roomIdByCoord.ContainsKey(north);

            // When a bridge tile sits at both ends of an axis the deck runs
            // straight through, so land touching the perpendicular sides is
            // just a bank it passes, not an arm — that's what keeps a
            // mid-span tile in a 1-wide channel from reading as a junction.
            bool throughX = brW && brE;
            bool throughZ = brS && brN;

            bool armW, armE, armS, armN;
            if (throughX)
            {
                armW = armE = true;
                armS = brS;
                armN = brN;
            }
            else if (throughZ)
            {
                armS = armN = true;
                armW = brW;
                armE = brE;
            }
            else
            {
                armW = brW || landW;
                armE = brE || landE;
                armS = brS || landS;
                armN = brN || landN;
            }

            int armCount = (armW ? 1 : 0) + (armE ? 1 : 0) + (armS ? 1 : 0) + (armN ? 1 : 0);

            if (armCount >= 4)
            {
                return (BridgePiece.FourWay, 0f);
            }

            if (armCount == 3)
            {
                // The T mesh is closed on local +X and open on the other
                // three sides (the raw .obj is authored closed-on-(-X), but
                // Unity's OBJ import negates X). Rotate local +X to face the
                // one direction with no arm: +X -> world is east/south/west/
                // north at yaw 0/90/180/270.
                float tYaw;
                if (!armE) tYaw = 0f;
                else if (!armS) tYaw = 90f;
                else if (!armW) tYaw = 180f;
                else tYaw = 270f;             // !armN
                return (BridgePiece.TJunction, tYaw);
            }

            // A right-angle bend between two bridge arms (one on each axis)
            // — straight 2-arm runs, and bends where one arm is land rather
            // than bridge, fall through to the edge/middle logic below so
            // they still get a flush deck / a landing flange.
            if (armCount == 2 && (armW || armE) && (armS || armN) && (brW || brE) && (brS || brN))
            {
                // In-engine the corner mesh is open on +X and +Z (its raw
                // .obj is open on -X / +Z; Unity's OBJ import negates X), so
                // at yaw 0 it connects east + north.
                float cYaw;
                if (armN && armE) cYaw = 0f;
                else if (armE && armS) cYaw = 90f;
                else if (armS && armW) cYaw = 180f;
                else cYaw = 270f;             // armW && armN
                return (BridgePiece.Corner, cYaw);
            }

            // "Run axis" = the one the bridge actually travels along here.
            // Prefer an axis that connects on both ends; then one with land
            // at either end (so a fresh 1-tile bridge still reads as edge);
            // then any connected axis at all.
            bool xConnected = (landW || brW) && (landE || brE);
            bool zConnected = (landS || brS) && (landN || brN);

            bool runX;
            if (xConnected != zConnected)
            {
                runX = xConnected;
            }
            else if (xConnected)
            {
                // Both axes connect but neither is a 3+ arm junction (those
                // are handled above) — a straight tile in a 1-wide channel
                // with banks on the perpendicular axis. Follow the axis the
                // bridge actually runs through.
                runX = throughX;
            }
            else
            {
                bool xLand = landW || landE, zLand = landS || landN;
                bool xAny = xLand || brW || brE, zAny = zLand || brS || brN;
                if (xLand != zLand)
                {
                    runX = xLand;
                }
                else if (xAny != zAny)
                {
                    runX = xAny;
                }
                else
                {
                    runX = true;
                }
            }

            bool negLand = runX ? landW : landS;
            bool posLand = runX ? landE : landN;

            if (negLand ^ posLand)
            {
                // Land at exactly one end — edge piece, land side toward it.
                float edgeYaw = runX
                    ? (negLand ? 90f : 270f)   // land west : land east
                    : (negLand ? 0f : 180f);   // land south : land north
                return (BridgePiece.Edge, edgeYaw);
            }

            if (negLand && posLand)
            {
                // A single tile bridging directly between two banks — no
                // double-flange mesh exists, so use an edge piece anchored
                // to the negative bank.
                return (BridgePiece.Edge, runX ? 90f : 0f);
            }

            // Neither end is land — middle piece aligned to the run axis
            // (symmetric, so 0/180 and 90/270 are interchangeable).
            return (BridgePiece.Middle, runX ? 90f : 0f);
        }

        private bool IsLandNeighbour(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return false;
            }

            var tile = _grid.GetTile(coord);
            return tile.Type == TileType.Floor && tile.Ownership == TileOwnership.Claimed;
        }

        /// A flat wood-colored slab resting on top of the Water/Lava surface
        /// mesh for coord — the pre-dungeon_pack look, kept only as a
        /// fallback for when Tools/DungeonPack/Setup Props hasn't run yet.
        /// Grounded flush with the liquid surface (DungeonGrid.FloorSurfaceY)
        /// rather than JailManager's "GridToWorld + down*0.5", which would
        /// leave the slab ~90% submerged.
        private GameObject BuildPlankFallback(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord);
            basePosition.y = _grid.FloorSurfaceY + PlankFloorHeight * 0.5f;

            var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = $"BridgePlank_{coord.x}_{coord.y}";
            plank.transform.SetParent(transform, false);
            plank.transform.position = basePosition;
            plank.transform.localScale = new Vector3(cellSize * PlankFootprintScale, PlankFloorHeight, cellSize * PlankFootprintScale);
            plank.GetComponent<Renderer>().material.color = _plankColor;
            Destroy(plank.GetComponent<Collider>());
            return plank;
        }
    }
}
