using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Rooms;

namespace KeepersDomain.Net
{
    /// The one session-lifetime networked object (prefab
    /// Resources/Net/NetGame, spawned by the host in GameBootstrap.BuildWorld
    /// once it's hosting). Milestone 1a: replicates the grid — a one-shot
    /// tile snapshot to each joining client, then per-tile deltas off
    /// DungeonGrid.TileChanged. Its client-side OnNetworkSpawn is the
    /// "networking is live" signal that kicks off the render-only client
    /// world.
    ///
    /// Later milestones hang creature/keeper-state replication and client
    /// command RPCs off this same object.
    public class NetGame : NetworkBehaviour
    {
        public static NetGame Instance { get; private set; }

        // Tiles per snapshot / delta RPC — kept well under the transport
        // fragmentation cap. A NetTile is ~40 bytes, so 128 ~= 5 KB.
        private const int TilesPerRpc = 128;

        /// Map dimensions — the client needs these to size its DungeonGrid
        /// before the tile snapshot lands. Initial netvar values are
        /// available in OnNetworkSpawn.
        public readonly NetworkVariable<int> MapWidth = new NetworkVariable<int>();
        public readonly NetworkVariable<int> MapHeight = new NetworkVariable<int>();

        private DungeonGrid _grid;

        // Host: coords whose TileChanged fired since the last flush.
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();
        private readonly List<Vector2Int> _flush = new List<Vector2Int>();

        // Client: gold-free room managers (from BuildClientWorld) + the
        // room footprints gathered from replicated tiles, replayed through
        // RoomReconstruction — once the whole snapshot has landed, then
        // again per new room a live delta introduces (a Lair/Treasury/...
        // built after this client joined).
        private Dictionary<RoomDesignTool, IRestorableRoomManager> _clientRoomManagers;
        private readonly Dictionary<string, List<Vector2Int>> _clientRoomFootprints = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, int> _clientRoomOwners = new Dictionary<string, int>();
        private readonly HashSet<string> _clientReconstructedRooms = new HashSet<string>();
        private readonly Dictionary<string, List<Vector2Int>> _clientRoomScratch = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, int> _clientRoomOwnerScratch = new Dictionary<string, int>();

        /// Host only — called from GameBootstrap.BuildWorld right after the
        /// NetGame is spawned, once the grid exists.
        public void HostBind(DungeonGrid grid)
        {
            _grid = grid;
            MapWidth.Value = grid.Width;
            MapHeight.Value = grid.Height;
            _grid.TileChanged += OnTileChanged;
        }

        /// Host only — called from GameBootstrap.OnHostReady once every
        /// KeeperContext (and its room managers) exists, so a Lair tile
        /// being claimed by a creature or a Treasury tile's gold changing
        /// relays to the client the same way tile deltas do. Neither is
        /// DungeonGrid tile state (a claim is LairManager's own per-tile
        /// bookkeeping; gold is TreasuryManager's), so they need their own
        /// small events/RPCs rather than riding NetTile.
        public void HostBindKeeperRooms()
        {
            if (KeeperContext.All == null)
            {
                return;
            }

            foreach (var ctx in KeeperContext.All)
            {
                if (ctx.Lair != null)
                {
                    ctx.Lair.ClaimChanged += OnHostLairClaimChanged;
                }

                if (ctx.Treasury != null)
                {
                    ctx.Treasury.GoldChanged += OnHostTreasuryGoldChanged;
                }
            }
        }

        private void OnHostLairClaimChanged(Vector2Int coord, bool claimed)
        {
            LairClaimChangedRpc(NetCoord.From(coord), claimed);
        }

        private void OnHostTreasuryGoldChanged(Vector2Int coord, int amount)
        {
            TreasuryGoldChangedRpc(NetCoord.From(coord), amount);
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (IsServer)
            {
                return;
            }

            // Client: build the render-only world (creates the DungeonGrid
            // and calls ClientBindRooms below), then pull the grid state.
            NetSession.Instance?.OnClientReady?.Invoke();
            if (_grid == null)
            {
                _grid = FindAnyObjectByType<DungeonGrid>();
            }

            RequestSnapshotRpc();
        }

