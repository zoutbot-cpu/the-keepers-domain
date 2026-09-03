using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Grid;

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

        /// Host only — called from GameBootstrap.BuildWorld right after the
        /// NetGame is spawned, once the grid exists.
        public void HostBind(DungeonGrid grid)
        {
            _grid = grid;
            MapWidth.Value = grid.Width;
            MapHeight.Value = grid.Height;
            _grid.TileChanged += OnTileChanged;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (IsServer)
            {
                return;
            }

            // Client: build the render-only world (creates the DungeonGrid),
            // then pull the current grid state from the host.
            NetSession.Instance?.OnClientReady?.Invoke();
            _grid = FindFirstObjectByType<DungeonGrid>();
            RequestSnapshotRpc();
        }

        public override void OnNetworkDespawn()
        {
            if (_grid != null)
            {
                _grid.TileChanged -= OnTileChanged;
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
            Apply(tiles);
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
            Apply(tiles);
        }

        private void Apply(NetTile[] tiles)
        {
            if (_grid == null)
            {
                _grid = FindFirstObjectByType<DungeonGrid>();
                if (_grid == null)
                {
                    return;
                }
            }

            foreach (var t in tiles)
            {
                _grid.ApplyReplicatedTile(t.Coord, t.ToTileState());
            }
        }
    }
}
