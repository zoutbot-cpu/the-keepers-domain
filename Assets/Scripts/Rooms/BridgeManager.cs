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

        [SerializeField] private Color _plankColor = new Color(0.45f, 0.32f, 0.18f);
        private const float PlankFloorHeight = 0.17f;
        private const float PlankFootprintScale = 0.9f;

        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private int _nextRoomId;

        private readonly Dictionary<Vector2Int, string> _roomIdByCoord = new Dictionary<Vector2Int, string>();
        private readonly Dictionary<string, Vector2Int> _coordByRoomId = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> _tileVisuals = new Dictionary<Vector2Int, GameObject>();

        // Only ever populated for Lava tiles — see TryPlaceBridgeTile/Update.
        private readonly Dictionary<Vector2Int, float> _lavaDecayDeadline = new Dictionary<Vector2Int, float>();
        private readonly List<Vector2Int> _decayScratch = new List<Vector2Int>();

        public void Initialize(DungeonGrid grid, LairManager lairManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _treasuryManager = treasuryManager;
            lairManager.RoomSold += OnRoomSold;
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
                return false;
            }

            if (_treasuryManager != null && !_treasuryManager.TrySpendGold(CostPerTile))
            {
                return false;
            }

            var roomId = $"Bridge_{_nextRoomId++}";
            if (!_grid.TryAssignBridgeRoom(coord, roomId))
            {
                // Shouldn't happen — CanPlaceBridgeTile just verified this
                // exact tile — but refund rather than silently eat the gold
                // if it somehow fails.
                _treasuryManager?.AddGold(CostPerTile);
                return false;
            }

            _roomIdByCoord[coord] = roomId;
            _coordByRoomId[roomId] = coord;
            _tileVisuals[coord] = BuildPlankVisual(coord);

            if (_grid.GetTile(coord).Type == TileType.Lava)
            {
                _lavaDecayDeadline[coord] = Time.time + LavaBridgeDecaySeconds;
            }

            GameplayLog.Write($"Bridge built at ({coord.x},{coord.y})");
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
            GameplayLog.Write($"Bridge decayed over lava at ({coord.x},{coord.y})");
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
        }

        /// A flat wood-colored slab sitting flush on top of DungeonGrid's
        /// own floor cube for coord (which stays the shared purple HasRoom
        /// color underneath, same as every other room) — same
        /// "GridToWorld + down*0.5" grounding convention JailManager's own
        /// ring-floor overlay uses.
        private GameObject BuildPlankVisual(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

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
