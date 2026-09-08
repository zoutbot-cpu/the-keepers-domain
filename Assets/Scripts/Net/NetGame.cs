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

        // Tiles per snapshot / delta RPC. A NetTile is ~40-66 bytes, so 48
        // stays under UnityTransport's 6 KB default payload cap — one
        // unfragmented reliable message per batch.
        private const int TilesPerRpc = 48;

        // How many snapshot batches the host streams to a joining client per
        // tick. The whole grid used to go out in a single frame; on a real
        // (Relay) connection that dumps far more than the reliable send
        // window can hold at once, the overflow is dropped, and the client
        // comes up with an almost-empty world. A handful per tick keeps the
        // window from overflowing while still finishing a 96x96 map in well
        // under a second.
        private const int SnapshotBatchesPerTick = 4;

        /// Map dimensions — the client needs these to size its DungeonGrid
        /// before the tile snapshot lands. The host writes them in its own
        /// OnNetworkSpawn (BEFORE the spawn message is serialized to
        /// observers), never after Spawn() — with the lobby, the client is
        /// already connected when the host spawns this object, so a value
        /// set a line after Spawn() misses the spawn snapshot and the
        /// client builds a 1x1 grid.
        public readonly NetworkVariable<int> MapWidth = new NetworkVariable<int>();
        public readonly NetworkVariable<int> MapHeight = new NetworkVariable<int>();

        private DungeonGrid _grid;

        // Host: coords whose TileChanged fired since the last flush.
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();
        private readonly List<Vector2Int> _flush = new List<Vector2Int>();

        // Host: joining clients still being caught up with the initial tile
        // snapshot, streamed a few batches per tick (see PumpSnapshots).
        private readonly Queue<PendingSnapshot> _pendingSnapshots = new Queue<PendingSnapshot>();

        private sealed class PendingSnapshot
        {
            public ulong ClientId;
            public List<Vector2Int> Coords;
            public int Cursor;
            // Persistent (not Temp) — it's reused across several ticks and
            // NGO forbids holding a Temp target past the call that made it.
            public BaseRpcTarget Target;
        }

        // Client: gold-free room managers (from BuildClientWorld) + the
        // running footprint of every room, replayed through
        // RoomReconstruction to build/rebuild decoration. Persistent: a
        // room's footprint grows when the host adds rows to it, and this is
        // the accumulated truth the client rebuilds from -- the host's own
        // roomId strings can't be matched client-side (RestoreRoom mints
        // its own), so tile membership is the only durable handle.
        private Dictionary<RoomDesignTool, IRestorableRoomManager> _clientRoomManagers;
        private readonly Dictionary<string, List<Vector2Int>> _clientRoomFootprints = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, int> _clientRoomOwners = new Dictionary<string, int>();
        // Host roomId -> tile count when its decoration was last (re)built.
        // Absent: never built. Present but < the current footprint: it grew
        // (extra rows) and needs a rebuild.
        private readonly Dictionary<string, int> _clientRoomBuiltCount = new Dictionary<string, int>();
        // Host roomId -> the local roomId RoomReconstruction minted for it
        // on the client, read back off the grid after a build. Lets a later
        // "this room went away" delta (which only carries the local id) be
        // matched back to the right footprint entry.
        private readonly Dictionary<string, string> _clientRoomLocalId = new Dictionary<string, string>();
        private readonly Dictionary<string, List<Vector2Int>> _clientRoomScratch = new Dictionary<string, List<Vector2Int>>();
        private readonly Dictionary<string, int> _clientRoomOwnerScratch = new Dictionary<string, int>();

        /// Host only — called from GameBootstrap.BuildHostGame BEFORE the
        /// NetGame is spawned, once the grid exists. The map-dimension
        /// netvars are written in OnNetworkSpawn (below) so they ride the
        /// spawn message; setting them here, pre-spawn, would be a no-op /
        /// throw.
        public void HostBind(DungeonGrid grid)
        {
            _grid = grid;
            _grid.TileChanged += OnTileChanged;

            // Already spawned (defensive — normal flow binds pre-spawn):
            // push the dimensions now.
            if (IsSpawned)
            {
                MapWidth.Value = grid.Width;
                MapHeight.Value = grid.Height;
            }
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

            // A sold/removed room is deliberately NOT relayed as its own
            // event here (unlike claims/gold above) -- see Apply's
            // ApplyRoomRemoved, which detects it from the ordinary tile
            // delta instead. LairManager.RoomSold's own roomId string is
            // host-local (each side mints its own independent counter
            // during RestoreRoom), so relaying it directly would tell the
            // client to look up a room it never had under that name.
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
                // Write the dimensions here, while OnNetworkSpawn still runs
                // BEFORE NGO serializes the spawn message to observers — so
                // an already-connected client (the lobby case) reads the
                // real size, not 0.
                if (_grid != null)
                {
                    MapWidth.Value = _grid.Width;
                    MapHeight.Value = _grid.Height;
                }

                return;
            }

            // Client: build the render-only world once the host's map size
            // has arrived. It's normally already in the spawn payload; if
            // the spawn somehow beat the value, wait for the netvar.
            if (MapWidth.Value > 0 && MapHeight.Value > 0)
            {
                BuildClientWorldAndRequestSnapshot();
            }
            else
            {
                MapWidth.OnValueChanged += OnClientMapSizeReplicated;
                MapHeight.OnValueChanged += OnClientMapSizeReplicated;
            }
        }

        private void OnClientMapSizeReplicated(int _, int __)
        {
            if (MapWidth.Value <= 0 || MapHeight.Value <= 0)
            {
                return;
            }

            MapWidth.OnValueChanged -= OnClientMapSizeReplicated;
            MapHeight.OnValueChanged -= OnClientMapSizeReplicated;
            BuildClientWorldAndRequestSnapshot();
        }

        private void BuildClientWorldAndRequestSnapshot()
        {
            // Builds the render-only world (creates the DungeonGrid and
            // calls ClientBindRooms), then pulls the grid state.
            NetSession.Instance?.OnClientReady?.Invoke();
            if (_grid == null)
            {
                _grid = FindAnyObjectByType<DungeonGrid>();
            }

            Debug.Log($"NetGame client: world built {MapWidth.Value}x{MapHeight.Value}, requesting tile snapshot.");
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
            MapWidth.OnValueChanged -= OnClientMapSizeReplicated;
            MapHeight.OnValueChanged -= OnClientMapSizeReplicated;

            while (_pendingSnapshots.Count > 0)
            {
                _pendingSnapshots.Dequeue().Target?.Dispose();
            }

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

            // Collect every non-default coord up front, then let
            // PumpSnapshots stream them out over the next several ticks
            // rather than blasting the whole grid into the reliable send
            // queue in one frame.
            var coords = new List<Vector2Int>();
            for (int x = 0; x < _grid.Width; x++)
            {
                for (int y = 0; y < _grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    if (!IsDefault(_grid.GetTile(coord)))
                    {
                        coords.Add(coord);
                    }
                }
            }

            var clientId = p.Receive.SenderClientId;
            _pendingSnapshots.Enqueue(new PendingSnapshot
            {
                ClientId = clientId,
                Coords = coords,
                Cursor = 0,
                Target = RpcTarget.Single(clientId, RpcTargetUse.Persistent),
            });
            Debug.Log($"NetGame host: client {clientId} requested snapshot — {coords.Count} non-default tiles of {_grid.Width}x{_grid.Height}.");
        }

        /// Host — streams the head pending snapshot a few batches at a time,
        /// finishing with SnapshotDoneRpc + the room-visual snapshot. Called
        /// once per tick from LateUpdate. RPCs from one NetworkObject arrive
        /// in send order, so the client still applies every tile batch
        /// before SnapshotDoneRpc triggers its room reconstruction.
        private void PumpSnapshots()
        {
            if (_pendingSnapshots.Count == 0)
            {
                return;
            }

            var job = _pendingSnapshots.Peek();

            // Client left mid-stream — drop the rest of its snapshot.
            if (!NetworkManager.ConnectedClients.ContainsKey(job.ClientId))
            {
                job.Target?.Dispose();
                _pendingSnapshots.Dequeue();
                return;
            }

            for (int b = 0; b < SnapshotBatchesPerTick && job.Cursor < job.Coords.Count; b++)
            {
                var n = Mathf.Min(TilesPerRpc, job.Coords.Count - job.Cursor);
                var tiles = new NetTile[n];
                for (int i = 0; i < n; i++)
                {
                    var coord = job.Coords[job.Cursor + i];
                    tiles[i] = NetTile.From(coord, _grid.GetTile(coord));
                }

                SnapshotTilesRpc(tiles, job.Target);
                job.Cursor += n;
            }

            if (job.Cursor >= job.Coords.Count)
            {
                SnapshotDoneRpc(job.Target);
                SendRoomVisualStateSnapshot(job.Target);
                job.Target?.Dispose();
                _pendingSnapshots.Dequeue();
                Debug.Log($"NetGame host: finished streaming {job.Coords.Count} tiles to client {job.ClientId}.");
            }
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

        // Client — running count of snapshot tiles received, for the log.
        private int _clientSnapshotTiles;

        [Rpc(SendTo.SpecifiedInParams)]
        private void SnapshotTilesRpc(NetTile[] tiles, RpcParams p)
        {
            _clientSnapshotTiles += tiles.Length;
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
            Debug.Log($"NetGame client: snapshot done — {_clientSnapshotTiles} tiles applied, grid {(_grid != null ? $"{_grid.Width}x{_grid.Height}" : "null")}, {_clientRoomFootprints.Count} room footprints.");

            if (_grid == null || _clientRoomManagers == null || _clientRoomFootprints.Count == 0)
            {
                return;
            }

            ReconstructRooms();
        }

        // ---- live deltas ----

        private void OnTileChanged(Vector2Int coord)
        {
            _dirty.Add(coord);
        }

        private void LateUpdate()
        {
            if (!IsSpawned || !IsServer || _grid == null)
            {
                return;
            }

            PumpSnapshots();

            if (_dirty.Count == 0)
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

        /// Host -> client: a keeper's Throne Room has fallen, the match is
        /// over. Called by GameBootstrap.HandleThroneDefeated on the host;
        /// the client turns it into its own EndScreen. The client "is"
        /// ClientOwnerId, so it lost iff that's the defeated keeper.
        [Rpc(SendTo.NotServer)]
        public void NotifyMatchOverRpc(int defeatedOwnerId)
        {
            var lost = defeatedOwnerId == ClientOwnerId;
            KeepersDomain.UI.EndScreen.Show(victory: !lost,
                lost ? "Your Throne Room has fallen." : "Every rival Throne Room has fallen.");
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
                // A room being removed (sold, or any other teardown) is NOT
                // carried as its own message -- it shows up here as an
                // ordinary tile update whose RoomId has gone from set to
                // empty. Read the client's own CURRENT tag before
                // overwriting it: the host's roomId string is meaningless
                // client-side (RestoreRoom mints a fresh id locally instead
                // of reusing the saved/replicated one -- see
                // ApplyRoomRemoved's own header), so this is the only
                // reliable way to know "a room just went away here."
                var previousRoomId = _grid.GetTile(t.Coord).RoomId;

                _grid.ApplyReplicatedTile(t.Coord, t.ToTileState());

                if (t.RoomId.IsEmpty)
                {
                    if (!string.IsNullOrEmpty(previousRoomId))
                    {
                        ApplyRoomRemoved(previousRoomId);
                    }

                    continue;
                }

                var roomId = t.RoomId.ToString();
                if (!_clientRoomFootprints.TryGetValue(roomId, out var list))
                {
                    list = new List<Vector2Int>();
                    _clientRoomFootprints[roomId] = list;
                    _clientRoomOwners[roomId] = t.OwnerId;
                }

                if (!list.Contains(t.Coord))
                {
                    list.Add(t.Coord);
                }
            }

            if (live)
            {
                ReconstructRooms();
            }
        }

        /// A room's tile lost its RoomId in a delta (see Apply's comment) --
        /// the host removed the room (sold, or torn down some other way).
        /// Tear down its decoration via the client's OWN LairManager using
        /// the CLIENT's locally-minted roomId (previousRoomId, read off the
        /// grid a moment ago) -- each side mints its own roomId counter, so
        /// the host's string was never meaningful here. Every other room
        /// manager (Treasury, SlimeHatchery, ...) listens for LairManager.
        /// RoomSold itself, so this tears down whichever room type it was.
        /// Then drop the room from the client's running footprint so a
        /// later delta doesn't try to rebuild a room that's gone.
        private void ApplyRoomRemoved(string previousClientRoomId)
        {
            RemoveClientRoomDecoration(previousClientRoomId);

            // previousClientRoomId is the client's local id -- match it back
            // to the host-id footprint entry and drop it, so a later delta
            // doesn't rebuild a room that's gone.
            string goneHostId = null;
            foreach (var entry in _clientRoomLocalId)
            {
                if (entry.Value == previousClientRoomId)
                {
                    goneHostId = entry.Key;
                    break;
                }
            }

            if (goneHostId != null)
            {
                _clientRoomFootprints.Remove(goneHostId);
                _clientRoomOwners.Remove(goneHostId);
                _clientRoomBuiltCount.Remove(goneHostId);
                _clientRoomLocalId.Remove(goneHostId);
            }
        }

        private void RemoveClientRoomDecoration(string clientRoomId)
        {
            if (!string.IsNullOrEmpty(clientRoomId)
                && _clientRoomManagers != null
                && _clientRoomManagers.TryGetValue(RoomDesignTool.Lair, out var manager)
                && manager is LairManager lair)
            {
                lair.ApplyReplicatedRoomSold(clientRoomId);
            }
        }

        /// (Re)builds client-side room decoration from the running
        /// footprints. A room that's never been built gets built; a room
        /// whose footprint has GROWN since it was last built (the host
        /// added rows to it) is torn down and rebuilt at its new full
        /// footprint -- the client can't just extend it, because it holds
        /// its own local roomId, not the host's. Rectangular expansions
        /// (extra rows/columns) are the common case and reconstruct
        /// cleanly through RoomReconstruction's bounding-box call.
        private void ReconstructRooms()
        {
            if (_clientRoomManagers == null || _grid == null)
            {
                return;
            }

            _clientRoomScratch.Clear();
            _clientRoomOwnerScratch.Clear();

            foreach (var entry in _clientRoomFootprints)
            {
                var hostId = entry.Key;
                var footprint = entry.Value;
                var builtCount = _clientRoomBuiltCount.TryGetValue(hostId, out var bc) ? bc : 0;

                if (builtCount >= footprint.Count)
                {
                    continue;
                }

                // Grew since last build -> tear the client's version down
                // first, then let it rebuild fresh below.
                if (builtCount > 0 && _clientRoomLocalId.TryGetValue(hostId, out var localId))
                {
                    RemoveClientRoomDecoration(localId);
                }

                _clientRoomScratch[hostId] = footprint;
                _clientRoomOwnerScratch[hostId] =
                    _clientRoomOwners.TryGetValue(hostId, out var o) ? o : 0;
            }

            if (_clientRoomScratch.Count == 0)
            {
                return;
            }

            RoomReconstruction.RestoreRooms(_grid, _clientRoomScratch, _clientRoomOwnerScratch, _clientRoomManagers);

            foreach (var entry in _clientRoomScratch)
            {
                _clientRoomBuiltCount[entry.Key] = entry.Value.Count;
                // Read back the local roomId RoomReconstruction just minted
                // (all the footprint's tiles now carry it).
                foreach (var c in entry.Value)
                {
                    var localId = _grid.GetTile(c).RoomId;
                    if (!string.IsNullOrEmpty(localId))
                    {
                        _clientRoomLocalId[entry.Key] = localId;
                        break;
                    }
                }
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
            if (ctx == null || ctx.ImplingSpawner == null)
            {
                Debug.LogWarning($"NetGame: client summon-impling had no keeper {ClientOwnerId} context/spawner -- is level1 actually a 2-player map?");
                return;
            }

            ctx.ImplingSpawner.SpawnImplingAt(coord.ToVector2Int());
        }

        // ---- client commands (Milestone 2) ----
        //
        // The client runs the host's real gameplay UI
        // (TileInteractionController + BottomMenuBar) now, routing every
        // mutating action through
        // NetworkedKeeperActions -> these RPCs. Each one calls the exact
        // gameplay method the offline/host UI calls, on keeper 1's own
        // KeeperContext, so every gate (territory, gold, mana, pool, job-
        // cancelability) applies unchanged and the result replicates back
        // through the normal tile-delta / creature-ghost / room-visual
        // paths -- there is never a direct reply.

        private KeeperContext ClientCtx => KeeperContext.ForOwner(ClientOwnerId);

        [Rpc(SendTo.Server)]
        public void RequestReinforceRpc(NetCoord coord)
        {
            if (_grid != null) _grid.RequestReinforce(coord.ToVector2Int(), ClientOwnerId);
        }

        [Rpc(SendTo.Server)]
        public void RequestBuildRpc(NetCoord coord)
        {
            if (_grid != null) _grid.RequestBuild(coord.ToVector2Int(), ClientOwnerId);
        }

        [Rpc(SendTo.Server)]
        public void RequestCancelDigRpc(NetCoord coord)
        {
            var ctx = ClientCtx;
            var c = coord.ToVector2Int();
            if (ctx != null && ctx.JobBoard != null && _grid != null && ctx.JobBoard.CancelJob(c))
            {
                _grid.CancelDig(c);
            }
        }

        [Rpc(SendTo.Server)]
        public void RequestCancelReinforceRpc(NetCoord coord)
        {
            var ctx = ClientCtx;
            var c = coord.ToVector2Int();
            if (ctx != null && ctx.JobBoard != null && _grid != null && ctx.JobBoard.CancelReinforceJob(c))
            {
                _grid.CancelReinforce(c);
            }
        }

        [Rpc(SendTo.Server)]
        public void RequestCancelBuildRpc(NetCoord coord)
        {
            var ctx = ClientCtx;
            var c = coord.ToVector2Int();
            if (ctx != null && ctx.JobBoard != null && _grid != null && ctx.JobBoard.CancelBuildJob(c))
            {
                _grid.CancelBuild(c);
            }
        }

        [Rpc(SendTo.Server)]
        public void RequestSellRoomRpc(NetCoord coord)
        {
            // TrySellRoom already rejects a tile that isn't keeper 1's own.
            ClientCtx?.Lair?.TrySellRoom(coord.ToVector2Int());
        }

        [Rpc(SendTo.Server)]
        public void RequestBridgeTileRpc(NetCoord coord)
        {
            ClientCtx?.Bridge?.TryPlaceBridgeTile(coord.ToVector2Int());
        }

        [Rpc(SendTo.Server)]
        public void RequestToggleLairClaimRpc(NetCoord coord)
        {
            ClientCtx?.Lair?.ToggleLairClaim(coord.ToVector2Int());
        }

        [Rpc(SendTo.Server)]
        public void RequestPlaceRoomRpc(RoomDesignTool room, NetCoord start, NetCoord end)
        {
            var ctx = ClientCtx;
            if (ctx == null)
            {
                return;
            }

            var s = start.ToVector2Int();
            var e = end.ToVector2Int();
            switch (room)
            {
                case RoomDesignTool.Lair: ctx.Lair?.TryPlaceLair(s, e); break;
                case RoomDesignTool.Treasury: ctx.Treasury?.TryPlaceTreasury(s, e); break;
                case RoomDesignTool.SlimeHatchery: ctx.SlimeHatchery?.TryPlaceHatchery(s, e); break;
                case RoomDesignTool.Tavern: ctx.Tavern?.TryPlaceTavern(s, e); break;
                case RoomDesignTool.TrainingRoom: ctx.TrainingRoom?.TryPlaceTrainingRoom(s, e); break;
                case RoomDesignTool.Library: ctx.Library?.TryPlaceLibrary(s, e); break;
                case RoomDesignTool.Jail: ctx.Jail?.TryPlaceJail(s, e); break;
                case RoomDesignTool.ConversionClass: ctx.ConversionClass?.TryPlaceConversionClass(s, e); break;
            }
        }

        [Rpc(SendTo.Server)]
        public void RequestSetTerrainRpc(NetCoord coord, byte tileType)
        {
            if (_grid != null) _grid.DevPaintTerrain(coord.ToVector2Int(), (TileType)tileType);
        }

        [Rpc(SendTo.Server)]
        public void RequestSetBedrockRpc(NetCoord coord)
        {
            if (_grid != null) _grid.SetBedrock(coord.ToVector2Int());
        }

        [Rpc(SendTo.Server)]
        public void RequestSetDigJobsPausedRpc(bool paused)
        {
            ClientCtx?.JobBoard?.SetDigJobsPaused(paused);
        }

        [Rpc(SendTo.Server)]
        public void RequestSetAutoReinforceRpc(bool enabled)
        {
            ClientCtx?.JobBoard?.SetAutoReinforceEnabled(enabled);
        }

        [Rpc(SendTo.Server)]
        public void RequestRecruitRpc(EditorCreatureKind kind)
        {
            var ctx = ClientCtx;
            if (ctx == null)
            {
                return;
            }

            switch (kind)
            {
                case EditorCreatureKind.Gremlin: ctx.GremlinSpawner?.TryRecruitGremlin(); break;
                case EditorCreatureKind.Warlock: ctx.WarlockSpawner?.TryRecruitWarlock(); break;
                case EditorCreatureKind.MazeRattler: ctx.MazeRattlerSpawner?.TryRecruitMazeRattler(); break;
                case EditorCreatureKind.BeanCounter: ctx.BeanCounterSpawner?.TryRecruitBeanCounter(); break;
            }
        }
    }
}