        /// Client — GameBootstrap.BuildClientWorld hands over the grid and
        /// the gold-free room managers.
        public void ClientBindRooms(DungeonGrid grid, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            _grid = grid;
            _clientRoomManagers = roomManagers;
        }

        public override void OnNetworkDespawn()
        {
            if (_grid != null)
            {
                _grid.TileChanged -= OnTileChanged;
            }

            if (KeeperContext.All != null)
            {
                foreach (var ctx in KeeperContext.All)
                {
                    if (ctx.Lair != null)
                    {
                        ctx.Lair.ClaimChanged -= OnHostLairClaimChanged;
                    }

                    if (ctx.Treasury != null)
                    {
                        ctx.Treasury.GoldChanged -= OnHostTreasuryGoldChanged;
                    }
                }
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---- snapshot ----

        [Rpc(SendTo.Server)]
        private void RequestSnapshotRpc(RpcParams p = default)
        {
            if (_grid == null)
            {
                return;
            }

            var sender = p.Receive.SenderClientId;
            var target = RpcTarget.Single(sender, RpcTargetUse.Temp);

            var batch = new List<NetTile>(TilesPerRpc);
            for (int x = 0; x < _grid.Width; x++)
            {
                for (int y = 0; y < _grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var tile = _grid.GetTile(coord);
                    if (IsDefault(tile))
                    {
                        continue;
                    }

                    batch.Add(NetTile.From(coord, tile));
                    if (batch.Count == TilesPerRpc)
                    {
                        SnapshotTilesRpc(batch.ToArray(), target);
                        batch.Clear();
                    }
                }
            }

            if (batch.Count > 0)
            {
                SnapshotTilesRpc(batch.ToArray(), target);
            }

            SnapshotDoneRpc(target);
            SendRoomVisualStateSnapshot(target);
        }

        /// Host — catches a newly-joined client up on room-manager visual
        /// state that isn't part of DungeonGrid's own tile data (lair
        /// claims, treasury gold), across every keeper. Sent right after
        /// SnapshotDoneRpc so RoomReconstruction has already registered
        /// every tile these calls target (RPCs from one object arrive in
        /// send order, same ordering SnapshotTilesRpc/SnapshotDoneRpc
        /// already rely on).
        private void SendRoomVisualStateSnapshot(BaseRpcTarget target)
        {
            if (KeeperContext.All == null)
            {
                return;
            }

            var claimedCoords = new List<NetCoord>();
            var goldCoords = new List<NetCoord>();
            var goldAmounts = new List<int>();

            foreach (var ctx in KeeperContext.All)
            {
                if (ctx.Lair != null)
                {
                    foreach (var coord in ctx.Lair.ClaimedTiles)
                    {
                        claimedCoords.Add(NetCoord.From(coord));
                    }
                }

                if (ctx.Treasury != null)
                {
                    foreach (var entry in ctx.Treasury.StoredGoldByTile)
                    {
                        if (entry.Value > 0)
                        {
                            goldCoords.Add(NetCoord.From(entry.Key));
                            goldAmounts.Add(entry.Value);
                        }
                    }
                }
            }

            if (claimedCoords.Count > 0)
            {
                LairClaimsSnapshotRpc(claimedCoords.ToArray(), target);
            }

            if (goldCoords.Count > 0)
            {
                TreasuryGoldSnapshotRpc(goldCoords.ToArray(), goldAmounts.ToArray(), target);
            }
        }

        /// Untouched Rock is never sent — DungeonGrid.Initialize already
        /// defaults every tile to it. Mirrors LevelDesignerSession.
        /// IsDefaultRock.
        private static bool IsDefault(TileState t)
        {
            return t.Type == TileType.Rock && !t.IsBedrock && !t.IsReinforced
                && t.WallResourceType == WallResourceType.None && !t.IsQueuedForDig
                && !t.IsQueuedForReinforce;
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void SnapshotTilesRpc(NetTile[] tiles, RpcParams p)
        {
            Apply(tiles, live: false);
        }

        /// Client — the tile snapshot is fully applied; rebuild real room
        /// decoration (carpet/bookcases/pit/...) so rooms aren't flat pink.
        /// Room tiles arrive from the snapshot already Claimed Floor but
        /// WITHOUT a RoomId (see NetTile.ToTileState) so RestoreRoom's
        /// TryAssignRoom can tag + decorate them here.
        [Rpc(SendTo.SpecifiedInParams)]
        private void SnapshotDoneRpc(RpcParams p)
        {
            if (_grid == null || _clientRoomManagers == null || _clientRoomFootprints.Count == 0)
            {
                return;
            }

            RoomReconstruction.RestoreRooms(_grid, _clientRoomFootprints, _clientRoomOwners, _clientRoomManagers);
            foreach (var roomId in _clientRoomFootprints.Keys)
            {
                _clientReconstructedRooms.Add(roomId);
            }

            _clientRoomFootprints.Clear();
            _clientRoomOwners.Clear();
        }

        // ---- live deltas ----

        private void OnTileChanged(Vector2Int coord)
        {
            _dirty.Add(coord);
        }

        private void LateUpdate()
        {
            if (!IsServer || _grid == null || _dirty.Count == 0)
            {
                return;
            }

            _flush.Clear();
            _flush.AddRange(_dirty);
            _dirty.Clear();

            for (int start = 0; start < _flush.Count; start += TilesPerRpc)
            {
                var n = Mathf.Min(TilesPerRpc, _flush.Count - start);
                var tiles = new NetTile[n];
                for (int i = 0; i < n; i++)
                {
                    var coord = _flush[start + i];
                    tiles[i] = NetTile.From(coord, _grid.GetTile(coord));
                }

                SyncTilesRpc(tiles);
            }
        }

        [Rpc(SendTo.NotServer)]
        private void SyncTilesRpc(NetTile[] tiles)
        {
            Apply(tiles, live: true);
        }

        private void Apply(NetTile[] tiles, bool live)
        {
            if (_grid == null)
            {
                _grid = FindAnyObjectByType<DungeonGrid>();
                if (_grid == null)
                {
                    return;
                }
            }

            foreach (var t in tiles)
            {
                _grid.ApplyReplicatedTile(t.Coord, t.ToTileState());

                if (!t.RoomId.IsEmpty)
                {
                    var roomId = t.RoomId.ToString();
                    if (!_clientRoomFootprints.TryGetValue(roomId, out var list))
                    {
                        list = new List<Vector2Int>();
                        _clientRoomFootprints[roomId] = list;
                        _clientRoomOwners[roomId] = t.OwnerId;
                    }

                    list.Add(t.Coord);
                }
            }

            if (live)
            {
                ReconstructNewRooms();
            }
        }

        /// A room built after this client joined arrives as ordinary tile
        /// deltas (Claimed Floor + a RoomId). Its whole footprint lands in
        /// one host frame (LairManager.TryPlaceLair etc. claim atomically),
        /// so once any of its tiles have been seen we can reconstruct it —
        /// guarded by _clientReconstructedRooms so a later delta touching
        /// the same room (a tile re-tag) doesn't rebuild its decoration.
        private void ReconstructNewRooms()
        {
            if (_clientRoomManagers == null)
            {
                return;
            }

            _clientRoomScratch.Clear();
            _clientRoomOwnerScratch.Clear();
            foreach (var entry in _clientRoomFootprints)
            {
                if (_clientReconstructedRooms.Contains(entry.Key))
                {
                    continue;
                }

                _clientRoomScratch[entry.Key] = entry.Value;
                _clientRoomOwnerScratch[entry.Key] =
                    _clientRoomOwners.TryGetValue(entry.Key, out var o) ? o : 0;
            }

            if (_clientRoomScratch.Count == 0)
            {
                return;
            }

            RoomReconstruction.RestoreRooms(_grid, _clientRoomScratch, _clientRoomOwnerScratch, _clientRoomManagers);

            foreach (var roomId in _clientRoomScratch.Keys)
            {
                _clientReconstructedRooms.Add(roomId);
                _clientRoomFootprints.Remove(roomId);
                _clientRoomOwners.Remove(roomId);
            }
        }

        // ---- room-manager visual state (lair claims, treasury gold) ----

        [Rpc(SendTo.SpecifiedInParams)]
        private void LairClaimsSnapshotRpc(NetCoord[] claimedCoords, RpcParams p)
        {
            foreach (var c in claimedCoords)
            {
                ApplyLairClaim(c.ToVector2Int(), claimed: true);
            }
        }

        [Rpc(SendTo.NotServer)]
        private void LairClaimChangedRpc(NetCoord coord, bool claimed)
        {
            ApplyLairClaim(coord.ToVector2Int(), claimed);
        }

        /// Client — calls straight into the (gold-free) LairManager's own
        /// claim/release, exactly as if a creature had claimed it locally.
        /// That method is pure visual + local bookkeeping (no gold cost, no
        /// gameplay side effect), so replaying it here is safe and needs no
        /// separate "apply replicated" path the way TreasuryManager's gold
        /// does. A no-op if the tile's room hasn't reconstructed yet (the
        /// snapshot ordering above prevents that) or claim/release itself
        /// rejects it (e.g. it's already in that state).
        private void ApplyLairClaim(Vector2Int coord, bool claimed)
        {
            if (_clientRoomManagers != null
                && _clientRoomManagers.TryGetValue(RoomDesignTool.Lair, out var manager)
                && manager is LairManager lair)
            {
                if (claimed)
                {
                    lair.TryClaimLairTile(coord);
                }
                else
                {
                    lair.ReleaseLairTile(coord);
                }
            }
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void TreasuryGoldSnapshotRpc(NetCoord[] coords, int[] amounts, RpcParams p)
        {
            for (int i = 0; i < coords.Length; i++)
            {
                ApplyTreasuryGold(coords[i].ToVector2Int(), amounts[i]);
            }
        }

        [Rpc(SendTo.NotServer)]
        private void TreasuryGoldChangedRpc(NetCoord coord, int amount)
        {
            ApplyTreasuryGold(coord.ToVector2Int(), amount);
        }

        private void ApplyTreasuryGold(Vector2Int coord, int amount)
        {
            if (_clientRoomManagers != null
                && _clientRoomManagers.TryGetValue(RoomDesignTool.Treasury, out var manager)
                && manager is TreasuryManager treasury)
            {
                treasury.ApplyReplicatedGold(coord, amount);
            }
        }

        // ---- client commands (Milestone 1c) ----

        // Exactly one client can ever connect in M1 (NetSession.
        // ApproveConnection caps the session at 2 total), and it's always
        // assigned keeper 1 -- the same fixed assignment ClientHud.
        // LocalOwnerId and KeeperNetState's HUD lookups already rely on.
        // Real per-connection clientId -> ownerId mapping is M2, once more
        // than one client can join.
        private const int ClientOwnerId = 1;

        /// Client — ClientInputController's Mine command. Routes to the
        /// exact same DungeonGrid.RequestDig every local player's Mine tool
        /// calls (TileInteractionController), so it's no-op-safe on
        /// anything that isn't a valid dig target and picks up
        /// BuilderJobBoard's own job-assignment/pathing unchanged. The
        /// result replicates back to every client through the normal tile
        /// delta path, not a direct reply.
        [Rpc(SendTo.Server)]
        public void RequestDigRpc(NetCoord coord)
        {
            if (_grid != null)
            {
                _grid.RequestDig(coord.ToVector2Int(), ClientOwnerId);
            }
        }

        /// Client — ClientInputController's Summon Impling command. Routes
        /// to keeper 1's own ImplingSpawner, the same mana-gated method the
        /// offline Impling menu's Spawn button calls locally. The new
        /// impling replicates back as a creature ghost (CreatureNetView),
        /// same as any host-spawned creature.
        [Rpc(SendTo.Server)]
        public void RequestSummonImplingRpc(NetCoord coord)
        {
            var ctx = KeeperContext.ForOwner(ClientOwnerId);
            if (ctx != null && ctx.ImplingSpawner != null)
            {
                ctx.ImplingSpawner.SpawnImplingAt(coord.ToVector2Int());
            }
        }

        // ---- client commands (Milestone 2, first slice) ----

        /// Client — ClientInputController's Reinforce command. Routes to
        /// the exact same DungeonGrid.RequestReinforce every local player's
        /// Reinforce tool calls.
        [Rpc(SendTo.Server)]
        public void RequestReinforceRpc(NetCoord coord)
        {
            if (_grid != null)
            {
                _grid.RequestReinforce(coord.ToVector2Int(), ClientOwnerId);
            }
        }

        /// Client — ClientInputController's Cancel command. Tries a queued
        /// dig first, then a queued reinforce, same as
        /// TileInteractionController's own Unqueue gesture does per
        /// BuildMode — except the client has one Cancel toggle covering
        /// both instead of a separate mode per job kind, since it only
        /// ever needs to cancel jobs it can see are queued right there on
        /// the tile. Keeper 1's own BuilderJobBoard gates which jobs it'll
        /// actually let go (a job already claimed by a creature mid-walk
        /// isn't cancelable), same as offline.
        [Rpc(SendTo.Server)]
        public void RequestCancelJobRpc(NetCoord coord)
        {
            var ctx = KeeperContext.ForOwner(ClientOwnerId);
            if (ctx == null || ctx.JobBoard == null || _grid == null)
            {
                return;
            }

            var c = coord.ToVector2Int();
            if (ctx.JobBoard.CancelJob(c))
            {
                _grid.CancelDig(c);
                return;
            }

            if (ctx.JobBoard.CancelReinforceJob(c))
            {
                _grid.CancelReinforce(c);
            }
        }

        /// Client — ClientInputController's Sell command. Routes to keeper
        /// 1's own LairManager.TrySellRoom, the same generic Sell tool
        /// every room type shares offline — it already rejects a tile that
        /// isn't keeper 1's own (see TrySellRoom's own owner check), so a
        /// stray/mistaken call can't tear down the host's rooms.
        [Rpc(SendTo.Server)]
        public void RequestSellRoomRpc(NetCoord coord)
        {
            var ctx = KeeperContext.ForOwner(ClientOwnerId);
            if (ctx != null && ctx.Lair != null)
            {
                ctx.Lair.TrySellRoom(coord.ToVector2Int());
            }
        }

        /// Client — ClientInputController's Bridge command. Routes to
        /// keeper 1's own BridgeManager.TryPlaceBridgeTile, the same
        /// instant gold-charged line-paint action the offline Bridge tool
        /// uses (one call per tile the gesture passes over).
        [Rpc(SendTo.Server)]
        public void RequestBridgeTileRpc(NetCoord coord)
        {
            var ctx = KeeperContext.ForOwner(ClientOwnerId);
            if (ctx != null && ctx.Bridge != null)
            {
                ctx.Bridge.TryPlaceBridgeTile(coord.ToVector2Int());
            }
        }

        /// Client — ClientInputController's Recruit buttons. Routes to
        /// keeper 1's own spawner for that species, the exact mana/pool-
        /// gated method the offline Creatures menu's Recruit button calls
        /// (TryRecruitX already no-ops if the pool's empty or the join
        /// requirements aren't met — nothing to validate here beyond
        /// picking the right spawner). Elf has no recruit path (see
        /// ElfSpawner's own header) so isn't included.
        [Rpc(SendTo.Server)]
        public void RequestRecruitRpc(EditorCreatureKind kind)
        {
            var ctx = KeeperContext.ForOwner(ClientOwnerId);
            if (ctx == null)
            {
                return;
            }

            switch (kind)
            {
                case EditorCreatureKind.Gremlin:
                    if (ctx.GremlinSpawner != null) ctx.GremlinSpawner.TryRecruitGremlin();
                    break;
                case EditorCreatureKind.Warlock:
                    if (ctx.WarlockSpawner != null) ctx.WarlockSpawner.TryRecruitWarlock();
                    break;
                case EditorCreatureKind.MazeRattler:
                    if (ctx.MazeRattlerSpawner != null) ctx.MazeRattlerSpawner.TryRecruitMazeRattler();
                    break;
                case EditorCreatureKind.BeanCounter:
                    if (ctx.BeanCounterSpawner != null) ctx.BeanCounterSpawner.TryRecruitBeanCounter();
                    break;
            }
        }
    }
}
